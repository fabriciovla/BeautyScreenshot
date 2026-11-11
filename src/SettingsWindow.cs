using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BeautyScreenshot
{
    /// <summary>
    /// Settings window. The layout lives in SettingsWindow.xaml (embedded and parsed at runtime);
    /// this class fills it in and saves every change as soon as it is made.
    /// </summary>
    sealed class SettingsWindow : Window
    {
        static SettingsWindow open;

        /// <summary>The fixed texts of SettingsWindow.xaml, which is written in English, for Windows in Spanish.</summary>
        static readonly Dictionary<string, string> Spanish = new Dictionary<string, string>
        {
            ["Settings"] = "Ajustes",
            ["Capture"] = "Captura",
            ["Keyboard shortcut"] = "Atajo de teclado",
            ["Combination that starts a capture"] = "Combinación que abre la captura",
            ["Enter and double-click"] = "Enter y doble clic",
            ["What to do when you confirm without the toolbar. For both, choose Save and turn on “Also copy when saving”."] =
                "Qué hacer al confirmar sin usar la barra. Para las dos cosas, elegí Guardar y activá «Copiar también al guardar».",
            ["Copy"] = "Copiar",
            ["Save"] = "Guardar",
            ["Saving"] = "Guardado",
            ["Folder"] = "Carpeta",
            ["Change…"] = "Cambiar…",
            ["Save without asking"] = "Guardar sin preguntar",
            ["Saves straight to the folder, without opening the dialog"] =
                "Guarda directo en la carpeta, sin abrir el cuadro de diálogo",
            ["File name"] = "Nombre de archivo",
            ["You can use {date}, {time} and {n} (a running number)"] =
                "Podés usar {fecha}, {hora} y {n} (número correlativo)",
            ["Also copy when saving"] = "Copiar también al guardar",
            ["Besides the file, puts the image on the clipboard"] =
                "Además del archivo, deja la imagen en el portapapeles",
            ["Appearance"] = "Apariencia",
            ["Rounded corners"] = "Esquinas redondeadas",
            ["Rounds the selection and the final image, with transparent corners"] =
                "Redondea la selección y la imagen final, con esquinas transparentes",
            ["Dimming"] = "Oscurecido",
            ["How dark the screen gets while you select"] = "Cuánto se oscurece la pantalla mientras seleccionás",
            ["Show hint"] = "Mostrar ayuda",
            ["A short hint at the bottom of the screen when a capture starts"] =
                "Una indicación breve abajo de la pantalla al empezar a capturar",
            ["Show notifications"] = "Mostrar notificaciones",
            ["A notice with a thumbnail after copying or saving"] = "Aviso con miniatura después de copiar o guardar",
            ["Capture sound"] = "Sonido al capturar",
            ["A soft shutter sound when copying or saving"] = "Un sonido de captura suave al copiar o guardar",
            ["General"] = "General",
            ["Start with Windows"] = "Iniciar con Windows",
            ["Waits in the system tray as soon as the PC starts"] =
                "Queda lista en la bandeja del sistema al encender la PC",
        };

        readonly KeyboardHook hook;
        readonly Action hotkeyChanged;
        readonly FrameworkElement root;
        readonly Button hotkeyButton;
        readonly TextBlock hotkeyText, hotkeyNote;
        bool recording;

        public static void ShowOrActivate(KeyboardHook hook, Action hotkeyChanged)
        {
            if (open == null)
            {
                open = new SettingsWindow(hook, hotkeyChanged);
                open.Closed += (s, e) => open = null;
                open.Show();
            }
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            Native.ForceForeground(new WindowInteropHelper(open).Handle);   // we live in the background, so Activate alone is refused
            open.Activate();
        }

        SettingsWindow(KeyboardHook hook, Action hotkeyChanged)
        {
            this.hook = hook;
            this.hotkeyChanged = hotkeyChanged;

            Title = L.T("Ajustes · BeautyScreenshot", "Settings · BeautyScreenshot");
            Width = 640;
            Height = 740;
            MinWidth = 520;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            Icon = AppIcon();
            ApplyPalette();

            using (var xaml = typeof(SettingsWindow).Assembly.GetManifestResourceStream("SettingsWindow.xaml"))
                root = (FrameworkElement)XamlReader.Load(xaml);
            L.Translate(root, Spanish);
            Content = root;

            // Capture
            hotkeyButton = Find<Button>("HotkeyButton");
            hotkeyText = Find<TextBlock>("HotkeyText");
            hotkeyNote = Find<TextBlock>("HotkeyNote");
            hotkeyText.Text = Settings.Hotkey.ToString();
            hotkeyButton.Click += (s, e) => { if (recording) EndRecording(); else StartRecording(); };

            var copy = Find<RadioButton>("ActionCopy");
            var save = Find<RadioButton>("ActionSave");
            (Settings.DefaultAction == CaptureAction.Save ? save : copy).IsChecked = true;
            copy.Checked += (s, e) => Settings.DefaultAction = CaptureAction.Copy;
            save.Checked += (s, e) => Settings.DefaultAction = CaptureAction.Save;

            // Saving
            var folderText = Find<TextBlock>("FolderText");
            folderText.Text = Settings.SaveDir;
            folderText.ToolTip = Settings.SaveDir;
            Find<Button>("FolderButton").Click += (s, e) =>
            {
                string dir = FolderPicker.Pick(new WindowInteropHelper(this).Handle, L.T("Carpeta para las capturas", "Folder for screenshots"), Settings.SaveDir);
                if (dir == null) return;
                Settings.SaveDir = dir;
                folderText.Text = dir;
                folderText.ToolTip = dir;
            };

            Bind("QuickSave", Settings.QuickSave, v => Settings.QuickSave = v);
            Bind("CopyOnSave", Settings.CopyOnSave, v => Settings.CopyOnSave = v);

            var pattern = Find<TextBox>("NamePattern");
            var preview = Find<TextBlock>("NamePreview");
            Action showPreview = () => preview.Text = L.T("Ej.: ", "E.g. ") + Settings.SuggestedFileName() + ".png";
            pattern.Text = Settings.FileNamePattern;
            showPreview();
            pattern.TextChanged += (s, e) =>
            {
                Settings.FileNamePattern = string.IsNullOrWhiteSpace(pattern.Text) ? Settings.DefaultPattern : pattern.Text;
                showPreview();
            };

            // Appearance
            var radius = Find<Slider>("RadiusSlider");
            var radiusValue = Find<TextBlock>("RadiusValue");
            Func<int, string> radiusLabel = r => r == 0 ? L.T("Rectas", "Square") : r + " px";
            radius.Value = Settings.CornerRadius;
            radiusValue.Text = radiusLabel(Settings.CornerRadius);
            radius.ValueChanged += (s, e) =>
            {
                int r = (int)Math.Round(radius.Value);
                radiusValue.Text = radiusLabel(r);
                Settings.CornerRadius = r;
            };

            var dim = Find<Slider>("DimSlider");
            var dimValue = Find<TextBlock>("DimValue");
            dim.Value = Settings.DimPercent;
            dimValue.Text = Settings.DimPercent + " %";
            dim.ValueChanged += (s, e) =>
            {
                int percent = (int)Math.Round(dim.Value);
                dimValue.Text = percent + " %";
                Settings.DimPercent = percent;
            };

            Bind("ShowHint", Settings.ShowHint, v => Settings.ShowHint = v);
            Bind("ShowToasts", Settings.ShowToasts, v => Settings.ShowToasts = v);
            Bind("PlaySound", Settings.PlaySound, v => { Settings.PlaySound = v; if (v) ShutterSound.Play(sample: true); });   // a sample when turned on

            // General
            Bind("AutoStart", Settings.StartWithWindows, v => Settings.StartWithWindows = v);

            var version = typeof(SettingsWindow).Assembly.GetName().Version;
            Find<TextBlock>("Footer").Text = "BeautyScreenshot " + version.Major + "." + version.Minor +
                L.T("   ·   Un clic en el ícono de la bandeja también abre la captura", "   ·   Clicking the tray icon also starts a capture");

            SourceInitialized += (s, e) => StyleTitleBar();
            Loaded += (s, e) =>
            {
                var page = Find<FrameworkElement>("Page");
                page.BeginAnimation(OpacityProperty, Ui.Tween(0, 1, 260));
                Find<TranslateTransform>("PageShift").BeginAnimation(TranslateTransform.YProperty, Ui.Tween(10, 0, 340));
            };
            Deactivated += (s, e) => { if (recording) EndRecording(); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !recording) Close(); };
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            Closed += (s, e) =>
            {
                SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
                if (recording) hook.StopRecording();
            };
        }

        T Find<T>(string name) where T : class => (T)root.FindName(name);

        void Bind(string name, bool value, Action<bool> save)
        {
            var toggle = Find<CheckBox>(name);
            toggle.IsChecked = value;
            toggle.Checked += (s, e) => save(true);
            toggle.Unchecked += (s, e) => save(false);
        }

        // ---------------------------------------------------------------- shortcut recording

        void StartRecording()
        {
            recording = true;
            hotkeyButton.Tag = "Recording";
            hotkeyText.Text = L.T("Presioná la combinación…", "Press the combination…");
            SetNote(L.T("Esc para cancelar", "Esc to cancel"), false);
            hook.Record(OnRecorded);
        }

        void OnRecorded(Hotkey? pressed)
        {
            if (!recording) return;
            EndRecording();
            if (pressed == null) return;   // Esc

            var hotkey = pressed.Value;
            if (!hotkey.IsValid)
            {
                SetNote(L.T("«" + hotkey + "» sola molestaría al escribir: sumale Ctrl, Alt, Mayús o Win",
                    "“" + hotkey + "” alone would get in the way while typing: add Ctrl, Alt, Shift or Win"), true);
                return;
            }
            Settings.Hotkey = hotkey;
            hook.Hotkey = hotkey;
            hotkeyText.Text = hotkey.ToString();
            hotkeyChanged?.Invoke();
        }

        void EndRecording()
        {
            recording = false;
            hook.StopRecording();
            hotkeyButton.Tag = null;
            hotkeyText.Text = Settings.Hotkey.ToString();
            SetNote(L.T("Combinación que abre la captura", "Combination that starts a capture"), false);
        }

        void SetNote(string text, bool warning)
        {
            hotkeyNote.Text = text;
            hotkeyNote.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Text" : "SubText");
            hotkeyNote.FontWeight = warning ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // ---------------------------------------------------------------- theme

        static readonly Dictionary<string, uint[]> Palette = new Dictionary<string, uint[]>
        {
            //                       light       dark
            ["Bg"]              = new[] { 0xFFF3F3F3, 0xFF202020 },
            ["Card"]            = new[] { 0xFFFBFBFB, 0xFF2B2B2B },
            ["CardBorder"]      = new[] { 0xFFE5E5E5, 0xFF383838 },
            ["Text"]            = new[] { 0xFF1B1B1B, 0xFFF2F2F2 },
            ["SubText"]         = new[] { 0xFF6B6B6B, 0xFFA3A3A3 },
            ["ControlBg"]       = new[] { 0xFFFFFFFF, 0xFF333333 },
            ["ControlHover"]    = new[] { 0xFFF5F5F5, 0xFF3B3B3B },
            ["ControlPressed"]  = new[] { 0xFFEDEDED, 0xFF303030 },
            ["ControlBorder"]   = new[] { 0xFFD6D6D6, 0xFF474747 },
            ["Accent"]          = new[] { 0xFF2B2B2B, 0xFFE6E6E6 },
            ["AccentText"]      = new[] { 0xFFFFFFFF, 0xFF1B1B1B },
            ["ToggleOffBorder"] = new[] { 0xFF8A8A8A, 0xFFA0A0A0 },
            ["ToggleOffBg"]     = new[] { 0xFFF9F9F9, 0xFF272727 },
            ["ToggleOffHover"]  = new[] { 0xFFEDEDED, 0xFF343434 },
            ["TrackRest"]       = new[] { 0xFFD4D4D4, 0xFF4A4A4A },
            ["ScrollThumb"]     = new[] { 0x808A8A8A, 0x80A0A0A0 },
        };

        bool light;

        void ApplyPalette()
        {
            light = Theme.AppsLight;
            foreach (var entry in Palette)
                Resources[entry.Key] = Ui.Brush(entry.Value[light ? 0 : 1]);
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(ForegroundProperty, "Text");
        }

        void StyleTitleBar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Native.StyleTitleBar(hwnd, !light, ((SolidColorBrush)Resources["Bg"]).Color, ((SolidColorBrush)Resources["Text"]).Color);
        }

        void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Dispatcher.BeginInvoke(new Action(() => { ApplyPalette(); StyleTitleBar(); }));
        }

        static BitmapSource AppIcon()
        {
            using (var bmp = IconArt.AppIcon(64))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
    }
}
