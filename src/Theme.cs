using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlueAux
{
    // The app's themes: "System" follows Windows' light/dark setting with the Mica backdrop on Windows 11,
    // and three looks of its own (OLED, Graphite, Paper) that draw the same everywhere. All of them use the
    // Windows accent colour. Styles.xaml only ever uses the colour tokens set here, through DynamicResource,
    // so a theme switches live: the token dictionary is swapped and every open window is told.
    static class Theme
    {
        public const string System = "System";
        public const string Oled = "Oled";
        public const string Graphite = "Graphite";
        public const string Paper = "Paper";
        public static readonly string[] All = { System, Oled, Graphite, Paper };

        static ResourceDictionary current;
        static Application app;
        static AppSettings settings;
        static bool windowsDark;

        public static event Action Changed;

        public static string Current { get; private set; }
        public static bool IsDark { get; private set; }
        public static bool UseMica { get; private set; }
        // True while Windows itself draws light taskbar and tray (SystemUsesLightTheme).
        public static bool WindowsLightShell { get; private set; }

        public static void Initialize(Application application, AppSettings appSettings)
        {
            app = application;
            settings = appSettings;
            Current = Normalize(settings.Theme);
            Apply();
            // Switching light/dark or the accent colour arrives as a "General" preference change.
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Apply();
                        Motion.Refresh();
                    }));
            };
        }

        public static string Normalize(string name)
        {
            return Array.IndexOf(All, name) >= 0 ? name : System;
        }

        public static string DisplayName(string name)
        {
            switch (name)
            {
                case Oled: return "OLED";
                case Graphite: return "Graphite";
                case Paper: return "Paper";
                default: return "Windows";
            }
        }

        public static void Select(string name)
        {
            name = Normalize(name);
            if (name == Current) return;
            Current = name;
            settings.Theme = name;
            settings.Save();
            Apply();
        }

        // The colours a theme's swatch in Settings is painted with.
        public static void Preview(string name, out Color window, out Color card, out Color text)
        {
            var d = Tokens(Normalize(name), ReadIsDark());
            window = ((SolidColorBrush)d["WindowBackground"]).Color;
            card = ((SolidColorBrush)d["CardFill"]).Color;
            text = ((SolidColorBrush)d["TextPrimary"]).Color;
            // Translucent tokens (System over Mica) are previewed over the window colour.
            card = Over(card, window);
        }

        static void Apply()
        {
            windowsDark = ReadIsDark();
            WindowsLightShell = ReadShellIsLight();
            IsDark = Current == System ? windowsDark : Current != Paper;
            UseMica = Current == System && WindowsBuild() >= 22621;

            var d = Tokens(Current, windowsDark);
            var merged = app.Resources.MergedDictionaries;
            if (current != null) merged.Remove(current);
            merged.Insert(0, d);
            current = d;

            foreach (Window window in app.Windows) ApplyToWindow(window);
            var handler = Changed;
            if (handler != null) handler();
        }

        // Colour tokens from Fluent 2, the Windows 11 design system. Each key names the Fluent token it
        // follows; the three custom themes keep the same keys with their own values.
        static ResourceDictionary Tokens(string theme, bool systemDark)
        {
            var d = new ResourceDictionary();
            bool mica = theme == System && WindowsBuild() >= 22621;
            bool dark = theme == System ? systemDark : theme != Paper;
            var accent = ReadAccent(dark ? 1 : 4);
            var accentText = ReadAccent(dark ? 0 : 5);

            switch (theme)
            {
                case Oled:
                    // True black, so OLED pixels switch off. Cards are outlined rather than lighter, and
                    // text is dimmed a touch below pure white (pure white on pure black halates).
                    Add(d, "TextPrimary", 0xEBFFFFFF);
                    Add(d, "TextSecondary", 0x9EFFFFFF);
                    Add(d, "TextTertiary", 0x6BFFFFFF);
                    Add(d, "WindowBackground", 0xFF000000);
                    Add(d, "CardFill", 0xFF000000);
                    Add(d, "CardStroke", 0x24FFFFFF);
                    Add(d, "ControlFill", 0x14FFFFFF);
                    Add(d, "ControlFillHover", 0x1FFFFFFF);
                    Add(d, "ControlFillPressed", 0x0AFFFFFF);
                    Add(d, "ControlStroke", 0x1AFFFFFF);
                    Add(d, "SubtleHover", 0x14FFFFFF);
                    Add(d, "SubtlePressed", 0x0AFFFFFF);
                    Add(d, "DividerStroke", 0x1AFFFFFF);
                    Add(d, "Input", 0x14FFFFFF);
                    Add(d, "InputFocused", 0xFF0A0A0A);
                    Add(d, "HeroNeutral", 0x1FFFFFFF);
                    Add(d, "Caution", 0xFFFCE100);
                    Add(d, "CautionSoft", 0xFF2B2410);
                    Add(d, "OnAccent", 0xFF000000);
                    Add(d, "Tooltip", 0xFF101010);
                    Add(d, "FocusOuter", 0xFFFFFFFF);
                    Add(d, "FocusInner", 0xFF000000);
                    d["HeroStroke"] = Hairline(0x2EFFFFFF, 0x0FFFFFFF);
                    d["HeroGlow"] = Glow(accent, 0x66);
                    d["CardShadowOpacity"] = 0.0;
                    d["CardShadowVisibility"] = Visibility.Collapsed;
                    d["WindowBorder"] = 0x1A1A1Au;
                    d["AmbientOpacity"] = 0.4;
                    break;

                case Graphite:
                    // Solid dark grey with depth by lightness, no strokes to speak of. Same on every PC.
                    Add(d, "TextPrimary", 0xFFFFFFFF);
                    Add(d, "TextSecondary", 0xC5FFFFFF);
                    Add(d, "TextTertiary", 0x87FFFFFF);
                    Add(d, "WindowBackground", 0xFF121212);
                    Add(d, "CardFill", 0xFF1C1C1C);
                    Add(d, "CardStroke", 0x0AFFFFFF);
                    Add(d, "ControlFill", 0x12FFFFFF);
                    Add(d, "ControlFillHover", 0x1AFFFFFF);
                    Add(d, "ControlFillPressed", 0x0AFFFFFF);
                    Add(d, "ControlStroke", 0x14FFFFFF);
                    Add(d, "SubtleHover", 0x12FFFFFF);
                    Add(d, "SubtlePressed", 0x0AFFFFFF);
                    Add(d, "DividerStroke", 0x14FFFFFF);
                    Add(d, "Input", 0x12FFFFFF);
                    Add(d, "InputFocused", 0xFF161616);
                    Add(d, "HeroNeutral", 0x1AFFFFFF);
                    Add(d, "Caution", 0xFFFCE100);
                    Add(d, "CautionSoft", 0xFF433519);
                    Add(d, "OnAccent", 0xFF000000);
                    Add(d, "Tooltip", 0xFF262626);
                    Add(d, "FocusOuter", 0xFFFFFFFF);
                    Add(d, "FocusInner", 0xFF121212);
                    d["HeroStroke"] = Hairline(0x1FFFFFFF, 0x08FFFFFF);
                    d["HeroGlow"] = Glow(accent, 0x4D);
                    d["CardShadowOpacity"] = 0.0;
                    d["CardShadowVisibility"] = Visibility.Collapsed;
                    d["WindowBorder"] = 0x2A2A2Au;
                    d["AmbientOpacity"] = 0.45;
                    break;

                case Paper:
                    // Warm off-white with white cards that sit on a soft shadow, and warm near-black ink.
                    Add(d, "TextPrimary", 0xFF1C1B1A);
                    Add(d, "TextSecondary", 0xA61C1B1A);
                    Add(d, "TextTertiary", 0x701C1B1A);
                    Add(d, "WindowBackground", 0xFFF7F5F2);
                    Add(d, "CardFill", 0xFFFFFFFF);
                    Add(d, "CardStroke", 0x14000000);
                    Add(d, "ControlFill", 0xFFFFFFFF);
                    Add(d, "ControlFillHover", 0xFFF3F1EE);
                    Add(d, "ControlFillPressed", 0xFFEBE8E4);
                    Add(d, "ControlStroke", 0x1F000000);
                    Add(d, "SubtleHover", 0x0A000000);
                    Add(d, "SubtlePressed", 0x06000000);
                    Add(d, "DividerStroke", 0x14000000);
                    Add(d, "Input", 0xFFFFFFFF);
                    Add(d, "InputFocused", 0xFFFFFFFF);
                    Add(d, "HeroNeutral", 0x0F000000);
                    Add(d, "Caution", 0xFF9D5D00);
                    Add(d, "CautionSoft", 0xFFFFF4CE);
                    Add(d, "OnAccent", 0xFFFFFFFF);
                    Add(d, "Tooltip", 0xFFFFFFFF);
                    Add(d, "FocusOuter", 0xFF1C1B1A);
                    Add(d, "FocusInner", 0xFFFFFFFF);
                    Add(d, "HeroStroke", 0x14000000);
                    d["HeroGlow"] = Glow(accent, 0x2E);
                    d["CardShadowOpacity"] = 0.10;
                    d["CardShadowVisibility"] = Visibility.Visible;
                    d["WindowBorder"] = 0xE3E0DBu;
                    d["AmbientOpacity"] = 0.3;
                    break;

                default:
                    if (dark)
                    {
                        Add(d, "TextPrimary", 0xFFFFFFFF);        // TextFillColorPrimary
                        Add(d, "TextSecondary", 0xC5FFFFFF);      // TextFillColorSecondary
                        Add(d, "TextTertiary", 0x87FFFFFF);       // TextFillColorTertiary
                        Add(d, "WindowBackground", 0xFF202020);   // SolidBackgroundFillColorBase
                        Add(d, "CardFill", mica ? 0x0DFFFFFF : 0xFF2B2B2B);   // CardBackgroundFillColorDefault
                        Add(d, "CardStroke", mica ? 0x19000000 : 0xFF1D1D1D); // CardStrokeColorDefault
                        Add(d, "ControlFill", 0x0FFFFFFF);        // ControlFillColorDefault
                        Add(d, "ControlFillHover", 0x15FFFFFF);   // ControlFillColorSecondary
                        Add(d, "ControlFillPressed", 0x08FFFFFF); // ControlFillColorTertiary
                        Add(d, "ControlStroke", 0x12FFFFFF);      // ControlStrokeColorDefault
                        Add(d, "SubtleHover", 0x0FFFFFFF);        // SubtleFillColorSecondary
                        Add(d, "SubtlePressed", 0x0AFFFFFF);      // SubtleFillColorTertiary
                        Add(d, "DividerStroke", 0x15FFFFFF);      // DividerStrokeColorDefault
                        Add(d, "Input", 0x0FFFFFFF);              // ControlFillColorDefault
                        Add(d, "InputFocused", 0xB31E1E1E);       // ControlFillColorInputActive
                        Add(d, "HeroNeutral", 0x15FFFFFF);        // ControlFillColorSecondary
                        Add(d, "Caution", 0xFFFCE100);            // SystemFillColorCaution
                        Add(d, "CautionSoft", 0xFF433519);        // SystemFillColorCautionBackground
                        Add(d, "OnAccent", 0xFF000000);           // TextOnAccentFillColorPrimary
                        Add(d, "Tooltip", 0xFF2C2C2C);            // AcrylicInAppFillColorDefault (solid)
                        Add(d, "FocusOuter", 0xFFFFFFFF);         // FocusStrokeColorOuter
                        Add(d, "FocusInner", 0xB3000000);         // FocusStrokeColorInner
                        d["HeroStroke"] = Hairline(0x24FFFFFF, 0x0AFFFFFF);
                        d["HeroGlow"] = Glow(accent, 0x4D);
                    }
                    else
                    {
                        Add(d, "TextPrimary", 0xE4000000);
                        Add(d, "TextSecondary", 0x9E000000);
                        Add(d, "TextTertiary", 0x72000000);
                        Add(d, "WindowBackground", 0xFFF3F3F3);
                        Add(d, "CardFill", mica ? 0xB3FFFFFF : 0xFFFFFFFF);
                        Add(d, "CardStroke", 0x0F000000);
                        Add(d, "ControlFill", 0xB3FFFFFF);
                        Add(d, "ControlFillHover", 0x80F9F9F9);
                        Add(d, "ControlFillPressed", 0x4DF9F9F9);
                        Add(d, "ControlStroke", 0x18000000);
                        Add(d, "SubtleHover", 0x09000000);
                        Add(d, "SubtlePressed", 0x06000000);
                        Add(d, "DividerStroke", 0x0F000000);
                        Add(d, "Input", 0xB3FFFFFF);
                        Add(d, "InputFocused", 0xFFFFFFFF);
                        Add(d, "HeroNeutral", 0x0F000000);
                        Add(d, "Caution", 0xFF9D5D00);
                        Add(d, "CautionSoft", 0xFFFFF4CE);
                        Add(d, "OnAccent", 0xFFFFFFFF);
                        Add(d, "Tooltip", 0xFFF9F9F9);
                        Add(d, "FocusOuter", 0xE4000000);
                        Add(d, "FocusInner", 0xB3FFFFFF);
                        Add(d, "HeroStroke", 0x0F000000);
                        d["HeroGlow"] = Glow(accent, 0x33);
                    }
                    d["CardShadowOpacity"] = 0.0;
                    d["CardShadowVisibility"] = Visibility.Collapsed;
                    d["WindowBorder"] = 0xFFFFFFFFu; // DwmColorDefault: Windows draws its own
                    d["AmbientOpacity"] = dark ? 0.45 : 0.3;
                    break;
            }

            // AccentFillColorDefault / Secondary / Tertiary, and AccentTextFillColorPrimary for links.
            d["Accent"] = Frozen(accent);
            d["AccentHover"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
            d["AccentPressed"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
            d["AccentSoft"] = Frozen(Color.FromArgb(0x59, accent.R, accent.G, accent.B));
            d["AccentText"] = Frozen(accentText);
            return d;
        }

        public static void ApplyToWindow(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int build = WindowsBuild();
            int dark = IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DwmUseImmersiveDarkMode, ref dark, sizeof(int));

            var source = HwndSource.FromHwnd(hwnd);
            if (source != null && source.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;

            if (UseMica)
            {
                window.Background = Brushes.Transparent;
                var margins = new Native.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
                int backdrop = Native.DwmBackdropMica;
                Native.DwmSetWindowAttribute(hwnd, Native.DwmSystemBackdropType, ref backdrop, sizeof(int));
            }
            else
            {
                // Back to a window that paints itself (the theme may have been Mica a moment ago).
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                if (build >= 22621)
                {
                    int backdrop = Native.DwmBackdropNone;
                    Native.DwmSetWindowAttribute(hwnd, Native.DwmSystemBackdropType, ref backdrop, sizeof(int));
                }
            }

            // Windows 11 lets the title bar take the theme's colours, so OLED is black up to the top edge.
            if (build < 22000) return;
            int caption = Native.DwmColorDefault, text = Native.DwmColorDefault, border = Native.DwmColorDefault;
            if (Current != System)
            {
                caption = ColorRef(((SolidColorBrush)current["WindowBackground"]).Color);
                text = ColorRef(Over(((SolidColorBrush)current["TextPrimary"]).Color, ((SolidColorBrush)current["WindowBackground"]).Color));
                uint rgb = (uint)current["WindowBorder"];
                border = (int)((rgb & 0xFF) << 16 | (rgb & 0xFF00) | (rgb >> 16 & 0xFF));
            }
            Native.DwmSetWindowAttribute(hwnd, Native.DwmCaptionColor, ref caption, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, Native.DwmTextColor, ref text, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, Native.DwmBorderColor, ref border, sizeof(int));
        }

        static int ColorRef(Color c)
        {
            return c.R | c.G << 8 | c.B << 16;
        }

        static Color Over(Color top, Color under)
        {
            double a = top.A / 255.0;
            return Color.FromRgb(
                (byte)Math.Round(top.R * a + under.R * (1 - a)),
                (byte)Math.Round(top.G * a + under.G * (1 - a)),
                (byte)Math.Round(top.B * a + under.B * (1 - a)));
        }

        static void Add(ResourceDictionary d, string key, uint argb)
        {
            d[key] = Frozen(ToColor(argb));
        }

        static Color ToColor(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // A 1 px stroke that catches the light at the top, the way Windows 11 widgets do.
        static Brush Hairline(uint top, uint bottom)
        {
            var brush = new LinearGradientBrush(ToColor(top), ToColor(bottom), 90);
            brush.Freeze();
            return brush;
        }

        // The soft accent halo behind the now-playing circle.
        static Brush Glow(Color accent, byte alpha)
        {
            var brush = new RadialGradientBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
            brush.Freeze();
            return brush;
        }

        static bool ReadIsDark()
        {
            return ReadPersonalize("AppsUseLightTheme") == 0;
        }

        static bool ReadShellIsLight()
        {
            return ReadPersonalize("SystemUsesLightTheme") == 1;
        }

        static int ReadPersonalize(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue(name);
                    return value is int ? (int)value : -1;
                }
            }
            catch
            {
                return -1;
            }
        }

        // AccentPalette holds 8 RGBA shades from lightest to darkest: light 3, light 2, light 1, base,
        // dark 1, dark 2, dark 3. Fluent fills with light 2 on dark backgrounds and dark 1 on light ones,
        // and colours text with light 3 / dark 2.
        static Color ReadAccent(int shade)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var palette = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (palette != null && palette.Length >= 32)
                    {
                        int i = shade * 4;
                        return Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
                    }
                }
            }
            catch
            {
            }
            switch (shade)
            {
                case 0: return Color.FromRgb(0x99, 0xEB, 0xFF);
                case 1: return Color.FromRgb(0x60, 0xCD, 0xFF);
                case 5: return Color.FromRgb(0x00, 0x3E, 0x92);
                default: return Color.FromRgb(0x00, 0x5F, 0xB8);
            }
        }

        static int cachedBuild = -1;

        static int WindowsBuild()
        {
            if (cachedBuild >= 0) return cachedBuild;
            cachedBuild = 0;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build;
                    if (key != null && int.TryParse(key.GetValue("CurrentBuildNumber") as string, out build)) cachedBuild = build;
                }
            }
            catch
            {
            }
            return cachedBuild;
        }
    }

    // Whether the UI animates, and how long for. Off when the Animations setting is off or Windows'
    // "Show animations" is off. Styles.xaml reads the durations with x:Static when it loads, so a change
    // reloads the styles and the view (App listens to Changed); code-behind animations ask each time.
    static class Motion
    {
        static AppSettings settings;

        public static event Action Changed;

        public static bool Enabled { get; private set; }

        public static void Initialize(AppSettings appSettings)
        {
            settings = appSettings;
            Enabled = Compute();
        }

        public static void Refresh()
        {
            bool enabled = Compute();
            if (enabled == Enabled) return;
            Enabled = enabled;
            var handler = Changed;
            if (handler != null) handler();
        }

        static bool Compute()
        {
            return settings != null && settings.Animations && SystemParameters.ClientAreaAnimation;
        }

        // Fluent's control durations: 83 ms for fast feedback, 167 ms for most changes, 250 ms for the big ones.
        public static Duration Fast { get { return new Duration(Time(0.083)); } }
        public static Duration Normal { get { return new Duration(Time(0.167)); } }
        public static Duration Slow { get { return new Duration(Time(0.25)); } }

        public static TimeSpan Time(double seconds)
        {
            return Enabled ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
    }
}
