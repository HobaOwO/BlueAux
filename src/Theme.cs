using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlueAux
{
    // Follows the Windows light/dark setting and accent colour, and gives windows the Mica backdrop on Windows 11.
    static class Theme
    {
        static ResourceDictionary current;
        static Application app;

        public static bool IsDark { get; private set; }
        public static bool UseMica { get; private set; }

        public static void Initialize(Application application)
        {
            app = application;
            UseMica = WindowsBuild() >= 22621;
            Apply();
            // Switching light/dark or the accent colour arrives as a "General" preference change.
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    app.Dispatcher.BeginInvoke(new Action(Apply));
            };
        }

        static void Apply()
        {
            IsDark = ReadIsDark();
            var accent = ReadAccent(IsDark ? 1 : 4);
            var accentText = ReadAccent(IsDark ? 0 : 5);
            var d = new ResourceDictionary();

            // Colour tokens from Fluent 2, the Windows 11 design system. Each key names the Fluent token it
            // follows; Styles.xaml uses only these keys, so both themes stay consistent.
            if (IsDark)
            {
                Add(d, "TextPrimary", 0xFFFFFFFF);        // TextFillColorPrimary
                Add(d, "TextSecondary", 0xC5FFFFFF);      // TextFillColorSecondary
                Add(d, "TextTertiary", 0x87FFFFFF);       // TextFillColorTertiary
                Add(d, "WindowBackground", 0xFF202020);   // SolidBackgroundFillColorBase
                Add(d, "CardFill", UseMica ? 0x0DFFFFFF : 0xFF2B2B2B);   // CardBackgroundFillColorDefault
                Add(d, "CardStroke", UseMica ? 0x19000000 : 0xFF1D1D1D); // CardStrokeColorDefault
                Add(d, "ControlFill", 0x0FFFFFFF);        // ControlFillColorDefault
                Add(d, "ControlFillHover", 0x15FFFFFF);   // ControlFillColorSecondary
                Add(d, "ControlFillPressed", 0x08FFFFFF); // ControlFillColorTertiary
                Add(d, "ControlStroke", 0x12FFFFFF);      // ControlStrokeColorDefault
                Add(d, "SubtleHover", 0x0FFFFFFF);        // SubtleFillColorSecondary
                Add(d, "SubtlePressed", 0x0AFFFFFF);      // SubtleFillColorTertiary
                Add(d, "DividerStroke", 0x15FFFFFF);            // DividerStrokeColorDefault
                Add(d, "Input", 0x0FFFFFFF);              // ControlFillColorDefault
                Add(d, "InputFocused", 0xB31E1E1E);       // ControlFillColorInputActive
                Add(d, "HeroNeutral", 0x15FFFFFF);        // ControlFillColorSecondary
                Add(d, "Caution", 0xFFFCE100);            // SystemFillColorCaution
                Add(d, "CautionSoft", 0xFF433519);        // SystemFillColorCautionBackground
                Add(d, "OnAccent", 0xFF000000);           // TextOnAccentFillColorPrimary
                Add(d, "Tooltip", 0xFF2C2C2C);            // AcrylicInAppFillColorDefault (solid)
            }
            else
            {
                Add(d, "TextPrimary", 0xE4000000);
                Add(d, "TextSecondary", 0x9E000000);
                Add(d, "TextTertiary", 0x72000000);
                Add(d, "WindowBackground", 0xFFF3F3F3);
                Add(d, "CardFill", UseMica ? 0xB3FFFFFF : 0xFFFFFFFF);
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
            }

            // AccentFillColorDefault / Secondary / Tertiary, and AccentTextFillColorPrimary for links.
            d["Accent"] = Frozen(accent);
            d["AccentHover"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
            d["AccentPressed"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
            d["AccentSoft"] = Frozen(Color.FromArgb(0x59, accent.R, accent.G, accent.B));
            d["AccentText"] = Frozen(accentText);

            var merged = app.Resources.MergedDictionaries;
            if (current != null) merged.Remove(current);
            merged.Insert(0, d);
            current = d;

            foreach (Window window in app.Windows) ApplyToWindow(window);
        }

        public static void ApplyToWindow(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            if (!UseMica) return;

            window.Background = Brushes.Transparent;
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null && source.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Native.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int backdrop = Native.DwmBackdropMica;
            Native.DwmSetWindowAttribute(hwnd, Native.DwmSystemBackdropType, ref backdrop, sizeof(int));
        }

        static void Add(ResourceDictionary d, string key, uint argb)
        {
            d[key] = Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }

        static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        static bool ReadIsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch
            {
                return false;
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

        static int WindowsBuild()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build;
                    if (key != null && int.TryParse(key.GetValue("CurrentBuildNumber") as string, out build)) return build;
                }
            }
            catch
            {
            }
            return 0;
        }
    }
}
