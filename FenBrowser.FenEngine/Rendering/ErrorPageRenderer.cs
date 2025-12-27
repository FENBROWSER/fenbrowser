using System;
using System.Net;

namespace FenBrowser.FenEngine.Rendering
{
    public static class ErrorPageRenderer
    {
        private static string GetPageStyle(bool isDark) =>
            $"background-color: {(isDark ? "#0c0c0c" : "#f6f7f9")};" +
            $"color: {(isDark ? "#ffffff" : "#1f2937")};" +
            "font-family: 'Segoe UI', 'Segoe UI Variable', system-ui, sans-serif;" +
            "margin:0;padding:0;width:100vw;height:100vh;" +
            "display:flex;align-items:center;justify-content:center;";

        private static string GetContainerStyle(bool isDark) =>
            $"background-color:{(isDark ? "#1b1b1b" : "#ffffff")};" +
            $"border:1px solid {(isDark ? "#333" : "#e5e7eb")};" +
            "border-radius:14px;" +
            "padding:48px;" +
            "max-width:560px;width:100%;" +
            "box-shadow:0 20px 40px rgba(0,0,0,0.08);";

        private static string GetTitleStyle(bool isDark) =>
            $"font-size:22px;font-weight:500;color:{(isDark ? "#ffffff" : "#1f2937")};margin-bottom:12px;";

        private static string GetMsgStyle(bool isDark) =>
            $"font-size:14px;color:{(isDark ? "#d1d5db" : "#374151")};line-height:1.6;margin-bottom:16px;";

        private static string GetMutedStyle(bool isDark) =>
            $"font-size:13px;color:{(isDark ? "#9ca3af" : "#6b7280")};line-height:1.5;";

        private static string GetCodeStyle(bool isDark) =>
            $"font-family:'Segoe UI Mono',Consolas,monospace;" +
            $"font-size:11px;color:{(isDark ? "#9ca3af" : "#6b7280")};" +
            "margin-top:24px;opacity:0.9;text-transform:uppercase;";

        private const string PrimaryBtnStyle =
            "background:#2563eb;color:#fff;padding:10px 18px;border-radius:10px;" +
            "text-decoration:none;font-size:14px;font-weight:500;display:inline-block;";

        private static string SecondaryBtnStyle(bool isDark) =>
            $"background:{(isDark ? "#2a2a2a" : "#f3f4f6")};" +
            $"color:{(isDark ? "#ffffff" : "#111827")};" +
            "padding:10px 18px;border-radius:10px;font-size:14px;" +
            $"border:1px solid {(isDark ? "#444" : "#e5e7eb")};text-decoration:none;" +
            "display:inline-block;";

        private static string DangerBtnStyle =>
            "background:transparent;border:1px solid #dc2626;color:#dc2626;" +
            "padding:8px 14px;border-radius:8px;font-size:13px;" +
            "display:inline-block;";

        private const string SslWarningIcon =
            @"<svg width='56' height='56' viewBox='0 0 24 24' fill='#dc2626'>
                <path d='M12 2 1 21h22L12 2zm0 14a1 1 0 110 2 1 1 0 010-2zm1-7h-2v5h2V9z'/>
              </svg>";

        private static bool IsDarkTheme()
        {
            try
            {
                return FenBrowser.Core.BrowserSettings.Instance.Theme ==
                       FenBrowser.Core.ThemePreference.Dark;
            }
            catch
            {
                return false;
            }
        }

        private static string RenderBase(string innerHtml)
        {
            bool isDark = IsDarkTheme();
            return $@"<html>
<body style=""{GetPageStyle(isDark)}"">
  <div style=""{GetContainerStyle(isDark)}"">
    {innerHtml}
  </div>
</body>
</html>";
        }

