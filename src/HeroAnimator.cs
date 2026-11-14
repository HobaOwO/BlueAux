using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BlueAux
{
    // The visual in the now-playing card. The circle lights up in the accent colour with a soft halo when a
    // phone is connected (cross-fading, with one ring burst on connect), pulses while waiting for a phone,
    // shows a dotted ring while nothing is paired, and dancing bars while playing. When the phone names a
    // song, the cover takes the circle's place, a record slides out from behind it and spins, the song's
    // colours tint the card, and a new song's text rises into place. Animations only run while the window
    // is visible, so the app costs nothing in the tray, and are skipped when Motion is off (then the bars
    // and the record stand still).
    sealed class HeroAnimator
    {
        static readonly double[] BarSeconds = { 0.46, 0.62, 0.38, 0.55, 0.42 };
        const double SlotWidth = 64, SlotWithRecord = 90, RecordOffset = 30;

        readonly MainViewModel viewModel;
        readonly PropertyChangedEventHandler handler;
        readonly Ellipse glow, ring1, ring2, emptyRing, accentCircle;
        readonly TextBlock glyph;
        readonly FrameworkElement bars, heroText, slot, circleGroup, artGroup, disc, art, ambient;
        readonly Rectangle[] barShapes = new Rectangle[5];
        bool active;
        string appliedState;
        bool appliedArt;
        string shownTitle;
        ImageSource shownArt;

        public HeroAnimator(FrameworkElement root, MainViewModel viewModel)
        {
            this.viewModel = viewModel;
            glow = (Ellipse)root.FindName("Glow");
            ring1 = (Ellipse)root.FindName("Ring1");
            ring2 = (Ellipse)root.FindName("Ring2");
            emptyRing = (Ellipse)root.FindName("EmptyRing");
            accentCircle = (Ellipse)root.FindName("AccentCircle");
            glyph = (TextBlock)root.FindName("HeroGlyph");
            bars = (FrameworkElement)root.FindName("Bars");
            heroText = (FrameworkElement)root.FindName("HeroText");
            slot = (FrameworkElement)root.FindName("HeroVisual");
            circleGroup = (FrameworkElement)root.FindName("CircleGroup");
            artGroup = (FrameworkElement)root.FindName("ArtGroup");
            disc = (FrameworkElement)root.FindName("Disc");
            art = (FrameworkElement)root.FindName("Art");
            ambient = (FrameworkElement)root.FindName("Ambient");
            for (int i = 0; i < barShapes.Length; i++) barShapes[i] = (Rectangle)root.FindName("Bar" + i);
            shownTitle = viewModel.HeroTitle;
            shownArt = viewModel.HeroArt;
            handler = (s, e) =>
            {
                if (e.PropertyName == "HeroState" || e.PropertyName == "HeroSong") Apply();
                else if (e.PropertyName == "HeroTitle") TitleChanged();
                else if (e.PropertyName == "HeroArt") ArtChanged();
            };
            viewModel.PropertyChanged += handler;
            Apply();
        }

        // The view model outlives the window; unhook so the closed window can be freed.
        public void Detach()
        {
            viewModel.PropertyChanged -= handler;
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
            bool playing = state == "Streaming";
            bool showArt = playing && viewModel.HeroSong;
            glyph.SetResourceReference(TextBlock.ForegroundProperty, lit ? "OnAccent" : "TextSecondary");
            bars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            glyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;

            string key = (active ? state : "inactive") + (showArt ? "/art" : "");
            if (key == appliedState) return;
            bool wasPlaying = appliedState != null && appliedState.StartsWith("Streaming");
            appliedState = key;

            StopRing(ring1);
            StopRing(ring2);
            foreach (var bar in barShapes) ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ((RotateTransform)emptyRing.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
            Spin(false);

            // The accent and its halo fade in and out rather than snapping (instantly when Motion is off).
            Fade(accentCircle, lit ? 1 : 0, 0.25);
            Fade(glow, playing ? 1 : lit ? 0.6 : 0, 0.25);
            Fade(emptyRing, state == "Empty" ? 1 : 0, 0.25);
            ShowArt(showArt);
            if (!active || !Motion.Enabled) return;

            if (state == "Waiting" || state == "Connecting")
            {
                double seconds = state == "Connecting" ? 1.3 : 2.4;
                Pulse(ring1, TimeSpan.Zero, seconds, true);
                Pulse(ring2, TimeSpan.FromSeconds(seconds / 2), seconds, true);
            }
            else if (showArt)
            {
                Spin(true);
            }
            else if (state == "Streaming")
            {
                // One ring bursts outward on connect, then the halo breathes.
                if (!wasPlaying) Pulse(ring2, TimeSpan.Zero, 0.6, false);
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
            else if (state == "Empty")
            {
                var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(24)) { RepeatBehavior = RepeatBehavior.Forever };
                ((RotateTransform)emptyRing.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, turn);
            }
        }

        // The cover and the record come in (the cover pops up, the record slides out from behind it and the
        // slot widens to make room), or go away again, with the card's tint following the cover.
        void ShowArt(bool show)
        {
            if (show == appliedArt)
            {
                Tint();
                return;
            }
            appliedArt = show;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var discMove = (TranslateTransform)((TransformGroup)disc.RenderTransform).Children[1];
            var artScale = (ScaleTransform)art.RenderTransform;
            if (show)
            {
                artGroup.Visibility = Visibility.Visible;
                artGroup.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.Time(0.25)));
                artScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, Motion.Time(0.35)) { EasingFunction = ease });
                artScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, Motion.Time(0.35)) { EasingFunction = ease });
                discMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, RecordOffset, Motion.Time(0.45)) { BeginTime = Motion.Time(0.2), EasingFunction = ease });
                slot.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(SlotWithRecord, Motion.Time(0.35)) { EasingFunction = ease });
                Fade(circleGroup, 0, 0.15);
            }
            else
            {
                discMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Motion.Time(0.25)) { EasingFunction = ease });
                slot.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(SlotWidth, Motion.Time(0.25)) { EasingFunction = ease });
                Fade(circleGroup, 1, 0.25);
                // Hidden once it has faded, so the circle underneath gets the clicks and the layout.
                var hide = new DoubleAnimation(0, Motion.Time(0.2));
                hide.Completed += (s, e) => { if (!appliedArt) artGroup.Visibility = Visibility.Collapsed; };
                artGroup.BeginAnimation(UIElement.OpacityProperty, hide);
            }
            Tint();
        }

        void Tint()
        {
            double opacity = 0;
            if (appliedArt && viewModel.HeroArt != null)
            {
                object value = ambient.TryFindResource("AmbientOpacity");
                opacity = value is double ? (double)value : 0.4;
            }
            ambient.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, Motion.Time(0.6)));
        }

        void Spin(bool on)
        {
            var rotate = (RotateTransform)((TransformGroup)disc.RenderTransform).Children[0];
            if (!on)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                return;
            }
            var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.6)) { RepeatBehavior = RepeatBehavior.Forever };
            rotate.BeginAnimation(RotateTransform.AngleProperty, turn);
        }

        // The real cover arrived (or the song changed): it pops in, and the tint follows.
        void ArtChanged()
        {
            var image = viewModel.HeroArt;
            bool changed = image != shownArt;
            shownArt = image;
            Tint();
            if (!changed || !appliedArt || !Motion.Enabled) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var artScale = (ScaleTransform)art.RenderTransform;
            artScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromSeconds(0.3)) { EasingFunction = ease });
            artScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromSeconds(0.3)) { EasingFunction = ease });
        }

        // A new song while playing: the title and artist rise into place.
        void TitleChanged()
        {
            string title = viewModel.HeroTitle;
            if (title == shownTitle) return;
            bool swap = shownTitle != null && viewModel.HeroState == "Streaming" && appliedState != null && appliedState.StartsWith("Streaming");
            shownTitle = title;
            if (!swap || !Motion.Enabled) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            heroText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)));
            ((TranslateTransform)heroText.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease });
        }

        static void Fade(UIElement element, double to, double seconds)
        {
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, Motion.Time(seconds)));
        }

        static void Pulse(Ellipse ring, TimeSpan delay, double seconds, bool forever)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            var repeat = forever ? RepeatBehavior.Forever : new RepeatBehavior(1);
            var grow = new DoubleAnimation(1.0, 1.45, TimeSpan.FromSeconds(seconds))
            {
                BeginTime = delay,
                RepeatBehavior = repeat,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation(0.7, 0.0, TimeSpan.FromSeconds(seconds))
            {
                BeginTime = delay,
                RepeatBehavior = repeat
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
