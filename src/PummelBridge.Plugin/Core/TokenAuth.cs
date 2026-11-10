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

        /// <summary>Reads the token file, or creates it (and its directory) if missing or malformed.</summary>
        public static string LoadOrCreate(string path, out bool created)
        {
            created = false;
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path, Encoding.ASCII).Trim();
                if (IsWellFormed(existing))
                {
                    return existing;
                }
            }

            string token = Generate();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, token, Encoding.ASCII);
            created = true;
            return token;
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