        // ================= SSL ERROR PAGE =================

public static string RenderSslError(string url, string details)
{
    bool isDark = IsDarkTheme();

    string safeUrl = WebUtility.HtmlEncode(url);
    string safeDetails = WebUtility.HtmlEncode(
        details ?? "CERT_COMMON_NAME_INVALID"
    );

    return $@"
<html>
<body style=""{GetPageStyle(isDark)}"">
  <div style=""{GetContainerStyle(isDark)} max-width:560px;"">

    <!-- Header: Icon + Title (ANCHOR) -->
    <div style=""display:flex;align-items:center;gap:16px;margin-bottom:20px;"">
      {SslWarningIcon}
      <div style=""{GetTitleStyle(isDark)}"">
        This connection isn’t secure
      </div>
    </div>

    <!-- Primary Explanation -->
    <div style=""{GetMsgStyle(isDark)} margin-bottom:12px;"">
      Fen Browser couldn’t verify the identity of this website.
      The security certificate doesn’t match the site’s address.
    </div>

    <!-- Secondary Explanation (Muted) -->
    <div style=""font-size:14px;color:{(isDark ? "#9ca3af" : "#6b7280")};
                line-height:1.6;margin-bottom:20px;"">
      This may happen if the site is misconfigured or if your connection
      is being intercepted. Proceeding could expose sensitive information.
    </div>

    <!-- Context Box (Trust Builder) -->
    <div style=""background:{(isDark ? "#111827" : "#f9fafb")};
                border:1px solid {(isDark ? "#374151" : "#e5e7eb")};
                border-radius:10px;
                padding:12px;
                font-size:13px;
                margin-bottom:24px;"">
      <strong>Website:</strong> {safeUrl}<br>
      <strong>Error:</strong> Certificate name mismatch
    </div>

    <!-- Primary Actions -->
    <div style=""display:flex;gap:12px;margin-bottom:20px;"">
      <a href=""about:blank"" style=""{PrimaryBtnStyle}"">
        ← Go back to safety
      </a>
      <a href=""#"" style=""{SecondaryBtnStyle(isDark)}"">
        Retry
      </a>
    </div>

    <!-- Advanced (Progressive Disclosure) -->
    <details style=""margin-top:8px;"">
      <summary style=""cursor:pointer;
                      font-size:14px;
                      color:#2563eb;
                      user-select:none;"">
        Advanced ▸
      </summary>

      <div style=""margin-top:16px;font-size:13px;"">

        <!-- Technical Details -->
        <div style=""{GetCodeStyle(isDark)} margin-bottom:16px;"">
          {safeDetails}
        </div>

        <!-- Danger Zone (Earned, Not Immediate) -->
        <div style=""border:1px solid #dc2626;
                    background:{(isDark ? "#2a0f12" : "#fee2e2")};
                    border-radius:10px;
                    padding:14px;"">

          <strong>Proceed anyway (unsafe)</strong>

          <div style=""margin-top:6px;
                      font-size:13px;
                      color:{(isDark ? "#fca5a5" : "#7f1d1d")};"">
            Fen Browser will not protect you on this site.
          </div>

          <div style=""margin-top:10px;"">
            <button style=""background:transparent;
                           border:1px solid #dc2626;
                           color:#dc2626;
                           padding:8px 14px;
                           border-radius:8px;
                           font-size:13px;"">
              Continue
            </button>
          </div>

        </div>
      </div>
    </details>

  </div>
</body>
</html>";
}


        // ================= EXISTING PAGES (UNCHANGED BEHAVIOR) =================

        public static string RenderConnectionFailed(string url, string details)
        {
            return RenderBase($@"
<h1>Hmm… can’t reach this page</h1>
<p>{WebUtility.HtmlEncode(details)}</p>
<a href=""{url}"" style=""{PrimaryBtnStyle}"">Refresh</a>");
        }

        public static string RenderNoInternet(string url, string details)
        {
            return RenderBase($@"
<h1>No Internet</h1>
<p>Try checking your network connection.</p>
<a href=""{url}"" style=""{PrimaryBtnStyle}"">Refresh</a>");
        }

        public static string RenderGenericError(string url, string title, string message, string details)
        {
            return RenderBase($@"
<h1>{WebUtility.HtmlEncode(title)}</h1>
<p>{WebUtility.HtmlEncode(message)}</p>
<div style=""{GetCodeStyle(IsDarkTheme())}"">{WebUtility.HtmlEncode(details)}</div>
<a href=""{url}"" style=""{PrimaryBtnStyle}"">Refresh</a>");
        }
    }
}
