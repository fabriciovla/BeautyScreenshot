using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace BeautyScreenshot
{
    /// <summary>Build-time tool: writes the app icon (.ico) from IconArt. Usage: IconGen.exe out.ico</summary>
    static class IconGen
    {
        static void Main(string[] args)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = IconArt.AppIcon(sizes[i]))
                    frames[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);

            using (var w = new BinaryWriter(File.Create(args[0])))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte s = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(s);
                    w.Write(s);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
            }
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>Classic 32-bit icon frame: BITMAPINFOHEADER, bottom-up BGRA pixels, empty AND mask.</summary>
        static byte[] Dib(Bitmap bmp)
        {
            int s = bmp.Width;
            int maskStride = (s + 31) / 32 * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(s);
                w.Write(s * 2);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(0);
                w.Write(s * s * 4 + maskStride * s);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                for (int y = s - 1; y >= 0; y--)
                    for (int x = 0; x < s; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        w.Write(c.B);
                        w.Write(c.G);
                        w.Write(c.R);
                        w.Write(c.A);
                    }
                w.Write(new byte[maskStride * s]);
                return ms.ToArray();
            }
        }
    }
}
