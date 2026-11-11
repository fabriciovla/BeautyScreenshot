using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace BeautyScreenshot
{
    /// <summary>The app icon (from AppIcon.png) and the tray glyph: four viewfinder corners.</summary>
    static class IconArt
    {
        /// <summary>The app icon, scaled from the AppIcon.png (256 px) embedded in the assembly.</summary>
        public static Bitmap AppIcon(int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.png"))
            using (var source = new Bitmap(stream))
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }

        public static Bitmap TrayGlyph(int size, bool lightTaskbar)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var color = lightTaskbar ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.FromArgb(0xF5, 0xF5, 0xF5);
                DrawGlyph(g, size, size * 0.1f, size * 0.3f, Math.Max(1.5f, size * 0.1f), color);
            }
            return bmp;
        }

        static void DrawGlyph(Graphics g, int size, float pad, float len, float width, Color color)
        {
            float a = pad + width / 2, b = size - pad - width / 2;
            using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] { new PointF(a, a + len), new PointF(a, a), new PointF(a + len, a) });
                g.DrawLines(pen, new[] { new PointF(b - len, a), new PointF(b, a), new PointF(b, a + len) });
                g.DrawLines(pen, new[] { new PointF(b, b - len), new PointF(b, b), new PointF(b - len, b) });
                g.DrawLines(pen, new[] { new PointF(a + len, b), new PointF(a, b), new PointF(a, b - len) });
            }
        }
    }
}
