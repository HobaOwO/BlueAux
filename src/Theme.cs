using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PhoneSpeaker
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
            var accent = ReadAccent();
            var d = new ResourceDictionary();

            if (IsDark)
            {
                Add(d, "TextPrimary", 0xFFFFFFFF);
                Add(d, "TextSecondary", 0xC8FFFFFF);
                Add(d, "TextTertiary", 0x8AFFFFFF);
                Add(d, "WindowBackground", 0xFF202020);
                Add(d, "CardFill", UseMica ? 0x0EFFFFFF : 0xFF2B2B2B);
                Add(d, "CardStroke", UseMica ? 0x1A000000 : 0xFF1D1D1D);
                Add(d, "CardSolid", 0xFF2C2C2C);
                Add(d, "Subtle", 0x12FFFFFF);
                Add(d, "SubtleHover", 0x1CFFFFFF);
                Add(d, "SubtlePressed", 0x0AFFFFFF);
                Add(d, "ControlStroke", 0x14FFFFFF);
                Add(d, "Input", 0x0FFFFFFF);
                Add(d, "InputFocused", 0xB31E1E1E);
                Add(d, "HeroNeutral", 0x1FFFFFFF);
                Add(d, "Success", 0xFF6CCB5F);
                Add(d, "Caution", 0xFFFCE100);
                Add(d, "Neutral", 0xFF9D9D9D);
                Add(d, "OnAccent", 0xFF000000);
                Add(d, "Tooltip", 0xFF2C2C2C);
            }
            else
            {
                Add(d, "TextPrimary", 0xE4000000);
                Add(d, "TextSecondary", 0x9E000000);
                Add(d, "TextTertiary", 0x72000000);
                Add(d, "WindowBackground", 0xFFF3F3F3);
                Add(d, "CardFill", UseMica ? 0xB3FFFFFF : 0xFFFFFFFF);
                Add(d, "CardStroke", 0x12000000);
                Add(d, "CardSolid", 0xFFFBFBFB);
                Add(d, "Subtle", 0xB3FFFFFF);
                Add(d, "SubtleHover", 0x80F9F9F9);
                Add(d, "SubtlePressed", 0x4DF9F9F9);
                Add(d, "ControlStroke", 0x18000000);
                Add(d, "Input", 0xB3FFFFFF);
                Add(d, "InputFocused", 0xFFFFFFFF);
                Add(d, "HeroNeutral", 0x0F000000);
                Add(d, "Success", 0xFF0F7B0F);
                Add(d, "Caution", 0xFF9D5D00);
                Add(d, "Neutral", 0xFF8A8A8A);
                Add(d, "OnAccent", 0xFFFFFFFF);
                Add(d, "Tooltip", 0xFFF9F9F9);
            }

            d["Accent"] = Frozen(accent);
            d["AccentHover"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
            d["AccentPressed"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
            d["AccentSoft"] = Frozen(Color.FromArgb(0x59, accent.R, accent.G, accent.B));

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

        // AccentPalette holds 8 RGBA shades from lightest to darkest; Windows uses
        // "light 2" (index 1) on dark backgrounds and "dark 1" (index 4) on light ones.
        static Color ReadAccent()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var palette = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (palette != null && palette.Length >= 32)
                    {
                        int i = (IsDark ? 1 : 4) * 4;
                        return Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
                    }
                }
            }
            catch
            {
            }
            return IsDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
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
