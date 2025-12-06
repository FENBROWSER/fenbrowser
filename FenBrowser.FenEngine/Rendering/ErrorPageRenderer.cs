using System;
using System.Net;

namespace FenBrowser.FenEngine.Rendering
{
    public static class ErrorPageRenderer
    {
        // Dynamic styles based on theme
        private static string GetPageStyle(bool isDark) => 
            $"background-color: {(isDark ? "#1e1e1e" : "#f8f9fa")}; " +
            $"color: {(isDark ? "#f0f0f0" : "#323130")}; " +
            "font-family: 'Segoe UI', system-ui, sans-serif; " +
            "margin: 0; padding: 0; " +
            "width: 100vw; height: 100vh; " +
            "overflow: hidden; " +
            "display: flex; align-items: center; justify-content: center;";

        private static string GetContainerStyle(bool isDark) => 
            $"width: 600px; padding: 40px; " +
            $"background-color: {(isDark ? "#252526" : "#ffffff")}; " +
            "border-radius: 8px; " +
            $"box-shadow: 0 4px 20px {(isDark ? "rgba(0,0,0,0.5)" : "rgba(0,0,0,0.15)")}; " +
            "text-align: center;";

        private const string IconStyle = "font-size: 64px; margin-bottom: 20px; display: block;";
        
        private static string GetTitleStyle(bool isDark) => 
            $"font-size: 24px; font-weight: 600; margin-bottom: 15px; color: {(isDark ? "#ffffff" : "#201f1e")}; display: block;";

        private static string GetMsgStyle(bool isDark) => 
            $"font-size: 16px; color: {(isDark ? "#d0d0d0" : "#605e5c")}; margin-bottom: 25px; line-height: 1.5; display: block;";

        private static string GetCodeStyle(bool isDark) => 
            "font-family: Consolas, monospace; " +
            $"color: {(isDark ? "#aaaaaa" : "#666666")}; " +
            "font-size: 13px; margin-bottom: 30px; " +
            $"background-color: {(isDark ? "#1a1a1a" : "#f3f2f1")}; " +
            "padding: 10px; border-radius: 4px; display: inline-block; " +
            $"border: 1px solid {(isDark ? "#333" : "#e1dfdd")};";

        private const string BtnStyle = "background-color: #0078d4; color: #ffffff; padding: 10px 30px; border-radius: 4px; text-decoration: none; font-weight: bold; font-size: 14px; display: inline-block;";

        private static bool IsDarkTheme()
        {
            try
            {
                var theme = FenBrowser.Core.BrowserSettings.Instance.Theme;
                // Default to Light if System (unless we can detect OS theme, but safe defaults are better for "System" implies standard app look)
                // Actually, Windows apps default to Light.
                return theme == FenBrowser.Core.ThemePreference.Dark;
            }
            catch
            {
                return false; // Safest fallback
            }
        }

        private static string RenderPage(string icon, string iconColor, string title, string message, string code, string actionUrl)
        {
            bool isDark = IsDarkTheme();

            return $@"
                <html>
                <body style=""{GetPageStyle(isDark)}"">
                    <div style=""{GetContainerStyle(isDark)}"">
                        <div style=""{IconStyle} color: {iconColor};"">{icon}</div>
                        <div style=""{GetTitleStyle(isDark)}"">{title}</div>
                        <div style=""{GetMsgStyle(isDark)}"">{message}</div>
                        <div style=""{GetCodeStyle(isDark)}"">{code}</div>
                        <a href=""{actionUrl}"" style=""{BtnStyle}"">Refresh</a>
                    </div>
                </body>
                </html>";
        }

        public static string RenderConnectionFailed(string url, string details)
        {
            return RenderPage(
                "&#127760;", "#0078d4", // Blue Globe
                "Hmm... can't reach this page",
                $"Check if there is a typo in <strong>{WebUtility.HtmlEncode(url)}</strong>.<br>If spelling is correct, try checking your connection.",
                WebUtility.HtmlEncode(details ?? "DNS_PROBE_FINISHED_NXDOMAIN"),
                url
            );
        }

        public static string RenderSslError(string url, string details)
        {
            return RenderPage(
                "&#9888;", "#d13438", // Red Warning
                "Your connection isn't private",
                $"Attackers might be trying to steal your information from <strong>{WebUtility.HtmlEncode(url)}</strong>.",
                "NET::ERR_CERT_COMMON_NAME_INVALID",
                url
            );
        }

        public static string RenderNoInternet(string url, string details)
        {
            return RenderPage(
                "&#129430;", "#797775", // Grey
                "No Internet",
                "Try checking the network cables, modem, and router.<br>Reconnecting to Wi-Fi.",
                "ERR_INTERNET_DISCONNECTED",
                url
            );
        }

        public static string RenderGenericError(string url, string title, string message, string details)
        {
            return RenderPage(
                "&#128533;", "#ffb900", // Yellow
                WebUtility.HtmlEncode(title),
                WebUtility.HtmlEncode(message),
                WebUtility.HtmlEncode(details),
                url
            );
        }
    }
}
