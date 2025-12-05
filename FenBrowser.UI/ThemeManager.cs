using Avalonia;
using Avalonia.Styling;
using FenBrowser.Core;
using System;

namespace FenBrowser.UI
{
    public static class ThemeManager
    {
        public static void Initialize()
        {
            ApplyTheme(BrowserSettings.Instance.Theme);
        }

        public static void SetTheme(ThemePreference preference)
        {
            if (BrowserSettings.Instance.Theme != preference)
            {
                BrowserSettings.Instance.Theme = preference;
                BrowserSettings.Instance.Save();
                ApplyTheme(preference);
            }
        }

        private static void ApplyTheme(ThemePreference preference)
        {
            if (Application.Current == null) return;

            switch (preference)
            {
                case ThemePreference.Light:
                    Application.Current.RequestedThemeVariant = ThemeVariant.Light;
                    break;
                case ThemePreference.Dark:
                    Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                    break;
                case ThemePreference.System:
                default:
                    Application.Current.RequestedThemeVariant = ThemeVariant.Default; // Follows system
                    break;
            }
        }
    }
}
