using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using FenBrowser.WebDriver;

namespace FenBrowser.WebDriver.Protocol
{
    /// <summary>
    /// W3C WebDriver capabilities.
    /// </summary>
    public class Capabilities
    {
        private static readonly HashSet<string> AllowedPageLoadStrategies = new(StringComparer.Ordinal)
        {
            "none",
            "eager",
            "normal"
        };

        private static readonly HashSet<string> AllowedPromptBehaviors = new(StringComparer.Ordinal)
        {
            "dismiss",
            "accept",
            "dismiss and notify",
            "accept and notify",
            "ignore"
        };

        [JsonPropertyName("browserName")]
        public string BrowserName { get; set; } = "FenBrowser";

        [JsonPropertyName("browserVersion")]
        public string BrowserVersion { get; set; } = "1.0.0";

        [JsonPropertyName("platformName")]
        public string PlatformName { get; set; } = "windows";

        [JsonPropertyName("acceptInsecureCerts")]
        public bool AcceptInsecureCerts { get; set; }

        [JsonPropertyName("pageLoadStrategy")]
        public string PageLoadStrategy { get; set; } = "normal";

        [JsonPropertyName("proxy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ProxyConfig Proxy { get; set; }

        [JsonPropertyName("setWindowRect")]
        public bool SetWindowRect { get; set; } = true;

        [JsonPropertyName("timeouts")]
        public Timeouts Timeouts { get; set; } = new();

        [JsonPropertyName("strictFileInteractability")]
        public bool StrictFileInteractability { get; set; }

        [JsonPropertyName("unhandledPromptBehavior")]
        public string UnhandledPromptBehavior { get; set; } = "dismiss and notify";

        [JsonPropertyName("fen:options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FenOptions FenOptions { get; set; }

        /// <summary>
        /// Merge requested capabilities into a normalized session capability set.
        /// </summary>
        public static Capabilities Merge(Capabilities requested)
        {
            requested?.ValidateOrThrow();

            var merged = new Capabilities();
            if (requested == null)
                return merged;

            merged.AcceptInsecureCerts = requested.AcceptInsecureCerts;
            merged.PageLoadStrategy = NormalizePageLoadStrategy(requested.PageLoadStrategy);
            merged.Timeouts = requested.Timeouts?.Clone() ?? new Timeouts();
            merged.Proxy = requested.Proxy?.Clone();
            merged.StrictFileInteractability = requested.StrictFileInteractability;
            merged.UnhandledPromptBehavior = NormalizePromptBehavior(requested.UnhandledPromptBehavior);
            merged.FenOptions = requested.FenOptions?.Clone();
            merged.ValidateOrThrow();
            return merged;
        }

        public void ValidateOrThrow()
        {
            if (!AllowedPageLoadStrategies.Contains(NormalizePageLoadStrategy(PageLoadStrategy)))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported pageLoadStrategy: {PageLoadStrategy}");
            }

            if (!AllowedPromptBehaviors.Contains(NormalizePromptBehavior(UnhandledPromptBehavior)))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported unhandledPromptBehavior: {UnhandledPromptBehavior}");
            }

            Timeouts?.ValidateOrThrow();
            Proxy?.ValidateOrThrow();
            FenOptions?.ValidateOrThrow();
        }

        internal static string NormalizePageLoadStrategy(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "normal" : value.Trim().ToLowerInvariant();
        }

        internal static string NormalizePromptBehavior(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "dismiss and notify" : value.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Session timeouts.
    /// </summary>
    public class Timeouts
    {
        [JsonPropertyName("script")]
        public int? Script { get; set; } = 30000;

        [JsonPropertyName("pageLoad")]
        public int? PageLoad { get; set; } = 300000;

        [JsonPropertyName("implicit")]
        public int? Implicit { get; set; } = 0;

        public void ValidateOrThrow()
        {
            ValidateTimeout(nameof(Script), Script, allowNull: true);
            ValidateTimeout(nameof(PageLoad), PageLoad, allowNull: true);
            ValidateTimeout(nameof(Implicit), Implicit, allowNull: true);
        }

        public Timeouts Clone()
        {
            return new Timeouts
            {
                Script = Script,
                PageLoad = PageLoad,
                Implicit = Implicit
            };
        }

        private static void ValidateTimeout(string name, int? value, bool allowNull)
        {
            if (!value.HasValue)
            {
                if (!allowNull)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout is required");
                }

                return;
            }

            if (value.Value < 0)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout must be >= 0");
            }
        }
    }

    /// <summary>
    /// Proxy configuration.
    /// </summary>
    public class ProxyConfig
    {
        private static readonly HashSet<string> AllowedProxyTypes = new(StringComparer.Ordinal)
        {
            "direct",
            "manual",
            "pac",
            "autodetect",
            "system"
        };

        [JsonPropertyName("proxyType")]
        public string ProxyType { get; set; }

        [JsonPropertyName("httpProxy")]
        public string HttpProxy { get; set; }

        [JsonPropertyName("sslProxy")]
        public string SslProxy { get; set; }

        [JsonPropertyName("noProxy")]
        public List<string> NoProxy { get; set; }

        public void ValidateOrThrow()
        {
            if (string.IsNullOrWhiteSpace(ProxyType))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "proxy.proxyType is required when proxy is specified");
            }

            var normalizedType = ProxyType.Trim().ToLowerInvariant();
            if (!AllowedProxyTypes.Contains(normalizedType))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported proxyType: {ProxyType}");
            }

            if (normalizedType == "manual" &&
                string.IsNullOrWhiteSpace(HttpProxy) &&
                string.IsNullOrWhiteSpace(SslProxy))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "manual proxy configuration requires httpProxy or sslProxy");
            }
        }

        public ProxyConfig Clone()
        {
            return new ProxyConfig
            {
                ProxyType = ProxyType,
                HttpProxy = HttpProxy,
                SslProxy = SslProxy,
                NoProxy = NoProxy?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList()
            };
        }
    }

    /// <summary>
    /// FenBrowser-specific options.
    /// </summary>
    public class FenOptions
    {
        [JsonPropertyName("headless")]
        public bool Headless { get; set; }

        [JsonPropertyName("debuggerAddress")]
        public string DebuggerAddress { get; set; }

        [JsonPropertyName("args")]
        public List<string> Args { get; set; }

        [JsonPropertyName("binary")]
        public string Binary { get; set; }

        public void ValidateOrThrow()
        {
            if (Args != null && Args.Any(string.IsNullOrWhiteSpace))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "fen:options.args may not contain empty values");
            }
        }

        public FenOptions Clone()
        {
            return new FenOptions
            {
                Headless = Headless,
                DebuggerAddress = DebuggerAddress,
                Args = Args?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList(),
                Binary = Binary
            };
        }
    }

    /// <summary>
    /// Capability request for new session.
    /// </summary>
    public class CapabilityRequest
    {
        [JsonPropertyName("capabilities")]
        public CapabilitiesWrapper Capabilities { get; set; }
    }

    public class CapabilitiesWrapper
    {
        [JsonPropertyName("alwaysMatch")]
        public Capabilities AlwaysMatch { get; set; }

        [JsonPropertyName("firstMatch")]
        public List<Capabilities> FirstMatch { get; set; }
    }
}
