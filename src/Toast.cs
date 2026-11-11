using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BeautyScreenshot
{
    /// <summary>A small notification that rises at the bottom of a monitor and fades away on its own.</summary>
    sealed class Toast : Window
    {
        // Fixed window size with the card centred at the bottom: the transparent margin hosts the shadow
        // and is click-through, so the window can be placed in physical pixels without measuring first.
        const double WindowWidth = 420, WindowHeight = 120;

        static Toast current;

        readonly Forms.Screen screen;
        readonly Border card;
        readonly TranslateTransform shift = new TranslateTransform(0, 14);
        readonly DispatcherTimer timer;
        readonly Action onClick;
        IntPtr hwnd;
        bool leaving, closed;

        public static void Show(Forms.Screen screen, string title, string detail, BitmapSource thumb = null, Action onClick = null)
        {
            current?.Leave();
            current = new Toast(screen, title, detail, thumb, onClick);
            current.Present();
        }

        /// <summary>Closes the visible toast at once (before a capture, so it does not end up in it).</summary>
        public static bool CloseCurrent()
        {
            if (current == null) return false;
            current.timer.Stop();
            current.Close();
            current = null;
            return true;
        }

        Toast(Forms.Screen screen, string title, string detail, BitmapSource thumb, Action onClick)
        {
            this.screen = screen;
            this.onClick = onClick;

            Title = "BeautyScreenshot";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = WindowWidth;
            Height = WindowHeight;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;

            bool light = Theme.AppsLight;
            var surface = Ui.Brush(light ? 0xFAFBFBFBu : 0xF5262626u);
            var outline = Ui.Brush(light ? 0x14000000u : 0x1FFFFFFFu);
            var primary = Ui.Brush(light ? 0xFF1B1B1Bu : 0xFFF3F3F3u);
            var secondary = Ui.Brush(light ? 0xFF6B6B6Bu : 0xFFA8A8A8u);

            FrameworkElement leading;
            if (thumb != null)
            {
                leading = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = outline,
                    BorderThickness = new Thickness(1),
                    Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill },
                };
                RenderOptions.SetBitmapScalingMode(leading, BitmapScalingMode.HighQuality);
            }
            else
            {
                leading = new Border
                {
                    Width = 36,
                    Height = 36,
                    CornerRadius = new CornerRadius(18),
                    Background = Ui.Brush(light ? 0x0F000000u : 0x14FFFFFFu),
                    Child = new TextBlock
                    {
                        Text = "",
                        FontFamily = Ui.IconFont,
                        FontSize = 16,
                        Foreground = primary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
            }
            leading.Margin = new Thickness(0, 0, 12, 0);
            leading.VerticalAlignment = VerticalAlignment.Center;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = primary });
            if (!string.IsNullOrEmpty(detail))
                text.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    Foreground = secondary,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

            var row = new DockPanel();
            DockPanel.SetDock(leading, Dock.Left);
            row.Children.Add(leading);
            row.Children.Add(text);

            card = new Border
            {
                Child = row,
                Background = surface,
                BorderBrush = outline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 20, 10),
                MinWidth = 220,
                MaxWidth = WindowWidth - 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 20),
                Opacity = 0,
                RenderTransform = shift,
                Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Direction = 270, Opacity = light ? 0.18 : 0.4 },
            };
            card.MouseEnter += (s, e) => timer.Stop();
            card.MouseLeave += (s, e) => { if (!leaving) timer.Start(); };
            card.MouseLeftButtonUp += (s, e) => { onClick?.Invoke(); Leave(); };
            Content = new Grid { Children = { card } };

            timer = new DispatcherTimer { Interval = Ui.Ms(2600) };
            timer.Tick += (s, e) => Leave();
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Place));
            Closed += (s, e) => closed = true;
        }

        void Present()
        {
            hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Place();
            Place();
            Show();
            card.BeginAnimation(OpacityProperty, Ui.Tween(0, 1, 220));
            shift.BeginAnimation(TranslateTransform.YProperty, Ui.Tween(14, 0, 320));
            timer.Start();
        }

        void Place()
        {
            if (hwnd == IntPtr.Zero) return;
            double scale = Native.ScaleOf(screen);
            int w = (int)Math.Round(WindowWidth * scale), h = (int)Math.Round(WindowHeight * scale);
            var area = screen.WorkingArea;
            Native.Place(hwnd, area.X + (area.Width - w) / 2, area.Bottom - h, w, h);
        }

        void Leave()
        {
            if (leaving) return;
            leaving = true;
            timer.Stop();
            if (current == this) current = null;
            card.BeginAnimation(OpacityProperty, Ui.To(0, 200));
            shift.BeginAnimation(TranslateTransform.YProperty, Ui.To(6, 220));
            Ui.After(240, () => { if (!closed) Close(); });
        }
    }
}
