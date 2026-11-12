using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PummelPartyMcp.Core
{
    /// <summary>Per-install shared secret: 32 CSPRNG bytes as 64 lowercase hex chars.</summary>
    public static class TokenAuth
    {
        public const string HeaderName = "X-PummelPartyMcp-Token";
        private const int TokenBytes = 32;

        /// <summary>
        /// Reads the token file, or creates it (and its directory) if missing or malformed. The file is never written in
        /// place: the token goes to a temp file in the same directory which is then moved (missing target) or replaced
        /// (malformed target) atomically, so a reader never sees a half-written token. Starters are serialised with a
        /// named mutex, and a lost race (move/replace failing with IOException or a sharing violation) never throws:
        /// the temp file is deleted, the file is re-read with a short bounded retry and the winner's token is used
        /// (<paramref name="created"/> false). After a successful write the file is re-read and what it holds is returned,
        /// so all callers agree with the file. The file's ACL is not changed (see docs/plugin-api.md, Safety).
        /// </summary>
        public static string LoadOrCreate(string path, out bool created)
        {
            created = false;
            string existing = TryRead(path);
            if (existing != null)
            {
                return existing;
            }

            Mutex gate = null;
            bool locked = false;
            try
            {
                gate = new Mutex(false, "PummelPartyMcp.Token." + PathKey(path));
                try { locked = gate.WaitOne(5000); }
                catch (AbandonedMutexException) { locked = true; }
            }
            catch (Exception)
            {
                gate = null; // named mutexes unavailable: the lock-free retry logic below still copes with races
            }
            try
            {
                return CreateLocked(path, out created);
            }
            finally
            {
                if (gate != null)
                {
                    if (locked)
                    {
                        try { gate.ReleaseMutex(); } catch (Exception) { }
                    }
                    gate.Dispose();
                }
            }
        }

        private static string PathKey(string path)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    sb.Append(h[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static string CreateLocked(string path, out bool created)
        {
            created = false;
            string existing = TryRead(path);
            if (existing != null)
            {
                return existing;
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Exception last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = Generate();
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, token, Encoding.ASCII);
                    if (File.Exists(path))
                    {
                        ReplaceFile(temp, path); // malformed file
                    }
                    else
                    {
                        File.Move(temp, path); // fails if another process created the file meanwhile
                    }
                    // Re-read: use what is on disk so every starter agrees with the file.
                    string onDisk = ReadWithRetry(path);
                    if (onDisk != null)
                    {
                        created = onDisk == token;
                        return onDisk;
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    last = ex; // sharing violation while another starter holds the file
                }
                finally
                {
                    try { File.Delete(temp); } catch (Exception) { /* already moved, or best effort */ }
                }

                // Lost a race (or target momentarily locked / mid-replace): a valid token from the winner wins.
                string other = ReadWithRetry(path);
                if (other != null)
                {
                    created = false;
                    return other;
                }
            }
            throw new IOException("Could not create or read a valid token file at '" + path + "' after retrying.", last);
        }

        /// <summary>Bounded retry (~1 s) for a file that may be briefly locked or mid-replace.</summary>
        private static string ReadWithRetry(string path)
        {
            for (int i = 0; i < 40; i++)
            {
                string t = TryRead(path);
                if (t != null)
                {
                    return t;
                }
                Thread.Sleep(i < 10 ? 5 : 25);
            }
            return null;
        }

        /// <summary>The well-formed token in the file, or null (missing, unreadable or malformed).</summary>
        private static string TryRead(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                string text = File.ReadAllText(path, Encoding.ASCII).Trim();
                return IsWellFormed(text) ? text : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void ReplaceFile(string temp, string path)
        {
            try
            {
                File.Replace(temp, path, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temp, path, true); // runtime without File.Replace: not atomic, but still a complete file
            }
            catch (NotImplementedException)
            {
                File.Copy(temp, path, true);
            }
        }

        public static string Generate()
        {
            byte[] bytes = new byte[TokenBytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            StringBuilder sb = new StringBuilder(TokenBytes * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        public static bool IsWellFormed(string token)
        {
            if (token == null || token.Length != TokenBytes * 2)
            {
                return false;
            }
            foreach (char c in token)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Constant-time comparison (time depends only on the expected token's length).</summary>
        public static bool FixedTimeEquals(string expected, string provided)
        {
            if (expected == null)
            {
                return false;
            }
            byte[] a = Encoding.UTF8.GetBytes(expected);
            byte[] b = Encoding.UTF8.GetBytes(provided ?? string.Empty);
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length; i++)
            {
                byte other = i < b.Length ? b[i] : (byte)0;
                diff |= a[i] ^ other;
            }
            return diff == 0;
        }
    }
}
