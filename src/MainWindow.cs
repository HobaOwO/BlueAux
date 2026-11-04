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

namespace PhoneSpeaker
{
    static class Xaml
    {
        public static object Load(string name)
        {
            using (Stream stream = typeof(Xaml).Assembly.GetManifestResourceStream("PhoneSpeaker." + name))
                return XamlReader.Load(stream);
        }
    }

    public sealed class MainWindow : Window
    {
        readonly AppSettings settings;
        readonly Action exitApp;
        readonly HeroAnimator hero;

        public MainWindow(MainViewModel viewModel, AppSettings settings, Action exitApp)
        {
            this.settings = settings;
            this.exitApp = exitApp;

            Title = "Phone Speaker";
            Width = 440;
            Height = Math.Min(740, SystemParameters.WorkArea.Height - 40);
            MinWidth = 380;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SetResourceReference(ForegroundProperty, "TextPrimary");
            SetResourceReference(BackgroundProperty, "WindowBackground");
            Icon = IconFactory.WindowIcon();

            var root = (FrameworkElement)Xaml.Load("MainView.xaml");
            Content = root;
            DataContext = viewModel;
            hero = new HeroAnimator(root, viewModel);
            IsVisibleChanged += (s, e) => hero.SetActive(IsVisible);

            // The idle-minutes box takes digits only and applies on Enter as well as when you click away.
            var idleBox = (TextBox)root.FindName("IdleBox");
            idleBox.PreviewTextInput += (s, e) => e.Handled = !e.Text.All(char.IsDigit);
            idleBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) idleBox.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            };
        }

        public bool AllowClose { get; set; }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Theme.ApplyToWindow(this);
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
            hero.Detach();
            DataContext = null;
        }
    }

    // The big circle at the top: pulses while waiting for a phone, shows dancing bars while playing.
    // Animations only run while the window is visible, so the app costs nothing in the tray.
    sealed class HeroAnimator
    {
        static readonly double[] BarSeconds = { 0.46, 0.62, 0.38, 0.55, 0.42 };

        readonly MainViewModel viewModel;
        readonly PropertyChangedEventHandler stateHandler;
        readonly Ellipse ring1, ring2, circle;
        readonly TextBlock glyph;
        readonly FrameworkElement bars;
        readonly Rectangle[] barShapes = new Rectangle[5];
        bool active;
        string appliedState;

        public HeroAnimator(FrameworkElement root, MainViewModel viewModel)
        {
            this.viewModel = viewModel;
            ring1 = (Ellipse)root.FindName("Ring1");
            ring2 = (Ellipse)root.FindName("Ring2");
            circle = (Ellipse)root.FindName("HeroCircle");
            glyph = (TextBlock)root.FindName("HeroGlyph");
            bars = (FrameworkElement)root.FindName("Bars");
            for (int i = 0; i < barShapes.Length; i++) barShapes[i] = (Rectangle)root.FindName("Bar" + i);
            stateHandler = (s, e) =>
            {
                if (e.PropertyName == "HeroState") Apply();
            };
            viewModel.PropertyChanged += stateHandler;
            Apply();
        }

        // The view model outlives the window; unhook so the closed window can be freed.
        public void Detach()
        {
            viewModel.PropertyChanged -= stateHandler;
            SetActive(false);
        }

        public void SetActive(bool value)
        {
            active = value;
            appliedState = null;
            Apply();
        }

        void Apply()
        {
            string state = viewModel.HeroState;
            bool lit = state == "Streaming" || state == "Waiting" || state == "Connecting";
            circle.SetResourceReference(Shape.FillProperty, lit ? "Accent" : "HeroNeutral");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, lit ? "OnAccent" : "TextSecondary");
            bars.Visibility = state == "Streaming" ? Visibility.Visible : Visibility.Collapsed;
            glyph.Visibility = state == "Streaming" ? Visibility.Collapsed : Visibility.Visible;

            string key = active ? state : "inactive";
            if (key == appliedState) return;
            appliedState = key;

            StopRing(ring1);
            StopRing(ring2);
            foreach (var bar in barShapes) ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (!active) return;

            if (state == "Waiting" || state == "Connecting")
            {
                double seconds = state == "Connecting" ? 1.3 : 2.4;
                Pulse(ring1, TimeSpan.Zero, seconds);
                Pulse(ring2, TimeSpan.FromSeconds(seconds / 2), seconds);
            }
            else if (state == "Streaming")
            {
                Breathe(ring1);
                for (int i = 0; i < barShapes.Length; i++)
                {
                    var bounce = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(BarSeconds[i]))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    ((ScaleTransform)barShapes[i].RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
                }
            }
        }

        static void Pulse(Ellipse ring, TimeSpan delay, double seconds)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            var grow = new DoubleAnimation(1.0, 1.7, TimeSpan.FromSeconds(seconds))
            {
                BeginTime = delay,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation(0.7, 0.0, TimeSpan.FromSeconds(seconds))
            {
                BeginTime = delay,
                RepeatBehavior = RepeatBehavior.Forever
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        static void Breathe(Ellipse ring)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            var grow = new DoubleAnimation(1.12, 1.28, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var fade = new DoubleAnimation(0.55, 0.3, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        static void StopRing(Ellipse ring)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }
}
