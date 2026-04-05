using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FenBrowser.Core
{
    public sealed class BrowserClientHintBrand
    {
        public BrowserClientHintBrand(string brand, string version)
        {
            Brand = brand ?? string.Empty;
            Version = version ?? string.Empty;
        }

        public string Brand { get; }
        public string Version { get; }
    }

    public sealed class BrowserUserAgentDataProfile
    {
        public IReadOnlyList<BrowserClientHintBrand> Brands { get; init; } = Array.Empty<BrowserClientHintBrand>();
        public IReadOnlyList<BrowserClientHintBrand> FullVersionList { get; init; } = Array.Empty<BrowserClientHintBrand>();
        public string Platform { get; init; } = "Windows";
        public string PlatformVersion { get; init; } = "10.0.0";
        public string Architecture { get; init; } = "x86";
        public string Bitness { get; init; } = "64";
        public string Model { get; init; } = string.Empty;
        public bool Mobile { get; init; }
        public bool Wow64 { get; init; }

        public string ToSecChUaHeader(IReadOnlyList<BrowserClientHintBrand> brands = null)
        {
            var source = brands ?? Brands;
            return string.Join(", ", source.Select(brand => $"\"{brand.Brand}\";v=\"{brand.Version}\""));
        }
    }

    public sealed class BrowserViewportMetrics
    {
        public double WindowWidth { get; init; } = 1280;
        public double WindowHeight { get; init; } = 720;
        public double OuterWidth { get; init; } = 1280;
        public double OuterHeight { get; init; } = 720;
        public double ScreenWidth { get; init; } = 1920;
        public double ScreenHeight { get; init; } = 1080;
        public double AvailableScreenWidth { get; init; } = 1920;
        public double AvailableScreenHeight { get; init; } = 1040;
        public double DevicePixelRatio { get; init; } = 1;
        public double ScreenX { get; init; }
        public double ScreenY { get; init; }
        public bool Hover { get; init; } = true;
        public bool FinePointer { get; init; } = true;
        public bool ReducedMotion { get; init; }
        public string PreferredColorScheme { get; init; } = "light";

        public static BrowserViewportMetrics Create(
            double windowWidth,
            double windowHeight,
            double? screenWidth = null,
            double? screenHeight = null,
            double? availableScreenWidth = null,
            double? availableScreenHeight = null,
            double devicePixelRatio = 1)
        {
            var safeWindowWidth = NormalizeDimension(windowWidth, 1280);
            var safeWindowHeight = NormalizeDimension(windowHeight, 720);
            var safeScreenWidth = NormalizeDimension(screenWidth ?? Math.Max(1920, safeWindowWidth), Math.Max(1920, safeWindowWidth));
            var safeScreenHeight = NormalizeDimension(screenHeight ?? Math.Max(1080, safeWindowHeight), Math.Max(1080, safeWindowHeight));
            var safeAvailWidth = NormalizeDimension(availableScreenWidth ?? safeScreenWidth, safeScreenWidth);
            var safeAvailHeight = NormalizeDimension(
                availableScreenHeight ?? Math.Max(1, safeScreenHeight - 40),
                Math.Max(1, safeScreenHeight - 40));

            return new BrowserViewportMetrics
            {
                WindowWidth = safeWindowWidth,
                WindowHeight = safeWindowHeight,
                OuterWidth = safeWindowWidth,
                OuterHeight = safeWindowHeight,
                ScreenWidth = safeScreenWidth,
                ScreenHeight = safeScreenHeight,
                AvailableScreenWidth = safeAvailWidth,
                AvailableScreenHeight = safeAvailHeight,
                DevicePixelRatio = devicePixelRatio > 0 ? devicePixelRatio : 1
            };
        }

        private static double NormalizeDimension(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                return fallback;
            }

            return Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class BrowserSurfaceProfile
    {
        public string UserAgent { get; init; } = string.Empty;
        public string AppVersion { get; init; } = string.Empty;
        public string AppName { get; init; } = "Netscape";
        public string AppCodeName { get; init; } = "Mozilla";
        public string Product { get; init; } = "Gecko";
        public string ProductSub { get; init; } = "20030107";
        public string Vendor { get; init; } = string.Empty;
        public string VendorSub { get; init; } = string.Empty;
        public string PlatformToken { get; init; } = "Win32";
        public string PlatformName { get; init; } = "Windows";
        public string OsCpu { get; init; } = "Windows NT 10.0; Win64; x64";
        public string Language { get; init; } = "en-US";
        public IReadOnlyList<string> Languages { get; init; } = new[] { "en-US", "en" };
        public int HardwareConcurrency { get; init; } = 8;
        public double DeviceMemory { get; init; } = 8;
        public bool CookieEnabled { get; init; } = true;
        public bool Online { get; init; } = true;
        public bool PdfViewerEnabled { get; init; } = true;
        public bool WebDriver { get; init; }
        public string DoNotTrack { get; init; } = "0";
        public BrowserViewportMetrics Viewport { get; init; } = BrowserViewportMetrics.Create(1280, 720);
        public BrowserUserAgentDataProfile UserAgentData { get; init; } = new BrowserUserAgentDataProfile();

        public bool MatchesMediaQuery(string query)
        {
            return BrowserMediaQueryEvaluator.Matches(this, query);
        }
    }

    internal static class BrowserMediaQueryEvaluator
    {
        public static bool Matches(BrowserSurfaceProfile surface, string query)
        {
            if (surface == null || string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            foreach (var candidate in SplitTopLevel(query, ','))
            {
                if (MatchesSingle(surface, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesSingle(BrowserSurfaceProfile surface, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var normalized = query.Trim();
            bool negate = false;

            if (normalized.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
            {
                negate = true;
                normalized = normalized[4..].Trim();
            }
            else if (normalized.StartsWith("only ", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[5..].Trim();
            }

            bool result = true;
            foreach (var segment in SplitAndConditions(normalized))
            {
                if (!MatchesSegment(surface, segment))
                {
                    result = false;
                    break;
                }
            }

            return negate ? !result : result;
        }

        private static bool MatchesSegment(BrowserSurfaceProfile surface, string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return true;
            }

            var normalized = TrimParens(segment).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return true;
            }

            if (normalized == "screen" || normalized == "all")
            {
                return true;
            }

            if (normalized == "print")
            {
                return false;
            }

            if (!normalized.Contains(':'))
            {
                return true;
            }

            if (TryMatchDimension(normalized, surface.Viewport?.WindowWidth ?? 0d, "width"))
            {
                return true;
            }

            if (TryMatchDimension(normalized, surface.Viewport?.WindowHeight ?? 0d, "height"))
            {
                return true;
            }

            if (normalized.StartsWith("orientation:", StringComparison.Ordinal))
            {
                bool isPortrait = (surface.Viewport?.WindowHeight ?? 0d) >= (surface.Viewport?.WindowWidth ?? 0d);
                var value = normalized["orientation:".Length..].Trim();
                return value switch
                {
                    "portrait" => isPortrait,
                    "landscape" => !isPortrait,
                    _ => false
                };
            }

            if (normalized.StartsWith("prefers-color-scheme:", StringComparison.Ordinal))
            {
                var preferred = (surface.Viewport?.PreferredColorScheme ?? "light").Trim().ToLowerInvariant();
                return string.Equals(
                    normalized["prefers-color-scheme:".Length..].Trim(),
                    preferred,
                    StringComparison.Ordinal);
            }

            if (normalized.StartsWith("prefers-reduced-motion:", StringComparison.Ordinal))
            {
                bool reduced = surface.Viewport?.ReducedMotion == true;
                var value = normalized["prefers-reduced-motion:".Length..].Trim();
                return value switch
                {
                    "reduce" => reduced,
                    "no-preference" => !reduced,
                    _ => false
                };
            }

            if (normalized.StartsWith("pointer:", StringComparison.Ordinal))
            {
                bool finePointer = surface.Viewport?.FinePointer == true;
                var value = normalized["pointer:".Length..].Trim();
                return value switch
                {
                    "fine" => finePointer,
                    "coarse" => !finePointer,
                    "none" => false,
                    _ => false
                };
            }

            if (normalized.StartsWith("hover:", StringComparison.Ordinal))
            {
                bool hover = surface.Viewport?.Hover == true;
                var value = normalized["hover:".Length..].Trim();
                return value switch
                {
                    "hover" => hover,
                    "none" => !hover,
                    _ => false
                };
            }

            return false;
        }

        private static bool TryMatchDimension(string query, double value, string featureName)
        {
            var minPattern = $@"^min-{featureName}\s*:\s*(\d+(?:\.\d+)?)px?$";
            var minMatch = Regex.Match(query, minPattern);
            if (minMatch.Success &&
                double.TryParse(minMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minValue))
            {
                return value >= minValue;
            }

            var maxPattern = $@"^max-{featureName}\s*:\s*(\d+(?:\.\d+)?)px?$";
            var maxMatch = Regex.Match(query, maxPattern);
            if (maxMatch.Success &&
                double.TryParse(maxMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxValue))
            {
                return value <= maxValue;
            }

            var exactPattern = $@"^{featureName}\s*:\s*(\d+(?:\.\d+)?)px?$";
            var exactMatch = Regex.Match(query, exactPattern);
            if (exactMatch.Success &&
                double.TryParse(exactMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var exactValue))
            {
                return Math.Abs(value - exactValue) < 0.01d;
            }

            return false;
        }

        private static IEnumerable<string> SplitAndConditions(string query)
        {
            var parts = new List<string>();
            var builder = new System.Text.StringBuilder();
            int depth = 0;

            for (int i = 0; i < query.Length; i++)
            {
                char current = query[i];
                if (current == '(')
                {
                    depth++;
                }
                else if (current == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (depth == 0 &&
                    i + 5 <= query.Length &&
                    string.Equals(query.Substring(i, 5), " and ", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                    i += 4;
                    continue;
                }

                builder.Append(current);
            }

            if (builder.Length > 0)
            {
                parts.Add(builder.ToString());
            }

            return parts;
        }

        private static IEnumerable<string> SplitTopLevel(string value, char separator)
        {
            var parts = new List<string>();
            var builder = new System.Text.StringBuilder();
            int depth = 0;

            foreach (var current in value)
            {
                if (current == '(')
                {
                    depth++;
                }
                else if (current == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (current == separator && depth == 0)
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                    continue;
                }

                builder.Append(current);
            }

            if (builder.Length > 0)
            {
                parts.Add(builder.ToString());
            }

            return parts;
        }

        private static string TrimParens(string value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            while (trimmed.StartsWith("(", StringComparison.Ordinal) &&
                   trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                trimmed = trimmed[1..^1].Trim();
            }

            return trimmed;
        }
    }

    public static class BrowserSurfaceProfileFactory
    {
        private const string GreaseBrand = " Not;A Brand";
        private const string GreaseMajorVersion = "99";
        private const string GreaseFullVersion = "99.0.0.0";
        private const string ChromiumVersion = "146";
        private const string ChromiumFullVersion = "146.0.7800.12";
        private const string FirefoxVersion = "133.0";

        public static BrowserSurfaceProfile Create(
            UserAgentType type,
            BrowserViewportMetrics metrics = null,
            bool useMobile = false,
            CultureInfo culture = null,
            bool sendDoNotTrack = false)
        {
            metrics ??= BrowserViewportMetrics.Create(1280, 720);
            culture ??= CultureInfo.CurrentCulture;
            var language = string.IsNullOrWhiteSpace(culture?.Name) ? "en-US" : culture.Name;
            var languages = BuildLanguageList(language);

            return type switch
            {
                UserAgentType.Firefox => CreateFirefoxProfile(metrics, useMobile, language, languages, sendDoNotTrack),
                UserAgentType.FenBrowser => CreateFenBrowserProfile(metrics, useMobile, language, languages, sendDoNotTrack),
                UserAgentType.Edge => CreateEdgeProfile(metrics, useMobile, language, languages, sendDoNotTrack),
                _ => CreateChromeProfile(metrics, useMobile, language, languages, sendDoNotTrack)
            };
        }

        private static BrowserSurfaceProfile CreateEdgeProfile(
            BrowserViewportMetrics metrics,
            bool useMobile,
            string language,
            IReadOnlyList<string> languages,
            bool sendDoNotTrack)
        {
            var userAgent = useMobile
                ? $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumFullVersion} Mobile Safari/537.36 EdgA/{ChromiumFullVersion}"
                : $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumFullVersion} Safari/537.36 Edg/{ChromiumFullVersion}";

            var brands = BuildChromiumBrands("Microsoft Edge", ChromiumVersion);
            var fullVersionList = BuildChromiumBrands("Microsoft Edge", ChromiumFullVersion, GreaseFullVersion);

            return CreateChromiumProfile(
                userAgent,
                metrics,
                useMobile,
                language,
                languages,
                brands,
                fullVersionList,
                sendDoNotTrack,
                platformVersion: ResolveWindowsPlatformVersion(),
                vendor: "Google Inc.");
        }

        private static BrowserSurfaceProfile CreateChromeProfile(
            BrowserViewportMetrics metrics,
            bool useMobile,
            string language,
            IReadOnlyList<string> languages,
            bool sendDoNotTrack)
        {
            var userAgent = useMobile
                ? $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumFullVersion} Mobile Safari/537.36"
                : $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumFullVersion} Safari/537.36";

            var brands = BuildChromiumBrands("Google Chrome", ChromiumVersion);
            var fullVersionList = BuildChromiumBrands("Google Chrome", ChromiumFullVersion, GreaseFullVersion);

            return CreateChromiumProfile(
                userAgent,
                metrics,
                useMobile,
                language,
                languages,
                brands,
                fullVersionList,
                sendDoNotTrack,
                platformVersion: ResolveWindowsPlatformVersion(),
                vendor: "Google Inc.");
        }

        private static BrowserSurfaceProfile CreateFenBrowserProfile(
            BrowserViewportMetrics metrics,
            bool useMobile,
            string language,
            IReadOnlyList<string> languages,
            bool sendDoNotTrack)
        {
            var userAgent = useMobile
                ? $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumFullVersion} Mobile Safari/537.36 FenBrowser/1.0"
                : $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumFullVersion} Safari/537.36 FenBrowser/1.0";

            var brands = BuildChromiumBrands("FenBrowser", "1");
            var fullVersionList = BuildChromiumBrands("FenBrowser", "1.0.0.0", GreaseFullVersion);

            return CreateChromiumProfile(
                userAgent,
                metrics,
                useMobile,
                language,
                languages,
                brands,
                fullVersionList,
                sendDoNotTrack,
                platformVersion: ResolveWindowsPlatformVersion(),
                vendor: "FenBrowser Project");
        }

        private static BrowserSurfaceProfile CreateFirefoxProfile(
            BrowserViewportMetrics metrics,
            bool useMobile,
            string language,
            IReadOnlyList<string> languages,
            bool sendDoNotTrack)
        {
            var userAgent = useMobile
                ? $"Mozilla/5.0 (Android 14; Mobile; rv:{FirefoxVersion}) Gecko/{FirefoxVersion} Firefox/{FirefoxVersion}"
                : $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{FirefoxVersion}) Gecko/20100101 Firefox/{FirefoxVersion}";

            return new BrowserSurfaceProfile
            {
                UserAgent = userAgent,
                AppVersion = userAgent.StartsWith("Mozilla/", StringComparison.Ordinal) ? userAgent.Substring(8) : userAgent,
                AppName = "Netscape",
                AppCodeName = "Mozilla",
                Product = "Gecko",
                ProductSub = "20100101",
                Vendor = string.Empty,
                VendorSub = string.Empty,
                PlatformToken = "Win32",
                PlatformName = "Windows",
                OsCpu = "Windows NT 10.0; Win64; x64",
                Language = language,
                Languages = languages,
                HardwareConcurrency = Math.Max(2, Environment.ProcessorCount),
                DeviceMemory = 8,
                CookieEnabled = true,
                Online = true,
                PdfViewerEnabled = true,
                WebDriver = false,
                DoNotTrack = sendDoNotTrack ? "1" : "0",
                Viewport = metrics,
                UserAgentData = new BrowserUserAgentDataProfile
                {
                    Platform = "Windows",
                    PlatformVersion = ResolveWindowsPlatformVersion(),
                    Architecture = "x86",
                    Bitness = "64",
                    Mobile = useMobile,
                    Wow64 = false
                }
            };
        }

        private static BrowserSurfaceProfile CreateChromiumProfile(
            string userAgent,
            BrowserViewportMetrics metrics,
            bool useMobile,
            string language,
            IReadOnlyList<string> languages,
            IReadOnlyList<BrowserClientHintBrand> brands,
            IReadOnlyList<BrowserClientHintBrand> fullVersionList,
            bool sendDoNotTrack,
            string platformVersion,
            string vendor)
        {
            return new BrowserSurfaceProfile
            {
                UserAgent = userAgent,
                AppVersion = userAgent.StartsWith("Mozilla/", StringComparison.Ordinal) ? userAgent.Substring(8) : userAgent,
                AppName = "Netscape",
                AppCodeName = "Mozilla",
                Product = "Gecko",
                ProductSub = "20030107",
                Vendor = vendor,
                VendorSub = string.Empty,
                PlatformToken = "Win32",
                PlatformName = "Windows",
                OsCpu = "Windows NT 10.0; Win64; x64",
                Language = language,
                Languages = languages,
                HardwareConcurrency = Math.Max(2, Environment.ProcessorCount),
                DeviceMemory = 8,
                CookieEnabled = true,
                Online = true,
                PdfViewerEnabled = true,
                WebDriver = false,
                DoNotTrack = sendDoNotTrack ? "1" : "0",
                Viewport = metrics,
                UserAgentData = new BrowserUserAgentDataProfile
                {
                    Brands = brands,
                    FullVersionList = fullVersionList,
                    Platform = "Windows",
                    PlatformVersion = platformVersion,
                    Architecture = "x86",
                    Bitness = "64",
                    Mobile = useMobile,
                    Wow64 = false
                }
            };
        }

        private static BrowserClientHintBrand[] BuildChromiumBrands(
            string browserBrand,
            string browserVersion,
            string greaseVersion = GreaseMajorVersion)
        {
            return new[]
            {
                new BrowserClientHintBrand(GreaseBrand, greaseVersion),
                new BrowserClientHintBrand("Chromium", browserVersion),
                new BrowserClientHintBrand(browserBrand, browserVersion)
            };
        }

        private static IReadOnlyList<string> BuildLanguageList(string language)
        {
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(language))
            {
                values.Add(language);
                var dashIndex = language.IndexOf('-');
                if (dashIndex > 0)
                {
                    values.Add(language.Substring(0, dashIndex));
                }
            }

            if (!values.Contains("en", StringComparer.OrdinalIgnoreCase))
            {
                values.Add("en");
            }

            return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string ResolveWindowsPlatformVersion()
        {
            try
            {
                var version = Environment.OSVersion.Version;
                if (OperatingSystem.IsWindows())
                {
                    if (version.Build >= 22000)
                    {
                        // Chromium client hints expose Windows 11 as 13+ and current
                        // Edge reports a 15.x family token on Windows 11.
                        return "15.0.0";
                    }

                    if (version.Major >= 10)
                    {
                        return "10.0.0";
                    }
                }

                return $"{Math.Max(1, version.Major)}.0.0";
            }
            catch
            {
                return "10.0.0";
            }
        }
    }
}
