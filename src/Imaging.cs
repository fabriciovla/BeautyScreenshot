using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace BeautyScreenshot
{
    static class Imaging
    {
        /// <summary>Grabs a region of the desktop (physical pixels) as a frozen WPF bitmap.</summary>
        public static BitmapSource Capture(Drawing.Rectangle area)
        {
            using (var bmp = new Drawing.Bitmap(area.Width, area.Height, DrawingImaging.PixelFormat.Format32bppRgb))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, Drawing.CopyPixelOperation.SourceCopy);

                var data = bmp.LockBits(new Drawing.Rectangle(0, 0, area.Width, area.Height),
                    DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
                try
                {
                    var src = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null,
                        data.Scan0, data.Stride * area.Height, data.Stride);
                    src.Freeze();
                    return src;
                }
                finally { bmp.UnlockBits(data); }
            }
        }

        /// <summary>
        /// Copies a region into its own BGRA bitmap (so the full screenshot can be released), optionally with
        /// antialiased transparent rounded corners. <paramref name="dpi"/> is written to the PNG, so apps that honour it
        /// show the image at the size it had on screen, as macOS does with Retina captures.
        /// </summary>
        public static BitmapSource Crop(BitmapSource source, Int32Rect rect, double radiusPx, double dpi)
        {
            int w = rect.Width, h = rect.Height, stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(rect, pixels, stride, 0);   // the source is Bgr32: its 4th byte is unused
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            double r = Math.Min(radiusPx, Math.Min(w, h) / 2.0);
            if (r >= 0.5) RoundCorners(pixels, w, h, r);

            var crop = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgra32, null, pixels, stride);
            crop.Freeze();
            return crop;
        }

        /// <summary>
        /// Antialiased transparent corners: each corner pixel gets the share of it (sampled 4×4) that lies inside the
        /// rounded rectangle. Only the corner squares are touched, so this is quick even for a whole screen.
        /// </summary>
        static void RoundCorners(byte[] pixels, int w, int h, double r)
        {
            int n = (int)Math.Ceiling(r);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int inside = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            double dx = r - (x + (sx + 0.5) / 4), dy = r - (y + (sy + 0.5) / 4);   // from the arc's centre
                            if (dx <= 0 || dy <= 0 || dx * dx + dy * dy <= r * r) inside++;
                        }
                    if (inside == 16) continue;
                    byte alpha = (byte)(inside * 255 / 16);
                    SetAlpha(pixels, w, x, y, alpha);
                    SetAlpha(pixels, w, w - 1 - x, y, alpha);
                    SetAlpha(pixels, w, x, h - 1 - y, alpha);
                    SetAlpha(pixels, w, w - 1 - x, h - 1 - y, alpha);
                }
        }

        static void SetAlpha(byte[] pixels, int w, int x, int y, byte alpha)
        {
            int i = (y * w + x) * 4;
            pixels[i + 3] = alpha;
            // Apps that ignore transparency then show white corners instead of black ones.
            if (alpha == 0) pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
        }

        static byte[] EncodePng(BitmapSource image)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        public static void SavePng(BitmapSource image, string path)
        {
            File.WriteAllBytes(path, EncodePng(image));
        }

        /// <summary>Encodes and writes in the background, so the confirm animation is not held up; reports failures on the UI thread.</summary>
        public static void SavePngAsync(BitmapSource image, string path, Action<string> failed)
        {
            var ui = Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                try { SavePng(image, path); }
                catch (Exception ex) { ui.BeginInvoke(new Action(() => failed(ex.Message))); }
            });
        }

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
        const uint CF_DIBV5 = 17, GMEM_MOVEABLE = 0x0002;

        /// <summary>
        /// Puts the image on the clipboard as PNG (browsers, Office, chat apps) and as a 32-bit DIBv5, both keeping
        /// transparency; Windows derives the plain bitmap formats from the DIBv5 for older apps. The encoding, the
        /// slow part, runs in the background; the clipboard is then filled on the UI thread and
        /// <paramref name="done"/> says whether it worked.
        /// </summary>
        public static void CopyToClipboardAsync(BitmapSource image, IntPtr owner, Action<bool> done)
        {
            var ui = Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                byte[] png = null, dib = null;
                try
                {
                    png = EncodePng(image);
                    dib = DibV5(image);
                }
                catch (Exception ex) { Log.Error(ex); }
                ui.BeginInvoke(new Action(() => done(png != null && Publish(png, dib, owner))));
            });
        }

        static bool Publish(byte[] png, byte[] dib, IntPtr owner)
        {
            uint pngFormat = RegisterClipboardFormat("PNG");

            // Another app may be holding the clipboard open for a moment.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (!OpenClipboard(owner)) { Thread.Sleep(40); continue; }
                try
                {
                    EmptyClipboard();
                    return Put(CF_DIBV5, dib) & Put(pngFormat, png);
                }
                finally { CloseClipboard(); }
            }
            return false;
        }

        static bool Put(uint format, byte[] data)
        {
            IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (mem == IntPtr.Zero) return false;
            Marshal.Copy(data, 0, GlobalLock(mem), data.Length);
            GlobalUnlock(mem);
            if (SetClipboardData(format, mem) != IntPtr.Zero) return true;   // the clipboard owns the memory now
            GlobalFree(mem);
            return false;
        }

        /// <summary>BITMAPV5HEADER + bottom-up BGRA pixels with straight alpha, tagged as sRGB.</summary>
        static byte[] DibV5(BitmapSource image)
        {
            var bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            int pelsPerMeter = (int)Math.Round(image.DpiX / 0.0254);

            using (var ms = new MemoryStream(124 + pixels.Length))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(124);                 // bV5Size
                bw.Write(w);
                bw.Write(h);                   // positive: bottom-up rows
                bw.Write((short)1);            // planes
                bw.Write((short)32);           // bits per pixel
                bw.Write(3);                   // BI_BITFIELDS
                bw.Write(pixels.Length);
                bw.Write(pelsPerMeter);
                bw.Write(pelsPerMeter);
                bw.Write(0);                   // colors used
                bw.Write(0);                   // colors important
                bw.Write(0x00FF0000);          // red mask
                bw.Write(0x0000FF00);          // green mask
                bw.Write(0x000000FF);          // blue mask
                bw.Write(unchecked((int)0xFF000000));   // alpha mask
                bw.Write(0x73524742);          // LCS_sRGB
                bw.Write(new byte[36]);        // endpoints (unused for sRGB)
                bw.Write(new byte[12]);        // gamma (unused for sRGB)
                bw.Write(4);                   // LCS_GM_IMAGES
                bw.Write(0);                   // profile data
                bw.Write(0);                   // profile size
                bw.Write(0);                   // reserved
                for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
                return ms.ToArray();
            }
        }

        /// <summary>Shows the save dialog owned by <paramref name="owner"/>; returns the chosen path or null.</summary>
        public static string AskSavePath(Window owner)
        {
            string dir = Settings.SaveDir;
            var dialog = new SaveFileDialog
            {
                Title = L.T("Guardar captura", "Save screenshot"),
                Filter = L.T("Imagen PNG (*.png)|*.png", "PNG image (*.png)|*.png"),
                DefaultExt = ".png",
                AddExtension = true,
                FileName = Settings.SuggestedFileName(),
                InitialDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(owner) != true) return null;
            Settings.SaveDir = Path.GetDirectoryName(dialog.FileName);
            Settings.AdvanceCounter();
            return dialog.FileName;
        }
    }
}
