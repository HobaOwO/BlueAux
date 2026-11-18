using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace BlueAux
{
    static class Xaml
    {
        public static object Load(string name)
        {
            using (Stream stream = typeof(Xaml).Assembly.GetManifestResourceStream("BlueAux." + name))
                return XamlReader.Load(stream);
        }
    }

    public sealed class MainWindow : Window
    {
        // The title bar is drawn by the app (no icon, no name): this tall, with Windows' own caption buttons at the right.
        const double CaptionHeight = 32;

        readonly MainViewModel viewModel;
        readonly AppSettings settings;
        readonly Action exitApp;
        readonly PropertyChangedEventHandler pageHandler;
        HeroAnimator hero;
        FrameworkElement root;
        ScrollViewer mainScroll, settingsScroll;
        Button maxButton;
        bool sized;

        public MainWindow(MainViewModel viewModel, AppSettings settings, Action exitApp)
        {
            this.viewModel = viewModel;
            this.settings = settings;
            this.exitApp = exitApp;

            Title = AppInfo.Name;
            // As tall as what it shows: the window follows the page's height (FitHeight), up to this limit,
            // and Settings scrolls past it.
            Width = 360;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Min(640, SystemParameters.WorkArea.Height - 40);
            MinWidth = 320;
            MinHeight = 200;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Segoe UI Variable's text optical size on Windows 11, Segoe UI before that.
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            // Fluent's Body size; Styles.xaml holds the rest of the type ramp.
            FontSize = 14;
            UseLayoutRounding = true;
            SetResourceReference(ForegroundProperty, "TextPrimary");
            SetResourceReference(BackgroundProperty, "WindowBackground");
            Icon = IconFactory.WindowIcon();
            Topmost = settings.AlwaysOnTop;

            // Our own title bar: the top strip of the content drags the window, the caption buttons are the
            // view's own (CaptionHook keeps snap layouts working), and the frame stays extended so Mica shows through.
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = CaptionHeight,
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
                ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
                CornerRadius = new CornerRadius(0)
            });
            StateChanged += (s, e) => UpdateMaximizedMargin();

            DataContext = viewModel;
            ReloadView();
            IsVisibleChanged += (s, e) => hero.SetActive(IsVisible);
            pageHandler = (s, e) =>
            {
                if (e.PropertyName == "IsSettingsOpen") Dispatcher.BeginInvoke(new Action(FitHeight), System.Windows.Threading.DispatcherPriority.Loaded);
                else if (e.PropertyName == "AlwaysOnTop") Topmost = viewModel.AlwaysOnTop;
            };
            viewModel.PropertyChanged += pageHandler;
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                // From the first layout on, the height is ours: set to the page now, animated later.
                if (sized) return;
                sized = true;
                SizeToContent = SizeToContent.Manual;
                var viewer = viewModel.IsSettingsOpen ? settingsScroll : mainScroll;
                double target = ActualHeight;
                if (viewer != null && viewer.ViewportHeight > 0) target += viewer.ExtentHeight - viewer.ViewportHeight;
                Height = Math.Max(MinHeight, Math.Min(MaxHeight, Math.Ceiling(target)));
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public bool AllowClose { get; set; }

        // Builds the view from MainView.xaml. Called again when the styles change (animations on/off):
        // the view model holds every bit of state, so nothing is lost.
        public void ReloadView()
        {
            if (hero != null) hero.Detach();
            root = (FrameworkElement)Xaml.Load("MainView.xaml");
            Content = root;
            UpdateMaximizedMargin();
            hero = new HeroAnimator(root, viewModel);
            hero.SetActive(IsVisible);

            // The title bar's buttons. Maximize is driven by Windows through the hit-test hook below.
            ((Button)root.FindName("MinButton")).Click += (s, e) => SystemCommands.MinimizeWindow(this);
            ((Button)root.FindName("CloseButton")).Click += (s, e) => Close();
            maxButton = (Button)root.FindName("MaxButton");
            maxButton.Click += (s, e) => ToggleMaximize();
            UpdateMaximizeGlyph();

            mainScroll = (ScrollViewer)root.FindName("MainScroll");
            settingsScroll = (ScrollViewer)root.FindName("SettingsScroll");
            foreach (var viewer in new[] { mainScroll, settingsScroll })
            {
                SmoothScroll.Attach(viewer);
                // Content came or went (a card appeared, a section unfolded): follow it.
                viewer.ScrollChanged += (s, e) =>
                {
                    if (e.ExtentHeightChange != 0 && sized) FitHeight();
                };
            }

            // The idle-minutes box takes digits only and applies on Enter as well as when you click away.
            var idleBox = (TextBox)root.FindName("IdleBox");
            idleBox.PreviewTextInput += (s, e) => e.Handled = !e.Text.All(char.IsDigit);
            idleBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) idleBox.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            };
        }

        // Maximized, Windows hangs the resize border off-screen; keep the content inside it.
        void UpdateMaximizedMargin()
        {
            if (root == null) return;
            root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
            UpdateMaximizeGlyph();
        }

        void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        void UpdateMaximizeGlyph()
        {
            if (maxButton == null) return;
            bool max = WindowState == WindowState.Maximized;
            maxButton.Content = max ? "\uE923" : "\uE922";
            maxButton.ToolTip = max ? "Restore" : "Maximize";
        }

        // Tells Windows where the maximize button is (HTMAXBUTTON), so Windows 11 shows its snap layouts
        // flyout over it. Windows then owns the mouse there, so hover, press and click come in as
        // non-client messages and are handled here.
        IntPtr CaptionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
                WM_NCMOUSELEAVE = 0x2A2, WM_MOUSEMOVE = 0x200, HTMAXBUTTON = 9;
            if (maxButton == null) return IntPtr.Zero;
            switch (msg)
            {
                case WM_NCHITTEST:
                    if (OverMaxButton(lParam))
                    {
                        handled = true;
                        return (IntPtr)HTMAXBUTTON;
                    }
                    break;
                case WM_NCMOUSEMOVE:
                    maxButton.Tag = (int)wParam == HTMAXBUTTON ? "hover" : null;
                    if ((int)wParam == HTMAXBUTTON) Native.TrackNonClientLeave(hwnd);
                    break;
                case WM_MOUSEMOVE:
                case WM_NCMOUSELEAVE:
                    maxButton.Tag = null;
                    break;
                case WM_NCLBUTTONDOWN:
                    if ((int)wParam == HTMAXBUTTON)
                    {
                        maxButton.Tag = "pressed";
                        handled = true;
                    }
                    break;
                case WM_NCLBUTTONUP:
                    if ((int)wParam == HTMAXBUTTON)
                    {
                        maxButton.Tag = null;
                        handled = true;
                        ToggleMaximize();
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        bool OverMaxButton(IntPtr lParam)
        {
            if (!maxButton.IsVisible) return false;
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            Point p = maxButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < maxButton.ActualWidth && p.Y < maxButton.ActualHeight;
        }

        // Grows or shrinks the window to the page it shows, up to MaxHeight, sliding rather than jumping,
        // and keeps it on screen while it grows.
        void FitHeight()
        {
            if (!sized || WindowState != WindowState.Normal) return;
            var viewer = viewModel.IsSettingsOpen ? settingsScroll : mainScroll;
            if (viewer == null || viewer.ViewportHeight <= 0) return;
            double target = ActualHeight + viewer.ExtentHeight - viewer.ViewportHeight;
            target = Math.Max(MinHeight, Math.Min(MaxHeight, Math.Ceiling(target)));
            if (Math.Abs(target - ActualHeight) < 1) return;

            var area = SystemParameters.WorkArea;
            if (Top + target > area.Bottom) Top = Math.Max(area.Top, area.Bottom - target);

            var grow = new DoubleAnimation(ActualHeight, target, Motion.Time(0.25)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            grow.Completed += (s, e) =>
            {
                BeginAnimation(HeightProperty, null);
                Height = target;
            };
            BeginAnimation(HeightProperty, grow);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // Without the system menu style, Windows stops drawing its own caption buttons under ours (seen with Mica).
            Native.SetWindowLong(hwnd, Native.GwlStyle, Native.GetWindowLong(hwnd, Native.GwlStyle) & ~Native.WsSysMenu);
            Theme.ApplyToWindow(this);
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (source != null) source.AddHook(CaptionHook);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            // With "keep running" on, the window really closes (to free its memory) while the app stays
            // in the tray; App builds a new one next time. Otherwise closing the window quits the app.
            if (AllowClose || settings.CloseToTray) return;
            e.Cancel = true;
            Dispatcher.BeginInvoke(exitApp);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            viewModel.PropertyChanged -= pageHandler;
            hero.Detach();
            DataContext = null;
        }
    }

    // Mouse-wheel scrolling that glides to where it's going instead of jumping there. Each notch moves the
    // target a step further; the offset then chases it a little every frame (an exponential ease), so any
    // number of notches at any speed join one smooth motion with no restarts. Off with Motion.
    static class SmoothScroll
    {
        const double NotchPixels = 72;
        // How quickly the offset closes in on the target: it covers 63 % of the gap in this many seconds.
        const double Tau = 0.07;

        public static void Attach(ScrollViewer viewer)
        {
            double target = 0, position = 0;
            bool running = false;
            long lastTicks = 0;
            TimeSpan lastFrame = TimeSpan.MinValue;
            EventHandler frame = null;
            frame = (s, e) =>
            {
                // Rendering fires for every render pass, and each scroll causes one, so step once per
                // real frame (same RenderingTime = same frame) or the loop runs hot and stutters.
                var args = e as System.Windows.Media.RenderingEventArgs;
                if (args != null)
                {
                    if (args.RenderingTime == lastFrame) return;
                    lastFrame = args.RenderingTime;
                }
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double dt = Math.Min(0.05, (now - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
                lastTicks = now;
                position += (target - position) * (1 - Math.Exp(-dt / Tau));
                if (Math.Abs(target - position) < 0.5)
                {
                    position = target;
                    running = false;
                    System.Windows.Media.CompositionTarget.Rendering -= frame;
                }
                viewer.ScrollToVerticalOffset(position);
            };
            viewer.PreviewMouseWheel += (s, e) =>
            {
                if (!Motion.Enabled || viewer.ScrollableHeight <= 0 || e.Delta == 0) return;
                e.Handled = true;
                if (!running)
                {
                    // Start from wherever the page is (it may have been dragged or resized meanwhile).
                    position = viewer.VerticalOffset;
                    target = position;
                }
                target = Math.Max(0, Math.Min(viewer.ScrollableHeight, target - e.Delta / 120.0 * NotchPixels));
                if (running || Math.Abs(target - position) < 0.5) return;
                running = true;
                lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                System.Windows.Media.CompositionTarget.Rendering += frame;
            };
            // The window closing or the view being rebuilt must not leave a frame handler behind.
            viewer.Unloaded += (s, e) =>
            {
                if (!running) return;
                running = false;
                System.Windows.Media.CompositionTarget.Rendering -= frame;
            };
        }
    }
}
