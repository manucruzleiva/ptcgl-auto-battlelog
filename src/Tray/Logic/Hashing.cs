using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PtcglAutoBattleLog.Tray.Logic
{
    internal static class Hashing
    {
        internal static byte[] Sha256(Stream s)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(s);
        }

        /// <summary>Hash of a file that may be open in the game (a loaded plugin is shared for reading).</summary>
        internal static byte[] Sha256File(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Sha256(fs);
        }

        internal static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Exactly 64 ASCII hex digits, either case. Anything else is not a SHA-256.</summary>
        internal static bool TryParseSha256(string hex, out byte[] bytes)
        {
            bytes = null;
            if (hex == null || hex.Length != 64) return false;
            var result = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                int hi = HexValue(hex[2 * i]), lo = HexValue(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return false;
                result[i] = (byte)((hi << 4) | lo);
            }
            bytes = result;
            return true;
        }

        internal static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
