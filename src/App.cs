using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("BeautyScreenshot")]
[assembly: AssemblyProduct("BeautyScreenshot")]
[assembly: AssemblyDescription("Capturas de pantalla simples y lindas")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace BeautyScreenshot
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (Array.Exists(args, a => a.Equals("--desinstalar", StringComparison.OrdinalIgnoreCase)))
            {
                Uninstall.Run();
                return;
            }

            bool wantsSettings = Array.Exists(args, a => a.Equals("--ajustes", StringComparison.OrdinalIgnoreCase));
            using (var instance = new Mutex(true, InstallInfo.InstanceMutex, out bool first))
            using (var captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BeautyScreenshot.Capture"))
            using (var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BeautyScreenshot.Settings"))
            using (var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.ExitEvent))
            {
                if (!first)
                {
                    // Already running: opening the exe again starts a capture (or opens Ajustes) in the running instance.
                    (wantsSettings ? settingsSignal : captureSignal).Set();
                    return;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var tray = new TrayApp();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Error(e.Exception);
                    tray.Recover();
                    e.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error(e.ExceptionObject as Exception);
                app.Startup += (s, e) => tray.Start(captureSignal, settingsSignal, exitSignal, wantsSettings);
                app.Run();
                GC.KeepAlive(instance);
            }
        }
    }

    sealed class TrayApp
    {
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        Forms.NotifyIcon tray;
        Forms.ContextMenuStrip menu;
        Forms.ToolStripMenuItem captureItem, autoStartItem;
        Drawing.Icon icon;
        KeyboardHook hook;
        CaptureSession session;
        DispatcherTimer trimTimer;

        public void Start(EventWaitHandle captureSignal, EventWaitHandle settingsSignal, EventWaitHandle exitSignal, bool openSettings)
        {
            hook = new KeyboardHook(Settings.Hotkey, () => BeginCapture(0));
            hook.Intercept = (vk, mods) => session != null && session.OnKey(vk, mods);
            BuildTray();

            // The exit signal comes from the setup (to replace the exe) and from uninstalling.
            var signals = new WaitHandle[] { captureSignal, settingsSignal, exitSignal };
            var listener = new Thread(() =>
            {
                while (true)
                {
                    switch (WaitHandle.WaitAny(signals))
                    {
                        case 0: dispatcher.BeginInvoke(new Action(() => BeginCapture(0))); break;
                        case 1: dispatcher.BeginInvoke(new Action(OpenSettings)); break;
                        default: dispatcher.BeginInvoke(new Action(Exit)); return;
                    }
                }
            }) { IsBackground = true, Name = "Second instance listener" };
            listener.Start();
            if (openSettings) OpenSettings();

            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General) dispatcher.BeginInvoke(new Action(ApplyTheme));
            };

            // Have the capture overlays ready (and rebuilt when monitors change), so the shortcut shows them instantly.
            Native.StayResponsive();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(CaptureSession.Prepare));
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShutterSound.Prepare));
            SystemEvents.DisplaySettingsChanged += (s, e) => dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (session != null) return;   // the next capture drops overlays of monitors that changed
                CaptureSession.Reset();
                CaptureSession.Prepare();
            }));

            Settings.RefreshAutoStartPath();
            if (Settings.ShowToasts || !hook.Installed)
                Toast.Show(Forms.Screen.PrimaryScreen, L.T("Beauty Screenshot está activo", "Beauty Screenshot is running"),
                    hook.Installed
                        ? L.T("Presioná " + hook.Hotkey + " para capturar", "Press " + hook.Hotkey + " to capture")
                        : L.T("Hacé clic en el ícono de la bandeja para capturar", "Click the tray icon to capture"));
            ScheduleTrim(4000);
        }

        // ---------------------------------------------------------------- tray

        void BuildTray()
        {
            menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = new Drawing.Font("Segoe UI", 9f) };

            captureItem = Item(L.T("Capturar", "Capture"), (s, e) => BeginCapture(250));
            captureItem.Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);

            autoStartItem = Item(L.T("Iniciar con Windows", "Start with Windows"), (s, e) =>
            {
                Settings.StartWithWindows = !Settings.StartWithWindows;
                autoStartItem.Checked = Settings.StartWithWindows;
            });
            menu.Opening += (s, e) => autoStartItem.Checked = Settings.StartWithWindows;   // may have changed in Ajustes

            menu.Items.Add(captureItem);
            menu.Items.Add(Item(L.T("Abrir carpeta de capturas", "Open screenshots folder"), (s, e) => OpenFolder(Settings.SaveDir)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item(L.T("Ajustes…", "Settings…"), (s, e) => OpenSettings()));
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item(L.T("Salir", "Quit"), (s, e) => Exit()));

            tray = new Forms.NotifyIcon { ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) BeginCapture(150); };
            ShowHotkey();
            ApplyTheme();
            tray.Visible = true;
        }

        void OpenSettings() => SettingsWindow.ShowOrActivate(hook, ShowHotkey);

        void ShowHotkey()
        {
            string keys = hook.Hotkey.ToString();
            captureItem.ShortcutKeyDisplayString = keys;
            string text = "BeautyScreenshot · " + L.T(keys + " para capturar", keys + " to capture");
            tray.Text = text.Length < 64 ? text : "BeautyScreenshot";
        }

        static Forms.ToolStripMenuItem Item(string text, EventHandler click)
        {
            var item = new Forms.ToolStripMenuItem(text) { Padding = new Forms.Padding(0, 4, 0, 4) };
            item.Click += click;
            return item;
        }

        void ApplyTheme()
        {
            bool light = Theme.SystemLight;
            var old = icon;
            int size = Forms.SystemInformation.SmallIconSize.Width;
            using (var glyph = IconArt.TrayGlyph(size, light))
                icon = OwnedIcon.FromBitmap(glyph);
            tray.Icon = icon;
            old?.Dispose();

            menu.Renderer = new NeutralMenuRenderer(light);
            menu.BackColor = NeutralMenuRenderer.Background(light);
            menu.ForeColor = NeutralMenuRenderer.Foreground(light);
        }

        static void OpenFolder(string dir)
        {
            if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void Exit()
        {
            hook?.Dispose();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        // ---------------------------------------------------------------- capture

        void BeginCapture(int delayMs)
        {
            if (session != null) return;
            // Anything of ours still on screen (the tray menu) must be gone before the screenshot.
            if (delayMs > 0) { Ui.After(delayMs, () => BeginCapture(0)); return; }

            // A toast or thumbnail is removed at once; waiting for one compositor frame (~16 ms) keeps it out of the shot.
            bool hadToast = Toast.CloseCurrent();
            if (CaptureThumbnail.StepAsideForCapture() | hadToast) Native.WaitForCompositor();

            trimTimer?.Stop();
            session = new CaptureSession();
            session.Finished += OnFinished;
            session.Start();
        }

        void OnFinished(CaptureResult result)
        {
            session = null;
            if (result == null)
            {
                CaptureThumbnail.Restore();   // cancelled: the pile that was hidden for the capture comes back
            }
            else
            {
                // The capture has just landed in the corner: it goes on top of the thumbnail pile there. With
                // notifications off it slides away at once; problems are always reported, with details in a toast.
                // (Clipboard and file failures, which happen in the background, raise their own toast.)
                string label;
                bool ok = result.Error == null;
                if (!ok)
                {
                    label = L.T("No se pudo guardar", "Couldn't save");
                    Toast.Show(result.Screen, label, result.Error);
                }
                else if (result.SavedPath != null) label = result.Copied ? L.T("Guardada y copiada", "Saved and copied") : L.T("Guardada", "Saved");
                else label = L.T("Copiada", "Copied");

                string path = result.SavedPath;
                Action open = path != null ? () => Process.Start("explorer.exe", "/select,\"" + path + "\"") : (Action)null;
                CaptureThumbnail.Add(result.Screen, result.Image, label, ok, open, Settings.ShowToasts || !ok ? 3000 : 0);
            }
            ScheduleTrim(result != null ? 4500 : 300);
        }

        /// <summary>Once idle, hand the capture memory back to Windows.</summary>
        void ScheduleTrim(int delayMs)
        {
            trimTimer?.Stop();
            trimTimer = new DispatcherTimer { Interval = Ui.Ms(delayMs) };
            trimTimer.Tick += (s, e) =>
            {
                trimTimer.Stop();
                if (session == null) Native.TrimMemory();
            };
            trimTimer.Start();
        }

        /// <summary>After an unexpected error, make sure no overlay is left covering the screen.</summary>
        public void Recover()
        {
            var stuck = session;
            session = null;
            try { stuck?.Abort(); } catch (Exception ex) { Log.Error(ex); }
        }
    }

    /// <summary>Icon built from a bitmap that releases its native handle when disposed.</summary>
    static class OwnedIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static Drawing.Icon FromBitmap(Drawing.Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            using (var temp = Drawing.Icon.FromHandle(h))
            {
                var copy = (Drawing.Icon)temp.Clone();
                DestroyIcon(h);
                return copy;
            }
        }
    }

    /// <summary>Flat, neutral tray menu in the taskbar's light or dark theme.</summary>
    sealed class NeutralMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        readonly bool light;

        public NeutralMenuRenderer(bool light) : base(new Palette(light))
        {
            this.light = light;
            RoundedEdges = false;
        }

        public static Drawing.Color Background(bool light) => light ? Drawing.Color.FromArgb(0xF9, 0xF9, 0xF9) : Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B);
        public static Drawing.Color Foreground(bool light) => light ? Drawing.Color.FromArgb(0x1B, 0x1B, 0x1B) : Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2);
        static Drawing.Color Hover(bool light) => light ? Drawing.Color.FromArgb(0xEA, 0xEA, 0xEA) : Drawing.Color.FromArgb(0x3D, 0x3D, 0x3D);
        static Drawing.Color Muted(bool light) => light ? Drawing.Color.FromArgb(0x70, 0x70, 0x70) : Drawing.Color.FromArgb(0xA0, 0xA0, 0xA0);

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Foreground(light) : Muted(light);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = Math.Min(r.Width, r.Height) / 8f;
            using (var pen = new Drawing.Pen(Foreground(light), Math.Max(1.4f, u * 0.9f))
            { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round, LineJoin = Drawing.Drawing2D.LineJoin.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new Drawing.PointF(cx - 2.6f * u, cy + 0.1f * u),
                    new Drawing.PointF(cx - 0.8f * u, cy + 1.9f * u),
                    new Drawing.PointF(cx + 2.8f * u, cy - 1.9f * u),
                });
            }
        }

        sealed class Palette : Forms.ProfessionalColorTable
        {
            readonly bool light;
            public Palette(bool light) { this.light = light; UseSystemColors = false; }

            Drawing.Color Bg => Background(light);
            Drawing.Color Sel => Hover(light);
            Drawing.Color Line => light ? Drawing.Color.FromArgb(0xE0, 0xE0, 0xE0) : Drawing.Color.FromArgb(0x45, 0x45, 0x45);

            public override Drawing.Color ToolStripDropDownBackground => Bg;
            public override Drawing.Color ImageMarginGradientBegin => Bg;
            public override Drawing.Color ImageMarginGradientMiddle => Bg;
            public override Drawing.Color ImageMarginGradientEnd => Bg;
            public override Drawing.Color MenuBorder => Line;
            public override Drawing.Color MenuItemBorder => Sel;
            public override Drawing.Color MenuItemSelected => Sel;
            public override Drawing.Color MenuItemSelectedGradientBegin => Sel;
            public override Drawing.Color MenuItemSelectedGradientEnd => Sel;
            public override Drawing.Color MenuItemPressedGradientBegin => Sel;
            public override Drawing.Color MenuItemPressedGradientEnd => Sel;
            public override Drawing.Color SeparatorDark => Line;
            public override Drawing.Color SeparatorLight => Bg;
            public override Drawing.Color CheckBackground => Bg;
            public override Drawing.Color CheckSelectedBackground => Sel;
            public override Drawing.Color CheckPressedBackground => Sel;
        }
    }

    static class Log
    {
        public static void Error(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyScreenshot");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
