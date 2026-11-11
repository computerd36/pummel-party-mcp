using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PummelBridge.Core
{
    /// <summary>Per-install shared secret: 32 CSPRNG bytes as 64 lowercase hex chars.</summary>
    public static class TokenAuth
    {
        public const string HeaderName = "X-PummelBridge-Token";
        private const int TokenBytes = 32;

        /// <summary>
        /// Reads the token file, or creates it (and its directory) if missing or malformed. The file is never written in
        /// place: the token goes to a temp file in the same directory which is then moved (missing target) or replaced
        /// (malformed target) atomically, so a reader never sees a half-written token. Two processes starting at once
        /// converge: the one whose move loses re-reads the file and uses the winner's token (<paramref name="created"/>
        /// false). The file's ACL is not changed (see docs/bridge-api.md, Safety).
        /// </summary>
        public static string LoadOrCreate(string path, out bool created)
        {
            created = false;
            string existing = TryRead(path);
            if (existing != null)
            {
                return existing;
            }

            string token = Generate();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, token, Encoding.ASCII);
                try
                {
                    if (File.Exists(path))
                    {
                        ReplaceFile(temp, path); // malformed file
                    }
                    else
                    {
                        File.Move(temp, path); // fails if another process created the file meanwhile
                    }
                }
                catch (IOException)
                {
                    // Lost a race (or the target was locked for a moment): a valid token written by someone else wins.
                    string other = TryRead(path);
                    if (other != null)
                    {
                        return other;
                    }
                    throw;
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { /* already moved, or best effort */ }
            }

            // Re-read: if another process replaced the file after us, use what is on disk now so both sides agree.
            string onDisk = TryRead(path);
            if (onDisk != null && onDisk != token)
            {
                return onDisk;
            }
            created = true;
            return token;
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
