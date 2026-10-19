using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The tray icon in its four states, drawn once at start from the embedded app.ico: the plain
    /// icon, or the icon with a green, amber or red dot in the corner. Every HICON from GetHicon
    /// is destroyed right after it is copied into an Icon that owns its own handle.
    /// </summary>
    internal sealed class StatusIcons : IDisposable
    {
        private readonly Icon _idle, _active, _attention, _error;

        public StatusIcons()
        {
            var size = SystemInformation.SmallIconSize;
            using (var art = LoadArt(size.Width))
            {
                _idle = Draw(art, size, null);
                _active = Draw(art, size, Color.FromArgb(0x2E, 0xB8, 0x4F));
                _attention = Draw(art, size, Color.FromArgb(0xF5, 0xA6, 0x23));
                _error = Draw(art, size, Color.FromArgb(0xE0, 0x3A, 0x3A));
            }
        }

        public Icon For(TrayHealth health)
        {
            switch (health)
            {
                case TrayHealth.Active: return _active;
                case TrayHealth.Attention: return _attention;
                case TrayHealth.Error: return _error;
                default: return _idle;
            }
        }

        /// <summary>The PNG frame nearest the tray size (keeps its alpha); System.Drawing's own pick otherwise.</summary>
        private static Bitmap LoadArt(int size)
        {
            byte[] ico;
            using (var s = Embedded.Open(Embedded.IconResource))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                ico = ms.ToArray();
            }
            if (IcoFile.TryGetPngFrame(ico, size, out var png, out _))
            {
                try
                {
                    // Bitmap keeps reading its stream: copy it into a bitmap that owns its pixels.
                    using (var ms = new MemoryStream(png))
                    using (var decoded = new Bitmap(ms))
                        return new Bitmap(decoded);
                }
                catch (Exception e) { Log.Error("Could not decode the icon's PNG frame", e); }
            }
            using (var ms = new MemoryStream(ico))
            using (var icon = new Icon(ms, size, size))
                return icon.ToBitmap();
        }

        private static Icon Draw(Bitmap art, Size size, Color? dot)
        {
            using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawImage(art, new Rectangle(0, 0, size.Width, size.Height));
                    if (dot.HasValue)
                    {
                        // Half the icon, bottom-right, with a white ring so it reads on dark and light taskbars.
                        float d = size.Width * 0.5f, ring = Math.Max(1f, size.Width / 16f);
                        var r = new RectangleF(size.Width - d - ring / 2, size.Height - d - ring / 2, d, d);
                        using (var fill = new SolidBrush(dot.Value)) g.FillEllipse(fill, r);
                        using (var pen = new Pen(Color.White, ring)) g.DrawEllipse(pen, r);
                    }
                }
                var hicon = bmp.GetHicon();
                try
                {
                    using (var borrowed = Icon.FromHandle(hicon))
                        return (Icon)borrowed.Clone();   // CopyImage: an icon that owns (and frees) its own handle
                }
                finally { NativeMethods.DestroyIcon(hicon); }
            }
        }

        public void Dispose()
        {
            _idle?.Dispose();
            _active?.Dispose();
            _attention?.Dispose();
            _error?.Dispose();
        }
    }
}
