using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace BeautyScreenshot
{
    enum CaptureAction { Copy, Save }

    sealed class CaptureResult
    {
        public BitmapSource Image;
        public CaptureAction Action;
        public Forms.Screen Screen;
        public bool Copied;
        public string SavedPath;
        public string Error;
    }

    /// <summary>One capture: freezes every monitor, shows one overlay per monitor and reports the outcome.</summary>
    sealed class CaptureSession
    {
        // Overlays are created once per monitor and reused: showing a hidden window takes ~2 ms, creating one ~60 ms
        // (~290 ms the very first time), which is the difference between instant and noticeably late.
        static readonly Dictionary<string, OverlayWindow> pool = new Dictionary<string, OverlayWindow>();

        readonly List<OverlayWindow> windows = new List<OverlayWindow>();
        OverlayWindow underCursor;
        IntPtr previousFocus;
        bool ending;

        /// <summary>Raised when the capture reaches the corner (success) or the overlays fade out (null: cancelled).</summary>
        public event Action<CaptureResult> Finished;

        static string KeyOf(Forms.Screen s) => s.DeviceName + "|" + s.Bounds;

        static OverlayWindow For(Forms.Screen s)
        {
            if (!pool.TryGetValue(KeyOf(s), out var w)) pool[KeyOf(s)] = w = new OverlayWindow();
            return w;
        }

        /// <summary>Closes overlays of monitors that no longer exist or changed size.</summary>
        static void DropStale(Forms.Screen[] screens)
        {
            var current = new HashSet<string>(screens.Select(KeyOf));
            foreach (var key in pool.Keys.Where(k => !current.Contains(k)).ToList())
            {
                pool[key].Close();
                pool.Remove(key);
            }
        }

        static BitmapSource[] Grab(System.Drawing.Rectangle[] areas)
        {
            var shots = new BitmapSource[areas.Length];
            if (areas.Length == 1) shots[0] = Imaging.Capture(areas[0]);
            else System.Threading.Tasks.Parallel.For(0, areas.Length, i => shots[i] = Imaging.Capture(areas[i]));
            return shots;
        }

        /// <summary>Creates the overlays for the current monitors and renders each once off-screen, so even the first capture is instant.</summary>
        public static void Prepare()
        {
            var screens = Forms.Screen.AllScreens;
            DropStale(screens);
            // Loads GDI+ and spins up the worker threads ahead of time, grabbing one pixel per monitor.
            Grab(screens.Select(s => new System.Drawing.Rectangle(s.Bounds.Location, new System.Drawing.Size(1, 1))).ToArray());
            foreach (var s in screens) For(s).WarmUp(s);
        }

        /// <summary>The monitor layout changed: start over with fresh overlays.</summary>
        public static void Reset()
        {
            foreach (var w in pool.Values) w.Close();
            pool.Clear();
        }

        public void Start()
        {
            // Grab all monitors before any overlay is shown, so no overlay ends up in another's screenshot,
            // and in parallel, so a second monitor does not add its time to the first one's.
            var screens = Forms.Screen.AllScreens;
            var shots = Grab(screens.Select(s => s.Bounds).ToArray());
            var cursor = Forms.Cursor.Position;
            DropStale(screens);

            for (int i = 0; i < screens.Length; i++)
            {
                var w = For(screens[i]);
                w.Begin(this, screens[i], shots[i]);
                windows.Add(w);
                if (screens[i].Bounds.Contains(cursor)) underCursor = w;
            }
            underCursor = underCursor ?? windows[0];
            previousFocus = Native.Foreground();
            // The overlays never take the focus (keys arrive through OnKey): a full-screen window in the foreground
            // makes Windows switch on "Do not disturb", and the app behind would repaint as inactive.
            foreach (var w in windows) w.Present(w == underCursor);
        }

        /// <summary>
        /// Keys pressed during the capture, from the keyboard hook. While selecting, every key is taken (true), so
        /// nothing reaches the app behind; once the capture is ending, or while the save dialog is open, they pass.
        /// </summary>
        public bool OnKey(int vk, int mods)
        {
            if (ending || windows.Any(w => w.IsBusy)) return false;
            var target = windows.FirstOrDefault(w => w.HasSelection) ?? underCursor;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => target.HandleKey(vk, mods)));
            return true;
        }

        /// <summary>After the save dialog, which did take the focus, give it back to the app behind.</summary>
        public void RestoreFocus() => Native.GiveFocusBack(previousFocus);

        internal void SelectionStarted(OverlayWindow owner)
        {
            foreach (var w in windows)
                if (w != owner) w.ClearSelection();
        }

        public void Cancel() => End(null, null);

        internal void Complete(OverlayWindow source, CaptureResult result) => End(source, result);

        void End(OverlayWindow source, CaptureResult result)
        {
            if (ending) return;
            ending = true;
            // If the save dialog took the focus, hand it back now, while the frozen screenshot still covers everything.
            Native.GiveFocusBack(previousFocus);
            foreach (var w in windows)
            {
                // On success, Finished fires when the capture reaches the corner, so the thumbnail can take over
                // at that spot before the overlay goes away.
                if (w == source) w.PlayConfirm(result.Image, () => Finished?.Invoke(result));
                else w.Dismiss();
            }
            if (source == null) Ui.After(200, () => Finished?.Invoke(null));
        }

        /// <summary>Immediate teardown after an unexpected error.</summary>
        public void Abort()
        {
            ending = true;
            foreach (var w in windows) w.Finish();
        }
    }

    /// <summary>
    /// Full-screen window showing the frozen screenshot of one monitor, dimmed except for the selection.
    /// Because the background is the screenshot itself, the window can be opaque (fast) and still look transparent.
    /// It is reused across captures: <see cref="Begin"/> arms it, <see cref="Finish"/> hides it.
    /// </summary>
    sealed class OverlayWindow : Window
    {
        enum Mode { Idle, Dragging, Moving, Selected, Busy, Closing }

        static readonly Size Unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

        CaptureSession session;
        Forms.Screen screen;
        BitmapSource shot;
        IntPtr hwnd;

        readonly Image image = new Image { Stretch = Stretch.Fill };
        UIElement flight;   // the confirm animation's layer, removed when the window is reused
        UIElement ghost;    // live copy of the thumbnail pile, which this window covers during a capture
        readonly Grid root = new Grid();
        readonly Canvas layer = new Canvas();
        readonly Path dim;
        readonly RectangleGeometry full = new RectangleGeometry();
        readonly RectangleGeometry hole = new RectangleGeometry(Rect.Empty);
        readonly Rectangle edgeLight, edgeDark;
        readonly Path corners;
        readonly Border sizeTag;
        readonly TextBlock sizeText;
        readonly Border toolbar;
        readonly TranslateTransform toolbarShift = new TranslateTransform();
        readonly Border hint;

        double radius;
        int generation;   // bumped on every Begin, so a delayed Finish from the previous capture cannot hide the new one
        Mode mode = Mode.Idle;
        Point anchor;
        Vector moveOffset;
        Rect target = Rect.Empty;   // where the selection is (pixel-snapped, in DIPs)
        Rect shown = Rect.Empty;    // where it is drawn; eases toward target every frame
        bool rendering;
        TimeSpan lastFrame;

        public OverlayWindow()
        {
            Title = "BeautyScreenshot";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            UseLayoutRounding = true;
            FontFamily = Ui.TextFont;

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

            dim = new Path
            {
                Opacity = 0,
                IsHitTestVisible = false,
                Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = new GeometryCollection { full, hole } },
            };

            edgeLight = new Rectangle { Stroke = Ui.Brush(0xF0FFFFFF), StrokeThickness = 1, IsHitTestVisible = false };
            edgeDark = new Rectangle { Stroke = Ui.Brush(0x38000000), StrokeThickness = 1, IsHitTestVisible = false };
            corners = new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };

            sizeText = new TextBlock { Foreground = Ui.Brush(0xFFF3F3F3), FontSize = 12 };
            sizeTag = new Border
            {
                Child = sizeText,
                Background = Ui.Brush(0xE61E1E1E),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 4),
                IsHitTestVisible = false,
            };

            toolbar = BuildToolbar();

            hint = new Border
            {
                Background = Ui.Brush(0xB3161616),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(14, 5, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Opacity = 0,
                Child = new TextBlock
                {
                    Text = L.T("Arrastrá para seleccionar  ·  Esc para salir", "Drag to select  ·  Esc to exit"),
                    Foreground = Ui.Brush(0xCCFFFFFF),
                    FontSize = 12,
                },
            };

            SetChrome(false);
            layer.Children.Add(edgeDark);
            layer.Children.Add(edgeLight);
            layer.Children.Add(corners);
            layer.Children.Add(sizeTag);
            layer.Children.Add(toolbar);

            root.Children.Add(image);
            root.Children.Add(dim);
            root.Children.Add(layer);
            root.Children.Add(hint);
            root.SizeChanged += (s, e) =>
            {
                full.Rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                PlaceHint();
            };
            Content = root;

            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Place));
            Closed += (s, e) => StopFrames();
        }

        /// <summary>The hint sits just above the taskbar, which the overlay covers.</summary>
        void PlaceHint()
        {
            if (screen == null || root.ActualHeight <= 0) return;
            double taskbar = (screen.Bounds.Bottom - screen.WorkingArea.Bottom) / ScaleY;
            hint.Margin = new Thickness(0, 0, 0, taskbar + 24);
        }

        // ---------------------------------------------------------------- lifecycle

        IntPtr EnsureHwnd()
        {
            if (hwnd == IntPtr.Zero)
            {
                hwnd = new WindowInteropHelper(this).EnsureHandle();
                // Never activated, even when clicked: see CaptureSession.OnKey.
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            }
            return hwnd;
        }

        /// <summary>
        /// Shows the window once far off-screen, at the monitor's size, and hides it after its first frame:
        /// window, GPU surface and code paths are then ready for an instant first capture.
        /// </summary>
        public void WarmUp(Forms.Screen monitor)
        {
            var tiny = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
            Begin(null, monitor, tiny);
            Native.Place(EnsureHwnd(), -32000, -32000, monitor.Bounds.Width, monitor.Bounds.Height);
            EventHandler rendered = null;
            rendered = (s, e) => { ContentRendered -= rendered; Finish(); };
            ContentRendered += rendered;
            Show();
        }

        /// <summary>Arms the (hidden) window for a new capture of <paramref name="monitor"/>.</summary>
        public void Begin(CaptureSession owner, Forms.Screen monitor, BitmapSource screenshot)
        {
            generation++;
            session = owner;
            screen = monitor;
            shot = screenshot;
            image.Source = screenshot;
            radius = Settings.CornerRadius;
            dim.Fill = Ui.Brush((uint)Math.Round(Settings.DimPercent * 2.55) << 24);

            StopFrames();
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (flight != null) { root.Children.Remove(flight); flight = null; }
            mode = Mode.Idle;
            target = shown = Rect.Empty;
            hole.Rect = Rect.Empty;
            SetChrome(false);
            HideToolbar();
            dim.BeginAnimation(OpacityProperty, null);
            dim.Opacity = 0;
            hint.BeginAnimation(OpacityProperty, null);
            hint.Opacity = 0;
            Cursor = Cursors.Cross;
            PlaceHint();
            ShowGhost(monitor);
        }

        /// <summary>
        /// The thumbnail pile is left out of screenshots, so the frozen screenshot does not have it, and this window
        /// covers the real one. A live copy at the same spot keeps it in view, undimmed, for the new capture to land on;
        /// when the overlay goes away the real pile, identical, is underneath.
        /// </summary>
        void ShowGhost(Forms.Screen monitor)
        {
            if (ghost != null) { root.Children.Remove(ghost); ghost = null; }
            var pile = CaptureThumbnail.GhostFor(monitor, out Size size);
            if (pile == null) return;

            // Same placement as the pile's own window: bottom-right of the work area, in this monitor's DIPs.
            double scale = Native.ScaleOf(monitor);
            var b = monitor.Bounds;
            var area = monitor.WorkingArea;
            double left = (area.Right - b.X - Math.Round(size.Width * scale)) / scale;
            double top = (area.Bottom - b.Y - Math.Round(size.Height * scale)) / scale;
            var view = new Rect(size);
            ghost = new Rectangle
            {
                Width = size.Width,
                Height = size.Height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(left, top, 0, 0),
                IsHitTestVisible = false,
                Fill = new VisualBrush(pile)
                {
                    Stretch = Stretch.None,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = view,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = view,
                },
            };
            root.Children.Insert(root.Children.IndexOf(layer), ghost);   // above the dimming, under the selection chrome
        }

        /// <summary>Hides the window for the next capture and lets go of the screenshot.</summary>
        public void Finish()
        {
            StopFrames();
            if (IsMouseCaptured) ReleaseMouseCapture();
            Hide();
            if (flight != null) { root.Children.Remove(flight); flight = null; }
            if (ghost != null) { root.Children.Remove(ghost); ghost = null; }
            image.Source = null;
            shot = null;
            session = null;
            mode = Mode.Closing;
        }

        public void Present(bool withHint)
        {
            EnsureHwnd();
            Place();
            Place();   // moving onto a monitor with another DPI resizes the window once; the second call undoes that
            Show();
            Place();

            // Quick enough to read as instant, still a fade rather than a cut.
            dim.BeginAnimation(OpacityProperty, Ui.Tween(0, 1, 110));
            if (withHint && Settings.ShowHint)
            {
                // Appears softly, stays a moment and dissolves on its own.
                var life = new DoubleAnimationUsingKeyFrames();
                life.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ui.Ms(200))));
                life.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ui.Ms(450)), Ui.EaseOut));
                life.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ui.Ms(3000))));
                life.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(Ui.Ms(3700)), Ui.EaseInOut));
                hint.BeginAnimation(OpacityProperty, life);
            }
        }

        void Place()
        {
            if (hwnd == IntPtr.Zero) return;
            var b = screen.Bounds;
            Native.Place(hwnd, b.X, b.Y, b.Width, b.Height);
        }


        // ---------------------------------------------------------------- geometry

        double ScaleX => shot != null && root.ActualWidth > 0 ? shot.PixelWidth / root.ActualWidth : 1;
        double ScaleY => shot != null && root.ActualHeight > 0 ? shot.PixelHeight / root.ActualHeight : 1;

        /// <summary>Clamps to the window and rounds to whole physical pixels.</summary>
        Point Snap(Point p)
        {
            double sx = ScaleX, sy = ScaleY;
            double x = Math.Max(0, Math.Min(p.X, root.ActualWidth));
            double y = Math.Max(0, Math.Min(p.Y, root.ActualHeight));
            return new Point(Math.Round(x * sx) / sx, Math.Round(y * sy) / sy);
        }

        Int32Rect PixelRect(Rect r)
        {
            int x = Clamp((int)Math.Round(r.Left * ScaleX), 0, shot.PixelWidth);
            int y = Clamp((int)Math.Round(r.Top * ScaleY), 0, shot.PixelHeight);
            int right = Clamp((int)Math.Round(r.Right * ScaleX), 0, shot.PixelWidth);
            int bottom = Clamp((int)Math.Round(r.Bottom * ScaleY), 0, shot.PixelHeight);
            return new Int32Rect(x, y, right - x, bottom - y);
        }

        static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        // ---------------------------------------------------------------- mouse and keyboard

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled || mode == Mode.Busy || mode == Mode.Closing) return;
            if (toolbar.IsVisible && toolbar.IsMouseOver) return;

            var p = Snap(e.GetPosition(root));
            if (mode == Mode.Selected && target.Contains(p))
            {
                if (e.ClickCount >= 2) { Confirm(Settings.DefaultAction); return; }
                mode = Mode.Moving;
                moveOffset = p - target.TopLeft;
                HideToolbar();
                CaptureMouse();
                return;
            }

            session.SelectionStarted(this);
            mode = Mode.Dragging;
            anchor = p;
            target = shown = new Rect(p, p);
            HideToolbar();
            Ui.Fade(hint, 0, 160);
            Apply();
            SetChrome(true);
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(root);
            switch (mode)
            {
                case Mode.Dragging:
                    target = new Rect(anchor, Snap(p));
                    Animate();
                    break;
                case Mode.Moving:
                    double x = Math.Max(0, Math.Min(p.X - moveOffset.X, root.ActualWidth - target.Width));
                    double y = Math.Max(0, Math.Min(p.Y - moveOffset.Y, root.ActualHeight - target.Height));
                    target = new Rect(Snap(new Point(x, y)), target.Size);
                    Animate();
                    break;
                case Mode.Selected:
                    Cursor = target.Contains(p) && !toolbar.IsMouseOver ? Cursors.SizeAll : Cursors.Cross;
                    break;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (mode == Mode.Dragging)
            {
                ReleaseMouseCapture();
                var px = PixelRect(target);
                if (px.Width < 4 || px.Height < 4)
                    target = new Rect(0, 0, root.ActualWidth, root.ActualHeight);   // a plain click takes the whole monitor
                mode = Mode.Selected;
                Animate();
                ShowToolbar();
            }
            else if (mode == Mode.Moving)
            {
                ReleaseMouseCapture();
                mode = Mode.Selected;
                ShowToolbar();
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (mode == Mode.Idle) session.Cancel();
            else if (mode == Mode.Selected || mode == Mode.Dragging || mode == Mode.Moving) ClearSelection();
        }

        public bool IsBusy => mode == Mode.Busy;
        public bool HasSelection => mode == Mode.Selected || mode == Mode.Dragging || mode == Mode.Moving;

        /// <summary>A key from the keyboard hook (virtual-key code, <see cref="Hotkey"/> modifier flags).</summary>
        public void HandleKey(int vk, int mods)
        {
            if (session == null || mode == Mode.Busy || mode == Mode.Closing) return;
            bool ctrl = (mods & Hotkey.Ctrl) != 0;
            switch (vk)
            {
                case 0x1B: session.Cancel(); break;                             // Esc
                case 0x0D: Confirm(Settings.DefaultAction); break;              // Enter
                case 0x43 when ctrl: Confirm(CaptureAction.Copy); break;        // Ctrl+C
                case 0x53 when ctrl: Confirm(CaptureAction.Save); break;        // Ctrl+S
            }
        }

        public void ClearSelection()
        {
            StopFrames();
            if (IsMouseCaptured) ReleaseMouseCapture();
            mode = Mode.Idle;
            target = shown = Rect.Empty;
            hole.Rect = Rect.Empty;
            SetChrome(false);
            HideToolbar();
            Cursor = Cursors.Cross;
        }

        // ---------------------------------------------------------------- drawing

        void Animate()
        {
            if (rendering) return;
            rendering = true;
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        void StopFrames()
        {
            if (!rendering) return;
            rendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame) return;
            double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - lastFrame).TotalSeconds, 0.05);
            lastFrame = now;

            // Frame-rate independent exponential ease: quick enough to feel direct, soft enough to feel fluid.
            double k = 1 - Math.Exp(-dt * 30);
            if (shown.IsEmpty) shown = target;
            double l = shown.Left + (target.Left - shown.Left) * k;
            double t = shown.Top + (target.Top - shown.Top) * k;
            double r = shown.Right + (target.Right - shown.Right) * k;
            double b = shown.Bottom + (target.Bottom - shown.Bottom) * k;
            bool settled = Math.Abs(l - target.Left) < 0.25 && Math.Abs(t - target.Top) < 0.25 &&
                           Math.Abs(r - target.Right) < 0.25 && Math.Abs(b - target.Bottom) < 0.25;
            shown = settled ? target : new Rect(new Point(l, t), new Point(r, b));
            Apply();
            if (settled) StopFrames();
        }

        void Apply()
        {
            var r = shown;
            if (r.IsEmpty) return;
            double rr = Radius(r);
            hole.Rect = r;
            hole.RadiusX = hole.RadiusY = rr;
            Put(edgeLight, Rect.Inflate(r, 1, 1));
            edgeLight.RadiusX = edgeLight.RadiusY = rr > 0 ? rr + 1 : 0;
            Put(edgeDark, Rect.Inflate(r, 2, 2));
            edgeDark.RadiusX = edgeDark.RadiusY = rr > 0 ? rr + 2 : 0;
            corners.Data = CornerMarks(Rect.Inflate(r, 1.5, 1.5), rr > 0 ? rr + 1.5 : 0);

            var px = PixelRect(target);
            sizeText.Text = px.Width + " × " + px.Height;
            sizeTag.Measure(Unbounded);
            var s = sizeTag.DesiredSize;
            double x = r.Left, y = r.Top - s.Height - 8;
            if (y < 6) { x = r.Left + 8; y = r.Top + 8; }
            x = Math.Max(6, Math.Min(x, root.ActualWidth - s.Width - 6));
            Canvas.SetLeft(sizeTag, x);
            Canvas.SetTop(sizeTag, y);

            if (toolbar.Visibility == Visibility.Visible) PlaceToolbar(r);
        }

        static void Put(FrameworkElement e, Rect r)
        {
            Canvas.SetLeft(e, r.X);
            Canvas.SetTop(e, r.Y);
            e.Width = r.Width;
            e.Height = r.Height;
        }

        /// <summary>Corner radius for a selection, never more than half its shorter side.</summary>
        double Radius(Rect r) => Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));

        /// <summary>Four corner brackets that follow the rounded corners of the selection.</summary>
        static Geometry CornerMarks(Rect r, double rr)
        {
            double half = Math.Min(r.Width, r.Height) / 2;
            rr = Math.Min(rr, half);
            double l = Math.Min(rr + 14, half);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                Mark(c, r.TopLeft, new Vector(0, 1), new Vector(1, 0), l, rr);
                Mark(c, r.TopRight, new Vector(-1, 0), new Vector(0, 1), l, rr);
                Mark(c, r.BottomRight, new Vector(0, -1), new Vector(-1, 0), l, rr);
                Mark(c, r.BottomLeft, new Vector(1, 0), new Vector(0, -1), l, rr);
            }
            g.Freeze();
            return g;
        }

        /// <summary>From <paramref name="a"/>·l along one edge, round the corner, to <paramref name="b"/>·l along the other (clockwise).</summary>
        static void Mark(StreamGeometryContext c, Point corner, Vector a, Vector b, double l, double rr)
        {
            c.BeginFigure(corner + a * l, false, false);
            if (rr > 0.5)
            {
                c.LineTo(corner + a * rr, true, true);
                c.ArcTo(corner + b * rr, new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, true);
            }
            else c.LineTo(corner, true, true);
            c.LineTo(corner + b * l, true, true);
        }

        void SetChrome(bool visible)
        {
            foreach (UIElement e in new UIElement[] { edgeLight, edgeDark, corners, sizeTag })
            {
                e.BeginAnimation(OpacityProperty, null);
                e.Opacity = 1;
                e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------------------------------------------------------------- toolbar

        Border BuildToolbar()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ToolButton("", L.T("Copiar", "Copy"), () => Confirm(CaptureAction.Copy)));
            row.Children.Add(ToolButton("", L.T("Guardar", "Save"), () => Confirm(CaptureAction.Save)));
            row.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 7, 4, 7), Background = Ui.Brush(0x29FFFFFF) });
            row.Children.Add(ToolButton("", null, () => session.Cancel()));

            return new Border
            {
                Child = row,
                Background = Ui.Brush(0xF21E1E1E),
                BorderBrush = Ui.Brush(0x1FFFFFFF),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Cursor = Cursors.Arrow,
                Visibility = Visibility.Collapsed,
                RenderTransform = toolbarShift,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.35 },
            };
        }

        static Border ToolButton(string glyph, string label, Action click)
        {
            var fg = Ui.Brush(0xFFF0F0F0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 14, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            if (label != null)
                row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = fg, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });

            var bg = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            var button = new Border
            {
                Child = row,
                Background = bg,
                CornerRadius = new CornerRadius(5),
                Padding = label != null ? new Thickness(11, 7, 13, 7) : new Thickness(10, 7, 10, 7),
                Cursor = Cursors.Hand,
            };

            Action<byte> tint = a => bg.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(a, 255, 255, 255), new Duration(Ui.Ms(120))));
            button.MouseEnter += (s, e) => tint(0x24);
            button.MouseLeave += (s, e) => tint(0);
            button.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                button.CaptureMouse();
                tint(0x14);
            };
            button.MouseLeftButtonUp += (s, e) =>
            {
                if (!button.IsMouseCaptured) return;
                e.Handled = true;
                button.ReleaseMouseCapture();
                var p = e.GetPosition(button);
                bool inside = p.X >= 0 && p.Y >= 0 && p.X <= button.ActualWidth && p.Y <= button.ActualHeight;
                tint(inside ? (byte)0x24 : (byte)0);
                if (inside) click();
            };
            return button;
        }

        void ShowToolbar()
        {
            toolbar.Visibility = Visibility.Visible;
            PlaceToolbar(shown);
            toolbar.BeginAnimation(OpacityProperty, Ui.Tween(0, 1, 180));
            toolbarShift.BeginAnimation(TranslateTransform.YProperty, Ui.Tween(6, 0, 260));
        }

        void HideToolbar()
        {
            toolbar.BeginAnimation(OpacityProperty, null);
            toolbar.Visibility = Visibility.Collapsed;
        }

        void PlaceToolbar(Rect r)
        {
            toolbar.Measure(Unbounded);
            var s = toolbar.DesiredSize;
            double w = root.ActualWidth, h = root.ActualHeight;
            double x = r.Right - s.Width;
            double y = r.Bottom + 12;
            if (y + s.Height > h - 8)
            {
                y = r.Top - s.Height - 12;
                if (y < 8) y = r.Bottom - s.Height - 12;   // no room outside: sit inside the selection
            }
            x = Math.Max(8, Math.Min(x, w - s.Width - 8));
            Canvas.SetLeft(toolbar, x);
            Canvas.SetTop(toolbar, y);
        }

        // ---------------------------------------------------------------- finishing

        void Confirm(CaptureAction action)
        {
            if (mode != Mode.Selected) return;
            var px = PixelRect(target);
            if (px.Width < 1 || px.Height < 1) return;

            StopFrames();
            shown = target;
            Apply();
            var image = Imaging.Crop(shot, px, Radius(target) * ScaleX, Math.Round(96 * ScaleX));
            var result = new CaptureResult { Image = image, Action = action, Screen = screen };

            if (action == CaptureAction.Save)
            {
                string path = null;
                if (!Settings.QuickSave)
                {
                    // The dialog is owned by the overlay, so it shows above it while the selection stays visible.
                    mode = Mode.Busy;
                    HideToolbar();
                    Cursor = Cursors.Arrow;
                    // Borrow the right to activate just long enough for the dialog to open with the keyboard.
                    var loan = Native.BorrowForeground();
                    Ui.After(200, loan.Dispose);   // runs inside the dialog's own message loop
                    path = Imaging.AskSavePath(this);
                    loan.Dispose();
                    session.RestoreFocus();   // the dialog had it
                    if (path == null)
                    {
                        mode = Mode.Selected;
                        Cursor = Cursors.Cross;
                        ShowToolbar();
                        return;
                    }
                }
                try
                {
                    if (path == null) path = Settings.NextSavePath();
                    result.SavedPath = path;
                    var where = screen;
                    Imaging.SavePngAsync(image, path, error => Toast.Show(where, L.T("No se pudo guardar", "Couldn't save"), error));
                    if (Settings.CopyOnSave) Copy(result);
                }
                catch (Exception ex) { result.Error = ex.Message; }
            }
            else
            {
                Copy(result);
            }

            // Encoding and writing run in the background, so the animation starts right away and stays smooth.
            ShutterSound.Play();
            mode = Mode.Closing;
            session.Complete(this, result);
        }

        void Copy(CaptureResult result)
        {
            result.Copied = true;   // a failure is reported by its own toast once the clipboard has been tried
            var where = screen;
            Imaging.CopyToClipboardAsync(result.Image, hwnd, ok =>
            {
                if (!ok) Toast.Show(where, L.T("No se pudo copiar", "Couldn't copy"), L.T("Otro programa está usando el portapapeles", "Another program is using the clipboard"));
            });
        }

        /// <summary>Where the thumbnail's picture rests (inside its frame), in this window's DIPs: bottom-right of the work area.</summary>
        Rect ThumbTarget(Size picture)
        {
            var b = screen.Bounds;
            var area = screen.WorkingArea;
            var framed = ThumbLayout.Framed(picture);
            double right = (area.Right - b.X) / ScaleX, bottom = (area.Bottom - b.Y) / ScaleY;
            return new Rect(right - ThumbLayout.Margin - framed.Width + ThumbLayout.Frame,
                            bottom - ThumbLayout.Margin - framed.Height + ThumbLayout.Frame,
                            picture.Width, picture.Height);
        }

        /// <summary>
        /// macOS-style hand-off: the capture shrinks and flies to the bottom-right corner with a growing shadow while
        /// the dimming dissolves. On arrival <paramref name="arrived"/> lets the thumbnail stack take it at the same
        /// spot, and the overlay hides right after.
        /// </summary>
        public void PlayConfirm(BitmapSource image, Action arrived)
        {
            const double travel = 460, start = 30;
            mode = Mode.Closing;
            StopFrames();
            Cursor = Cursors.Arrow;
            HideToolbar();
            foreach (UIElement e in new UIElement[] { edgeLight, edgeDark, corners, sizeTag, hint })
                Ui.Fade(e, 0, 120);

            var r = shown;
            double r0 = Radius(r);
            hole.Rect = Rect.Empty;   // dim everything; the card carries the bright selection on top

            var thumb = ThumbLayout.Fit(r.Width, r.Height);
            var end = ThumbTarget(thumb);
            double s = thumb.Width / r.Width;

            // Everything that travels lives in one group, in the selection's own (unscaled) coordinates, so a single
            // transform moves it. Lengths that must end at a given on-screen size are divided by the final scale.
            var scale = new ScaleTransform(1, 1);
            var shift = new TranslateTransform();
            var group = new Canvas { RenderTransform = new TransformGroup { Children = new TransformCollection { scale, shift } } };
            Canvas.SetLeft(group, r.X);
            Canvas.SetTop(group, r.Y);

            double frame = ThumbLayout.Frame / s;
            var border = new Rectangle
            {
                Width = r.Width + 2 * frame,
                Height = r.Height + 2 * frame,
                RadiusX = ThumbLayout.OuterRadius / s,
                RadiusY = ThumbLayout.OuterRadius / s,
                Fill = Brushes.White,
                Opacity = 0,
            };
            Canvas.SetLeft(border, -frame);
            Canvas.SetTop(border, -frame);

            var clip = new RectangleGeometry(new Rect(0, 0, r.Width, r.Height), r0, r0);
            var card = new Image { Source = image, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill, Clip = clip };
            RenderOptions.SetBitmapScalingMode(card, BitmapScalingMode.HighQuality);

            group.Children.Add(border);
            group.Children.Add(card);
            var stage = new Canvas();
            stage.Children.Add(group);
            // The shadow sits on an untransformed parent, so it keeps its size while the card shrinks.
            var shadow = ThumbLayout.Shadow(0);
            flight = new Grid { Children = { stage }, Effect = shadow, IsHitTestVisible = false };
            root.Children.Add(flight);

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.Tween(1, s, travel, start, Ui.Glide));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Ui.Tween(1, s, travel, start, Ui.Glide));
            shift.BeginAnimation(TranslateTransform.XProperty, Ui.Tween(0, end.X - r.X, travel, start, Ui.Glide));
            shift.BeginAnimation(TranslateTransform.YProperty, Ui.Tween(0, end.Y - r.Y, travel, start, Ui.Glide));
            // Corners end at the thumbnail's radius once scaled down, while the white frame fades in around them.
            clip.BeginAnimation(RectangleGeometry.RadiusXProperty, Ui.Tween(r0, ThumbLayout.InnerRadius / s, travel, start, Ui.Glide));
            clip.BeginAnimation(RectangleGeometry.RadiusYProperty, Ui.Tween(r0, ThumbLayout.InnerRadius / s, travel, start, Ui.Glide));
            border.BeginAnimation(OpacityProperty, Ui.Tween(0, 1, 320, start + 100));
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, Ui.Tween(0, ThumbLayout.ShadowOpacity, 300, 80));
            dim.BeginAnimation(OpacityProperty, Ui.To(0, 380, 100, Ui.EaseInOut));

            Ui.After(start + travel + 20, () =>
            {
                arrived();
                // The thumbnail has it now: after a frame (for its window to appear), drop the flying copy so the
                // landing bounce is not hidden under it, and uncover the screen shortly after.
                var landed = flight;
                Ui.After(30, () => { if (flight == landed && landed != null) landed.Visibility = Visibility.Collapsed; });
                FinishLater(90);
            });
        }

        public void Dismiss()
        {
            mode = Mode.Closing;
            StopFrames();
            Cursor = Cursors.Arrow;
            foreach (UIElement e in new UIElement[] { dim, edgeLight, edgeDark, corners, sizeTag, toolbar, hint })
                Ui.Fade(e, 0, 180);
            FinishLater(200);
        }

        void FinishLater(double ms)
        {
            int armed = generation;
            Ui.After(ms, () => { if (armed == generation) Finish(); });
        }
    }
}
