using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BeautyScreenshot
{
    /// <summary>
    /// Where the floating thumbnail sits: bottom-right of the work area. Shared by the overlay (which flies the
    /// capture there) and the thumbnail stack (which takes over at that exact spot), so the hand-off is seamless.
    /// </summary>
    static class ThumbLayout
    {
        public const double MaxWidth = 220, MaxHeight = 150, Margin = 20;
        /// <summary>White frame around the picture; the picture's corners stay concentric with the frame's.</summary>
        public const double Frame = 4, OuterRadius = 10, InnerRadius = OuterRadius - Frame;
        public const double ShadowBlur = 26, ShadowDepth = 4, ShadowOpacity = 0.45;

        /// <summary>Size of the picture inside the frame for an image of w×h DIPs: fits the box, keeps the aspect, never enlarges.</summary>
        public static Size Fit(double w, double h)
        {
            double s = Math.Min(1, Math.Min(MaxWidth / w, MaxHeight / h));
            return new Size(Math.Max(1, w * s), Math.Max(1, h * s));
        }

        /// <summary>Picture plus its frame.</summary>
        public static Size Framed(Size picture) => new Size(picture.Width + 2 * Frame, picture.Height + 2 * Frame);

        public static DropShadowEffect Shadow(double opacity) => new DropShadowEffect
        {
            BlurRadius = ShadowBlur,
            ShadowDepth = ShadowDepth,
            Direction = 270,
            Color = Colors.Black,
            Opacity = opacity,
        };
    }

    /// <summary>
    /// Captures resting in the corner after copying or saving, macOS style. Captures taken while it is up pile on top
    /// of each other, the older ones peeking out slightly turned, with a counter; the pile then slides out together.
    /// Windows keeps the window out of screenshots, so it stays put during a new capture: the overlay shows a live copy
    /// of it (<see cref="GhostFor"/>) for the new capture to land on. Where that exclusion is not available, it hides
    /// during the capture instead and comes back afterwards.
    /// </summary>
    sealed class CaptureThumbnail : Window
    {
        const int Behind = 3;            // older captures still drawn under the top one
        const double Peek = 6;           // how far each older capture sticks out
        static readonly double[] Turn = { 0, -3.5, 2.5, -1.5 };

        // Room for the largest card, the pile peeking out and its turned corners; cards sit at the bottom-right.
        static readonly double WindowWidth = ThumbLayout.MaxWidth + 2 * ThumbLayout.Frame + 2 * ThumbLayout.Margin + Peek * Behind + 24;
        static readonly double WindowHeight = ThumbLayout.MaxHeight + 2 * ThumbLayout.Frame + 2 * ThumbLayout.Margin + Peek * Behind + 24;

        static CaptureThumbnail current;

        sealed class Card
        {
            public Border View;
            public Border Caption;
            public RotateTransform Rotation;
            public ScaleTransform Pop;
            public TranslateTransform Offset;
            public DropShadowEffect Shadow;
            public Size Framed;
            public Action OnClick;
        }

        readonly Forms.Screen screen;
        readonly Grid stage = new Grid();
        readonly TranslateTransform slide = new TranslateTransform();
        readonly List<Card> cards = new List<Card>();   // oldest first; the last one is on top
        readonly Border badge;
        readonly TextBlock badgeText;
        readonly ScaleTransform badgePop = new ScaleTransform(1, 1);
        readonly DispatcherTimer timer = new DispatcherTimer();
        int count;
        IntPtr hwnd;
        bool excluded;   // Windows keeps this window out of screenshots
        bool leaving, closed;

        // ---------------------------------------------------------------- API

        /// <summary>Puts a capture that just landed in the corner on top of the stack (making the stack if needed).</summary>
        /// <param name="dwellMs">How long the stack rests after this capture before leaving; 0 slides it out right away.</param>
        public static void Add(Forms.Screen screen, BitmapSource image, string label, bool ok, Action onClick, int dwellMs)
        {
            if (current != null && (current.leaving || current.closed || current.screen.DeviceName != screen.DeviceName))
            {
                current.Dispose();
                current = null;
            }
            if (current == null)
            {
                current = new CaptureThumbnail(screen);
                current.Present();
            }
            else if (!current.IsVisible) current.Show();
            current.Push(image, dwellMs > 0 ? label : null, ok, onClick, dwellMs);
        }

        /// <summary>
        /// A capture is starting: the stack stops its countdown, and only hides if Windows cannot keep it out of the
        /// screenshot. Returns true if it hid, so the caller waits for the screen to update before grabbing it.
        /// </summary>
        public static bool StepAsideForCapture()
        {
            if (current == null || current.closed) return false;
            current.timer.Stop();
            if (current.excluded || !current.IsVisible) return false;
            if (current.leaving) { current.Dispose(); current = null; }
            else current.Hide();
            return true;
        }

        /// <summary>The capture was cancelled: the stack resumes its countdown (and comes back if it had to hide).</summary>
        public static void Restore()
        {
            if (current == null || current.closed || current.leaving) return;
            if (!current.IsVisible) current.Show();
            current.timer.Start();
        }

        /// <summary>
        /// The stack's content, for the overlay of <paramref name="screen"/> to draw a live copy of it at the same
        /// spot during a capture (the overlay covers the real one); null when there is nothing to copy.
        /// </summary>
        public static Visual GhostFor(Forms.Screen screen, out Size size)
        {
            size = new Size(WindowWidth, WindowHeight);
            var c = current;
            if (c == null || c.closed || c.leaving || !c.excluded || !c.IsVisible || c.screen.DeviceName != screen.DeviceName) return null;
            return c.stage;
        }

        // ---------------------------------------------------------------- window

        CaptureThumbnail(Forms.Screen screen)
        {
            this.screen = screen;

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
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            Width = WindowWidth;
            Height = WindowHeight;

            badgeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1),
            };
            badge = new Border
            {
                Child = badgeText,
                Background = Ui.Brush(0xF21E1E1E),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(11),
                MinWidth = 22,
                Height = 22,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = badgePop,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            stage.RenderTransform = slide;
            stage.Children.Add(badge);
            stage.MouseEnter += (s, e) => timer.Stop();
            stage.MouseLeave += (s, e) => { if (!leaving) timer.Start(); };
            stage.MouseLeftButtonUp += (s, e) =>
            {
                if (cards.Count > 0) cards[cards.Count - 1].OnClick?.Invoke();
                Leave();
            };
            Content = stage;

            timer.Tick += (s, e) => Leave();
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Place));
            Closed += (s, e) => closed = true;
        }

        void Present()
        {
            hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            excluded = Native.ExcludeFromCapture(hwnd);
            Place();
            Place();
            Show();
        }

        /// <summary>Bottom-right of the work area, so the top card sits exactly where the overlay animation left it.</summary>
        void Place()
        {
            if (hwnd == IntPtr.Zero) return;
            double scale = Native.ScaleOf(screen);
            int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
            var area = screen.WorkingArea;
            Native.Place(hwnd, area.Right - w, area.Bottom - h, w, h);
        }

        // ---------------------------------------------------------------- stack

        void Push(BitmapSource image, string label, bool ok, Action onClick, int dwellMs)
        {
            var picture = ThumbLayout.Fit(image.Width, image.Height);
            var card = MakeCard(Shrink(image, picture), picture, label, ok);
            card.OnClick = onClick;
            cards.Add(card);
            stage.Children.Insert(stage.Children.Count - 1, card.View);   // under the badge, over the older cards
            count++;

            // Older captures drop back into the pile; only the newest few stay drawn.
            while (cards.Count > Behind + 1)
            {
                stage.Children.Remove(cards[0].View);
                cards.RemoveAt(0);
            }
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                int depth = cards.Count - 1 - i;
                c.Offset.BeginAnimation(TranslateTransform.XProperty, Ui.To(-Peek * depth, 260));
                c.Offset.BeginAnimation(TranslateTransform.YProperty, Ui.To(-Peek * depth, 260));
                c.Rotation.BeginAnimation(RotateTransform.AngleProperty, Ui.To(Turn[Math.Min(depth, Turn.Length - 1)], 260));
                if (depth > 0) c.Caption.BeginAnimation(OpacityProperty, Ui.To(0, 150));
            }
            card.Caption.BeginAnimation(OpacityProperty, Ui.Tween(0, 1, 180, 120));
            Land(card);

            if (count >= 2)
            {
                badgeText.Text = count.ToString();
                badge.Margin = new Thickness(0, 0, ThumbLayout.Margin - 9, ThumbLayout.Margin + card.Framed.Height - 13);
                badge.Visibility = Visibility.Visible;
                badgePop.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.Tween(0.5, 1, 260, 60, new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
                badgePop.BeginAnimation(ScaleTransform.ScaleYProperty, Ui.Tween(0.5, 1, 260, 60, new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
            }

            stage.Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow;
            timer.Stop();
            timer.Interval = Ui.Ms(Math.Max(dwellMs, 60));
            timer.Start();
        }

        /// <summary>
        /// The capture settles in with a soft bounce: it swells a little, dips just under its size and comes to rest,
        /// its shadow lifting and dropping with it as if it had landed on the desktop. It starts a frame after the
        /// hand-off, once the flying copy in the overlay is gone, so the bounce is seen whole.
        /// </summary>
        static void Land(Card card)
        {
            var bounce = new DoubleAnimationUsingKeyFrames { BeginTime = Ui.Ms(40) };
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(Ui.Ms(110)), Ui.EaseOut));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromTimeSpan(Ui.Ms(230)), Ui.EaseInOut));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ui.Ms(340)), Ui.EaseOut));
            card.Pop.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            card.Pop.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);

            var lift = new DoubleAnimationUsingKeyFrames { BeginTime = Ui.Ms(40) };
            lift.KeyFrames.Add(new EasingDoubleKeyFrame(ThumbLayout.ShadowDepth * 2.5, KeyTime.FromTimeSpan(Ui.Ms(110)), Ui.EaseOut));
            lift.KeyFrames.Add(new EasingDoubleKeyFrame(ThumbLayout.ShadowDepth, KeyTime.FromTimeSpan(Ui.Ms(300)), Ui.EaseInOut));
            card.Shadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, lift);
        }

        Card MakeCard(BitmapSource image, Size picture, string label, bool ok)
        {
            var framed = ThumbLayout.Framed(picture);
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);

            var caption = new Border
            {
                Background = Ui.Brush(0xD91E1E1E),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 8, 3),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0,
                IsHitTestVisible = false,
                Visibility = label != null && picture.Width >= 90 && picture.Height >= 44 ? Visibility.Visible : Visibility.Collapsed,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = ok ? "" : "",
                FontFamily = Ui.IconFont,
                FontSize = 10,
                Foreground = Ui.Brush(0xFFF2F2F2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0),
            });
            row.Children.Add(new TextBlock { Text = label ?? "", FontSize = 11, Foreground = Ui.Brush(0xFFF2F2F2) });
            caption.Child = row;

            var rotation = new RotateTransform();
            var pop = new ScaleTransform(1, 1);
            var offset = new TranslateTransform();
            var shadow = ThumbLayout.Shadow(ThumbLayout.ShadowOpacity);
            var view = new Border
            {
                Width = framed.Width,
                Height = framed.Height,
                CornerRadius = new CornerRadius(ThumbLayout.OuterRadius),
                Background = Brushes.White,
                Padding = new Thickness(ThumbLayout.Frame),
                Effect = shadow,
                Margin = new Thickness(0, 0, ThumbLayout.Margin, ThumbLayout.Margin),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = new TransformCollection { rotation, pop, offset } },
                Child = new Border
                {
                    CornerRadius = new CornerRadius(ThumbLayout.InnerRadius),
                    Background = brush,
                    Child = caption,
                },
            };
            return new Card { View = view, Caption = caption, Rotation = rotation, Pop = pop, Offset = offset, Shadow = shadow, Framed = framed };
        }

        /// <summary>A small copy for display, so a pile of full-screen captures does not keep megabytes alive.</summary>
        BitmapSource Shrink(BitmapSource image, Size picture)
        {
            double pixels = picture.Width * Native.ScaleOf(screen) * 2;   // twice the shown size keeps it sharp
            double k = pixels / image.PixelWidth;
            if (k >= 1) return image;
            var small = new WriteableBitmap(new TransformedBitmap(image, new ScaleTransform(k, k)));
            small.Freeze();
            return small;
        }

        void Leave()
        {
            if (leaving) return;
            leaving = true;
            timer.Stop();
            // Picks up speed on its way out: short, so it reads as a swipe rather than a slow drift.
            slide.BeginAnimation(TranslateTransform.XProperty, Ui.To(WindowWidth, 230, 0, Ui.Launch));
            stage.BeginAnimation(OpacityProperty, Ui.To(0, 110, 120));
            Ui.After(260, Dispose);
        }

        void Dispose()
        {
            timer.Stop();
            if (current == this) current = null;
            if (!closed) Close();
        }
    }
}
