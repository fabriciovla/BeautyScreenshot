using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using Path = System.Windows.Shapes.Path;
using Microsoft.Win32;

namespace BeautyScreenshot.Setup
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new SetupWindow());
        }
    }

    /// <summary>
    /// The setup, shaped like Beauty Shell's welcome: the title rises letter by letter over the liquid, Install
    /// turns into the progress bar in the same spot, and once installed the same window carries on with three short
    /// steps (the shortcut, copy or save, the tray) before opening the app. No wizard pages, no "Next" chrome.
    /// An update skips the steps: it installs and reopens the app.
    /// </summary>
    sealed class SetupWindow : Window
    {
        const double LetterMs = 32;
        const int WelcomeSteps = 3;

        static readonly bool Motion = SystemParameters.ClientAreaAnimation;
        static readonly Brush White = Brushes.White;

        readonly Grid scene = new Grid();
        readonly Grid root = new Grid();
        readonly Liquid liquid;
        readonly Button close;
        readonly StackPanel dots;
        readonly bool update = Installer.IsUpdate;
        Grid actionHost;
        Border note;
        CheckBox autoStart;
        bool busy, installed, finishing;
        TimeSpan lastFrame;

        public SetupWindow()
        {
            Title = update ? L.T("Actualizar Beauty Screenshot", "Update Beauty Screenshot") : L.T("Instalar Beauty Screenshot", "Install Beauty Screenshot");
            Width = 820;
            Height = 540;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Black;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            Icon = AppIcon();
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            using (var xaml = typeof(SetupWindow).Assembly.GetManifestResourceStream("SetupStyles.xaml"))
                Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(xaml));

            // Cool blues and lavender, soft like Beauty Shell's greens; dimmed in dark mode as there.
            Color c1 = Hsl(218, 0.46, 0.6), c2 = Hsl(258, 0.36, 0.76), c3 = Hsl(198, 0.48, 0.46);
            liquid = new Liquid(Width, Height, new[] { c1, c2, c3, Mix(c2, Colors.White, 0.45) }, AppsDark);

            var background = new Image { Source = liquid.Bitmap, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.HighQuality);
            root.Children.Add(background);
            root.Children.Add(new Rectangle { Fill = Liquid.Grain(), Opacity = 0.075, IsHitTestVisible = false });

            scene.Margin = new Thickness(24, 48, 24, 64);
            root.Children.Add(scene);

            dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 26),
                IsHitTestVisible = false,
                Opacity = 0,
            };
            for (int i = 0; i < WelcomeSteps; i++)
                dots.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0), Background = Alpha(0.4) });
            root.Children.Add(dots);

            close = new Button
            {
                Style = (Style)FindResource("Close"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14),
                ToolTip = update ? L.T("Cancelar la actualización", "Cancel the update") : L.T("Cancelar la instalación", "Cancel the installation"),
                IsCancel = true,
            };
            close.Click += (s, e) => OnClose();
            root.Children.Add(close);
            Content = root;

            MouseLeftButtonDown += (s, e) =>
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            SourceInitialized += (s, e) => RoundCorners();
            CompositionTarget.Rendering += OnFrame;
            Closed += (s, e) => CompositionTarget.Rendering -= OnFrame;
            Loaded += (s, e) => Cover();
        }

        // ---------------------------------------------------------------- the cover

        void Cover()
        {
            var title = Rising(InstallInfo.DisplayName, 56, out int letters);
            double after = letters * LetterMs + 400;

            var sub = Text(update ? L.T("Hay una versión nueva, lista para instalar.", "A new version is ready to install.") : L.T("Capturas de pantalla simples y lindas.", "Simple, beautiful screenshots."), 15, 0.9);
            sub.Margin = new Thickness(0, 14, 0, 0);

            actionHost = new Grid { Height = 42, Margin = new Thickness(0, 34, 0, 0) };
            var install = Primary(update ? L.T("Actualizar", "Update") : L.T("Instalar", "Install"), arrow: true);
            install.IsDefault = true;
            install.Click += async (s, e) => await Install();
            actionHost.Children.Add(Host(install));

            note = new Border { Margin = new Thickness(0, 18, 0, 0), MinHeight = 20, HorizontalAlignment = HorizontalAlignment.Center };
            SetNote(L.T("Para tu usuario  ·  Sin permisos de administrador  ·  ", "Just for you  ·  No admin rights needed  ·  ") + PayloadSize, animate: false);

            var step = Column(title, sub, actionHost, note);
            Show(step);
            Enter(sub, after);
            Enter(actionHost, after + 150);
            Enter(note, after + 300);
        }

        /// <summary>Install becomes the progress bar, in the same line and the same width; the note says each step.</summary>
        async Task Install()
        {
            if (busy) return;
            busy = true;
            close.Visibility = Visibility.Hidden;
            liquid.Flow(1);

            Leave(actionHost.Children[0]);
            await Wait(150);
            var fill = new ScaleTransform(0, 1);
            var bar = new Border
            {
                Width = 260,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Alpha(0.22),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Border { Background = White, CornerRadius = new CornerRadius(3), RenderTransform = fill },
            };
            actionHost.Children.Clear();
            actionHost.Children.Add(bar);
            Enter(bar, 0);

            var steps = new (string label, Action work)[]
            {
                (update ? L.T("Cerrando la versión anterior…", "Closing the previous version…") : L.T("Preparando…", "Getting ready…"), Installer.CloseRunning),
                (L.T("Copiando archivos…", "Copying files…"), Installer.CopyApp),
                (L.T("Creando el acceso en Inicio…", "Adding it to the Start menu…"), Installer.CreateShortcut),
                (L.T("Registrando la app en Windows…", "Registering the app with Windows…"), Installer.Register),
            };
            try
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    SetNote(steps[i].label);
                    var work = steps[i].work;
                    fill.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.To((i + 1.0) / steps.Length, Dur(450), 0, Ui.Glide));
                    await Task.WhenAll(Task.Run(() => work()), Wait(450));
                }
                // An update keeps whatever was chosen, now pointing at the installed exe.
                if (update) Installer.SetStartWithWindows(Installer.StartsWithWindows);
            }
            catch (Exception ex)
            {
                SetNote(L.T("No se pudo instalar: ", "Couldn't install: ") + ex.Message);
                busy = false;
                close.Visibility = Visibility.Visible;
                return;
            }

            installed = true;
            busy = false;
            SetNote(L.T("Listo", "Done"), check: true);
            await Wait(700);
            if (update) Finish(Installer.StartsWithWindows);
            else Welcome(0);
        }

        // ---------------------------------------------------------------- the welcome

        void Welcome(int index)
        {
            liquid.Flow(1);
            UIElement art;
            Button next;
            string title, text;
            switch (index)
            {
                case 0:
                    title = L.T("Apretá Impr Pant", "Press Print Screen");
                    text = L.T("Desde cualquier app, en cualquier momento. Si preferís otra combinación, la cambiás en Ajustes.",
                        "From any app, at any time. If you prefer another combination, change it in Settings.");
                    art = Keycap(L.T("Impr Pant", "Print Screen"));
                    next = Primary(L.T("Siguiente", "Next"), arrow: true);
                    next.Click += (s, e) => Advance(1);
                    break;
                case 1:
                    title = L.T("Arrastrá y elegí", "Drag and choose");
                    text = L.T("Marcá lo que quieras capturar. Copiar deja la imagen en el portapapeles y Guardar la guarda como archivo.",
                        "Mark what you want to capture. Copy puts the image on the clipboard and Save stores it as a file.");
                    art = SelectionArt();
                    next = Primary(L.T("Siguiente", "Next"), arrow: true);
                    next.Click += (s, e) => Advance(2);
                    break;
                default:
                    title = L.T("Vive en la bandeja", "It lives in the tray");
                    text = L.T("Su ícono queda junto al reloj: desde ahí capturás, abrís tus capturas o cambiás los ajustes.",
                        "Its icon sits next to the clock: capture, open your screenshots or change settings from there.");
                    art = AutoStartRow();
                    next = Primary(L.T("Empezar", "Get started"), arrow: true);
                    next.Click += (s, e) => Finish(autoStart.IsChecked == true);
                    break;
            }
            next.IsDefault = true;

            var head = Column(Text(title, 34, 1, FontWeights.SemiBold, shadow: true), Text(text, 14.5, 0.9));
            ((FrameworkElement)head.Children[1]).Margin = new Thickness(0, 10, 0, 0);
            var nextHost = Host(next);
            var step = Column(head, art, nextHost);
            ((FrameworkElement)art).Margin = new Thickness(0, 28, 0, 0);
            nextHost.Margin = new Thickness(0, 28, 0, 0);

            Show(step);
            for (int i = 0; i < step.Children.Count; i++) Enter(step.Children[i], i * 100);
            ShowDots(index);
            close.ToolTip = L.T("Cerrar", "Close");
        }

        void Advance(int index)
        {
            if (scene.Children.Count == 0) return;
            Leave(scene.Children[0], () => Welcome(index));
        }

        void ShowDots(int index)
        {
            if (dots.Opacity == 0) dots.BeginAnimation(OpacityProperty, Ui.To(1, Dur(300)));
            for (int i = 0; i < dots.Children.Count; i++)
            {
                var dot = (Border)dots.Children[i];
                dot.BeginAnimation(WidthProperty, Ui.To(i == index ? 20 : 6, Dur(300), 0, Ui.Glide));
                dot.Background = i == index ? White : Alpha(0.4);
            }
        }

        /// <summary>The key itself, in glass, pressed now and then.</summary>
        UIElement Keycap(string label)
        {
            var press = new TranslateTransform();
            var key = new Border
            {
                MinWidth = 150,
                Height = 64,
                Padding = new Thickness(24, 0, 24, 0),
                CornerRadius = new CornerRadius(14),
                Background = Alpha(0.18),
                BorderBrush = Alpha(0.4),
                BorderThickness = new Thickness(1, 1, 1, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransform = press,
                Child = Text(label, 19, 1, FontWeights.SemiBold),
            };
            ((TextBlock)key.Child).VerticalAlignment = VerticalAlignment.Center;
            if (Motion)
            {
                var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = Ui.Ms(900) };
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ui.Ms(0))));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ui.Ms(1400))));
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(3, KeyTime.FromTimeSpan(Ui.Ms(1480)), Ui.EaseOut));
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ui.Ms(1720)), Ui.EaseOut));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ui.Ms(2400))));
                press.BeginAnimation(TranslateTransform.YProperty, anim);
            }
            return Host(key);
        }

        /// <summary>A selection being dragged open over a glass "screen", then the Copy / Save bar under it.</summary>
        UIElement SelectionArt()
        {
            var art = new Grid { Width = 320, Height = 172, HorizontalAlignment = HorizontalAlignment.Center };
            art.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Alpha(0.1), BorderBrush = Alpha(0.22), BorderThickness = new Thickness(1) });

            var selection = new Grid
            {
                Width = 182,
                Height = 98,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(44, 24, 0, 0),
            };
            selection.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Alpha(0.22), BorderBrush = Alpha(0.9), BorderThickness = new Thickness(1) });
            selection.Children.Add(Corner("M 0,16 L 0,6 A 6,6 0 0 1 6,0 L 16,0", HorizontalAlignment.Left, VerticalAlignment.Top));
            selection.Children.Add(Corner("M 0,0 L 10,0 A 6,6 0 0 1 16,6 L 16,16", HorizontalAlignment.Right, VerticalAlignment.Top));
            selection.Children.Add(Corner("M 16,0 L 16,10 A 6,6 0 0 1 10,16 L 0,16", HorizontalAlignment.Right, VerticalAlignment.Bottom));
            selection.Children.Add(Corner("M 16,16 L 6,16 A 6,6 0 0 1 0,10 L 0,0", HorizontalAlignment.Left, VerticalAlignment.Bottom));
            art.Children.Add(selection);

            var bar = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1C, 0x1C, 0x1C)),
                Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(108, 134, 0, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Border { CornerRadius = new CornerRadius(11), Background = Alpha(0.16), Padding = new Thickness(12, 4, 12, 5), Child = Text(L.T("Copiar", "Copy"), 12, 1, FontWeights.SemiBold) },
                        new Border { Padding = new Thickness(12, 4, 12, 5), Child = Text(L.T("Guardar", "Save"), 12, 1, FontWeights.SemiBold) },
                    },
                },
            };
            art.Children.Add(bar);

            // Dragged open from its top-left corner, then the bar shows up under it.
            selection.BeginAnimation(WidthProperty, Ui.Tween(18, 182, Dur(900), 450, Ui.Glide));
            selection.BeginAnimation(HeightProperty, Ui.Tween(14, 98, Dur(900), 450, Ui.Glide));
            bar.Opacity = 0;
            Enter(bar, 1250);
            return art;
        }

        static Path Corner(string data, HorizontalAlignment h, VerticalAlignment v) => new Path
        {
            Data = Geometry.Parse(data),
            Stroke = White,
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = new Thickness(-6),
        };

        /// <summary>Start with Windows, on by default: a screenshot tool should be there when the key is pressed.</summary>
        UIElement AutoStartRow()
        {
            autoStart = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = true };
            var label = Column(Text(L.T("Iniciar con Windows", "Start with Windows"), 14, 1, FontWeights.SemiBold),
                Text(L.T("Para que esté lista siempre, sin abrirla.", "So it's always ready, without opening it."), 12.5, 0.75));
            label.HorizontalAlignment = HorizontalAlignment.Left;
            foreach (TextBlock t in label.Children)
            {
                t.TextAlignment = TextAlignment.Left;
                t.HorizontalAlignment = HorizontalAlignment.Left;
            }
            ((TextBlock)label.Children[1]).Margin = new Thickness(0, 2, 0, 0);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(label);
            Grid.SetColumn(autoStart, 1);
            grid.Children.Add(autoStart);

            var row = new Border
            {
                Width = 340,
                Padding = new Thickness(18, 14, 16, 14),
                CornerRadius = new CornerRadius(14),
                Background = Alpha(0.16),
                BorderBrush = Alpha(0.26),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = grid,
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (!autoStart.IsMouseOver) autoStart.IsChecked = autoStart.IsChecked != true;
                e.Handled = true;
            };
            row.MouseLeftButtonDown += (s, e) => e.Handled = true;   // not a window drag
            return row;
        }

        // ---------------------------------------------------------------- leaving

        void OnClose()
        {
            if (busy) return;
            if (installed) Finish(autoStart?.IsChecked ?? true);
            else Close();
        }

        /// <summary>Saves the choice, opens the app (its own "está activo" toast takes over) and fades away.</summary>
        void Finish(bool startWithWindows)
        {
            if (finishing) return;
            finishing = true;
            try
            {
                Installer.SetStartWithWindows(startWithWindows);
                Installer.Launch();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Beauty Screenshot quedó instalada, pero no se pudo abrir: ", "Beauty Screenshot is installed, but couldn't be opened: ") + ex.Message, InstallInfo.DisplayName);
            }
            var fade = Ui.To(0, Dur(220));
            fade.Completed += (s, e) => Close();
            root.BeginAnimation(OpacityProperty, fade);
        }

        // ---------------------------------------------------------------- building blocks

        void Show(Panel step)
        {
            scene.Children.Clear();
            scene.Children.Add(step);
        }

        /// <summary>
        /// A plain container, so Enter and Leave move it and not what is inside: buttons animate their own scale
        /// when pressed, and the key its press.
        /// </summary>
        static Grid Host(UIElement child)
        {
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            host.Children.Add(child);
            return host;
        }

        static StackPanel Column(params UIElement[] children)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var child in children) panel.Children.Add(child);
            return panel;
        }

        static TextBlock Text(string text, double size, double opacity, FontWeight? weight = null, bool shadow = false)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                Foreground = Alpha(opacity),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                HorizontalAlignment = HorizontalAlignment.Center,
                LineHeight = size * 1.45,
            };
            if (shadow) block.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 2, Direction = 270, Opacity = 0.1 };
            return block;
        }

        Button Primary(string label, bool arrow)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            if (arrow)
                content.Children.Add(new Path
                {
                    Data = Geometry.Parse("M 2,8 L 14,8 M 9,3 L 14,8 L 9,13"),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            return new Button { Style = (Style)FindResource("Primary"), Content = content };
        }

        /// <summary>The small line under the action: the size before, each step while installing, "Listo" at the end.</summary>
        void SetNote(string text, bool check = false, bool animate = true)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            if (check)
                line.Children.Add(new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = 13, Foreground = Alpha(0.85), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            var label = Text(text, 12.5, 0.75);
            label.MaxWidth = 520;
            line.Children.Add(label);
            note.Child = line;
            if (animate) Enter(note, 0);
        }

        /// <summary>
        /// The title: each letter rises out of a blur, one after the other, as in Beauty Shell (and Arc). The blur is
        /// dropped once a letter lands, so the text ends crisp.
        /// </summary>
        static FrameworkElement Rising(string text, double size, out int count)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            count = 0;
            foreach (char ch in text)
            {
                if (ch == ' ')
                {
                    row.Children.Add(new Border { Width = size * 0.26 });
                    continue;
                }
                var shift = new TranslateTransform(0, Motion ? size * 0.45 : 0);
                var blur = new BlurEffect { Radius = Motion ? 18 : 0 };
                var letter = new TextBlock
                {
                    Text = ch.ToString(),
                    FontSize = size,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = White,
                    Margin = new Thickness(0, 0, -size * 0.02, 0),
                    Opacity = 0,
                    RenderTransform = shift,
                    Effect = blur,
                };
                double delay = count * LetterMs;
                var fade = Ui.Tween(0, 1, Dur(900), delay, Ui.Glide);
                fade.Completed += (s, e) => letter.Effect = null;
                letter.BeginAnimation(OpacityProperty, fade);
                shift.BeginAnimation(TranslateTransform.YProperty, Ui.To(0, Dur(900), delay, Ui.Glide));
                blur.BeginAnimation(BlurEffect.RadiusProperty, Ui.To(0, Dur(900), delay, Ui.Glide));
                row.Children.Add(letter);
                count++;
            }
            return new Border
            {
                Child = row,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 2, Direction = 270, Opacity = 0.12 },
            };
        }

        /// <summary>Comes in from 12px below, out of a light blur (Beauty Shell's "enter").</summary>
        static void Enter(UIElement element, double delay)
        {
            var shift = new TranslateTransform(0, Motion ? 12 : 0);
            element.RenderTransform = shift;
            element.Opacity = 0;
            var fade = Ui.Tween(0, 1, Dur(300), delay);
            if (Motion && !(element is Panel))
            {
                // A blur on a whole step would also blur the effects inside it; only plain elements get it.
                var blur = new BlurEffect { Radius = 8 };
                if (element.Effect == null)
                {
                    element.Effect = blur;
                    blur.BeginAnimation(BlurEffect.RadiusProperty, Ui.To(0, Dur(300), delay));
                    fade.Completed += (s, e) => { if (element.Effect == blur) element.Effect = null; };
                }
            }
            element.BeginAnimation(OpacityProperty, fade);
            shift.BeginAnimation(TranslateTransform.YProperty, Ui.To(0, Dur(300), delay));
        }

        /// <summary>Goes up 12px and fades (Beauty Shell's "leave").</summary>
        static void Leave(UIElement element, Action then = null)
        {
            var shift = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
            element.RenderTransform = shift;
            var fade = Ui.To(0, Dur(150));
            if (then != null) fade.Completed += (s, e) => then();
            element.BeginAnimation(OpacityProperty, fade);
            shift.BeginAnimation(TranslateTransform.YProperty, Ui.To(Motion ? -12 : 0, Dur(150)));
        }

        static double Dur(double ms) => Motion ? ms : 1;
        static Task Wait(double ms) => Task.Delay(TimeSpan.FromMilliseconds(Motion ? ms : 0));

        static SolidColorBrush Alpha(double opacity)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255));
            b.Freeze();
            return b;
        }

        void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame) return;
            double dt = lastFrame == TimeSpan.Zero ? 0 : (now - lastFrame).TotalSeconds;
            lastFrame = now;
            if (Motion || dt == 0) liquid.Tick(Motion ? dt : 0);
        }

        static string PayloadSize
        {
            get
            {
                using (var s = typeof(SetupWindow).Assembly.GetManifestResourceStream("BeautyScreenshot.exe"))
                    return Math.Ceiling(s.Length / 1024.0) + " KB";
            }
        }

        static bool AppsDark
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
        }

        static Color Hsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Windows 11 rounds the corners of a borderless window only when asked.</summary>
        void RoundCorners()
        {
            int round = 2;   // DWMWCP_ROUND
            try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int)); }
            catch (DllNotFoundException) { }
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
