using System;
using System.Net;

namespace FenBrowser.FenEngine.Rendering
{
    public static class ErrorPageRenderer
    {
        // Common styles
        private const string BodyStyle = "background-color: #2b2b2b; color: #ffffff; font-family: 'Segoe UI', sans-serif; margin: 0; padding: 0; height: 100vh; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center;";
        private const string ContainerStyle = "max-width: 600px; padding: 40px; display: flex; flex-direction: column; align-items: center;";
        private const string IconStyle = "font-size: 72px; margin-bottom: 24px;";
        private const string HeadingStyle = "font-size: 24px; font-weight: 600; margin-bottom: 16px; color: #ffffff;";
        private const string MessageStyle = "font-size: 16px; color: #cccccc; margin-bottom: 24px; line-height: 1.5;";
        private const string CodeStyle = "font-family: monospace; color: #888888; font-size: 14px; margin-bottom: 32px; background-color: #202020; padding: 8px 16px; border-radius: 4px;";
        private const string ButtonStyle = "background-color: #0078d4; color: #ffffff; padding: 10px 24px; border-radius: 4px; border: none; font-weight: 600; font-size: 14px; cursor: pointer; text-decoration: none; display: inline-block;";
        private const string LinkStyle = "color: #0078d4; text-decoration: none; margin-top: 20px; font-size: 14px;";

        public static string RenderConnectionFailed(string url, string details)
        {
            return $@"
                <html>
                <body style=""{BodyStyle}"">
                    <div style=""{ContainerStyle}"">
                        <div style=""{IconStyle} color: #a0a0a0;"">&#127760;</div> <!-- Globe with X -->
                        <div style=""{HeadingStyle}"">Hmm... can't reach this page</div>
                        <div style=""{MessageStyle}"">
                            Check if there is a typo in <strong>{WebUtility.HtmlEncode(url)}</strong>.<br/>
                            If spelling is correct, try checking your connection.
                        </div>
                        <div style=""{CodeStyle}"">{WebUtility.HtmlEncode(details ?? "DNS_PROBE_FINISHED_NXDOMAIN")}</div>
                        <a href=""{url}"" style=""{ButtonStyle}"">Refresh</a>
                    </div>
                </body>
                </html>";
        }

        public static string RenderSslError(string url, string details)
        {
            return $@"
                <html>
                <body style=""{BodyStyle}"">
                    <div style=""{ContainerStyle}"">
                        <div style=""{IconStyle} color: #ef4444;"">&#9888;</div> <!-- Warning Triangle -->
                        <div style=""{HeadingStyle}"">Your connection isn't private</div>
                        <div style=""{MessageStyle}"">
                            Attackers might be trying to steal your information from <strong>{WebUtility.HtmlEncode(url)}</strong> (for example, passwords, messages, or credit cards).
                        </div>
                        <div style=""{CodeStyle}"">NET::ERR_CERT_COMMON_NAME_INVALID</div>
                        <div style=""display: flex; gap: 16px;"">
                            <button onclick=""history.back()"" style=""{ButtonStyle}"">Go Back</button>
                            <!-- Advanced options could go here -->
                        </div>
                        <div style=""margin-top: 20px; font-size: 12px; color: #666;"">{WebUtility.HtmlEncode(details)}</div>
                    </div>
                </body>
                </html>";
        }

        public static string RenderNoInternet(string url, string details)
        {
            return $@"
                <html>
                <body style=""{BodyStyle}"">
                    <div style=""{ContainerStyle}"">
                        <div style=""{IconStyle} color: #a0a0a0;"">&#129430;</div> <!-- Dinosaur or similar -->
                        <div style=""{HeadingStyle}"">No Internet</div>
                        <div style=""{MessageStyle}"">
                            Try:
                            <ul style=""text-align: left; display: inline-block; margin-top: 10px;"">
                                <li>Checking the network cables, modem, and router</li>
                                <li>Reconnecting to Wi-Fi</li>
                            </ul>
                        </div>
                        <div style=""{CodeStyle}"">ERR_INTERNET_DISCONNECTED</div>
                        <a href=""{url}"" style=""{ButtonStyle}"">Refresh</a>
                    </div>
                </body>
                </html>";
        }

        public static string RenderGenericError(string url, string title, string message, string details)
        {
            return $@"
                <html>
                <body style=""{BodyStyle}"">
                    <div style=""{ContainerStyle}"">
                        <div style=""{IconStyle} color: #a0a0a0;"">&#128533;</div> <!-- Confused Face -->
                        <div style=""{HeadingStyle}"">{WebUtility.HtmlEncode(title)}</div>
                        <div style=""{MessageStyle}"">{WebUtility.HtmlEncode(message)}</div>
                        <div style=""{CodeStyle}"">{WebUtility.HtmlEncode(details)}</div>
                        <a href=""{url}"" style=""{ButtonStyle}"">Refresh</a>
                    </div>
                </body>
                </html>";
        }
    }
}
