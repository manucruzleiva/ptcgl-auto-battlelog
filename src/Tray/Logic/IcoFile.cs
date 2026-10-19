using System;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Picks a frame out of a .ico file. The status icons are drawn from the PNG frame closest to
    /// the tray's icon size: decoding the PNG keeps its alpha, which System.Drawing.Icon does not
    /// always do for PNG-compressed frames.
    /// </summary>
    public static class IcoFile
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// The PNG bytes of the smallest frame at least <paramref name="size"/> wide, or of the
        /// largest frame when none is that big. False when the file is not an icon, the chosen frame
        /// is not PNG, or the directory points outside the file.
        /// </summary>
        public static bool TryGetPngFrame(byte[] ico, int size, out byte[] png, out int frameSize)
        {
            png = null;
            frameSize = 0;
            if (ico == null || ico.Length < 6 || size <= 0) return false;
            if (U16(ico, 0) != 0 || U16(ico, 2) != 1) return false;   // reserved, type 1 = icon
            int count = U16(ico, 4);
            if (count == 0 || ico.Length < 6 + 16 * count) return false;

            int best = -1, bestSize = 0;
            for (int i = 0; i < count; i++)
            {
                int w = ico[6 + 16 * i];
                if (w == 0) w = 256;
                bool better = best < 0 ||
                              (bestSize < size ? w > bestSize : w >= size && w < bestSize);
                if (better) { best = i; bestSize = w; }
            }

            long length = U32(ico, 6 + 16 * best + 8);
            long offset = U32(ico, 6 + 16 * best + 12);
            if (offset < 6 + 16 * count || length < PngSignature.Length || offset + length > ico.Length) return false;
            for (int i = 0; i < PngSignature.Length; i++)
                if (ico[offset + i] != PngSignature[i]) return false;

            png = new byte[length];
            Array.Copy(ico, offset, png, 0, length);
            frameSize = bestSize;
            return true;
        }

        public static bool TryGetPngFrame(Stream ico, int size, out byte[] png, out int frameSize)
        {
            png = null;
            frameSize = 0;
            if (ico == null) return false;
            using (var ms = new MemoryStream())
            {
                ico.CopyTo(ms);
                return TryGetPngFrame(ms.ToArray(), size, out png, out frameSize);
            }
        }

        private static int U16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
        private static long U32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
    }
}
