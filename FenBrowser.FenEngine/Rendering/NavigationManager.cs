using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Core.Security;
using FenBrowser.FenEngine.Rendering.Performance;

namespace FenBrowser.FenEngine.Rendering
{
    public enum NavigationRequestKind
    {
        UserInput,
        Programmatic
    }

    public class NavigationManager
    {
        private const int MaxDecodedDataUrlBytes = 32 * 1024 * 1024;
        private readonly ResourceManager _resourceManager;

        public NavigationManager(ResourceManager resourceManager)
        {
            _resourceManager = resourceManager;
        }

        public Task<FetchResult> NavigateUserInputAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.UserInput, referer: null);
        }

        public Task<FetchResult> NavigateAsync(string url)
        {
            return NavigateAsync(url, NavigationRequestKind.Programmatic, referer: null);
        }

        public async Task<FetchResult> NavigateAsync(
            string url,
            NavigationRequestKind requestKind,
            Uri referer = null)
        {
            // 1. Normalize URL
            if (string.IsNullOrWhiteSpace(url))
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Empty URL" };

            url = NormalizeInternalFenUrl(url.Trim());

            // Handle internal schemes
            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult { Status = FetchStatus.Success, Content = "", FinalUri = new Uri("about:blank"), ContentType = "text/html" };
            }

            if (url.Equals("fen://newtab", StringComparison.OrdinalIgnoreCase) || url.Equals("about:newtab", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult { Status = FetchStatus.Success, Content = NewTabRenderer.Render(), FinalUri = new Uri("fen://newtab"), ContentType = "text/html" };
            }

            if (url.StartsWith("fen://performance", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(url, UriKind.Absolute, out var performanceUri) &&
                string.Equals(performanceUri.Host, "performance", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult
                {
                    Status = FetchStatus.Success,
                    Content = PerformancePageRenderer.Render(performanceUri),
                    FinalUri = performanceUri,
                    ContentType = "text/html"
                };
            }

            // Handle local file paths (only for trusted user input)
            if (requestKind == NavigationRequestKind.UserInput &&
                System.IO.Path.IsPathRooted(url) &&
                !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                // Check if it has a colon (e.g. C:\) or starts with \\ (UNC)
                if (url.IndexOf(':') >= 0 || url.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    url = "file:///" + url.Replace("\\", "/");
                    try
                    {
                        // Do not emit local filesystem paths into normal navigation logs.
                        FenBrowser.Core.EngineLogCompat.Debug(
                            "[NavigationManager] Converted rooted user path to file URI.",
                            FenBrowser.Core.Logging.LogCategory.Navigation);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.GetType().Name}");
                    }
                }
            }
            else if (requestKind == NavigationRequestKind.Programmatic &&
                     System.IO.Path.IsPathRooted(url) &&
                     !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = "Programmatic rooted-path navigation is blocked by policy"
                };
            }

            // Default to HTTPS if no supported scheme prefix is present. Scheme
            // matching is ASCII case-insensitive; treating HTTP:// as schemeless
            // produced malformed URLs such as https://HTTP://example.test.
            if (!HasKnownNavigationSchemePrefix(url))
            {
                try
                {
                    FenBrowser.Core.EngineLogCompat.Debug(
                        "[NavigationManager] Defaulting schemeless navigation to HTTPS.",
                        FenBrowser.Core.Logging.LogCategory.Navigation);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavigationManager] Debug log failed: {ex.GetType().Name}");
                }
                url = "https://" + url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Invalid URL format" };
            }

            var fileNavigationAllowed = IsFileNavigationAllowed(requestKind);
            var automationContext = IsAutomationContext();
            var navigationDecision = BrowserSecurityPolicy.EvaluateTopLevelNavigation(
                uri,
                requestKind == NavigationRequestKind.UserInput,
                automationContext,
                BrowserSettings.Instance.AllowFileSchemeNavigation,
                fileNavigationAllowed);

            if (!navigationDecision.IsAllowed)
            {
                navigationDecision.Log(LogCategory.Security);
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = navigationDecision.Message
                };
            }

            if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                navigationDecision.Log(LogCategory.Security, LogLevel.Info);
            }

            // data: URLs are local URL payloads, not network requests. Sending them
            // through ResourceManager would immediately fail its HTTP(S)-only network
            // admission policy even though top-level navigation explicitly allows data:.
            // A Document created from this FinalUri derives a fresh opaque Origin.
            if (string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeDataNavigation(uri);
            }

            // Handle images
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".png", StringComparison.Ordinal) ||
                path.EndsWith(".jpg", StringComparison.Ordinal) ||
                path.EndsWith(".jpeg", StringComparison.Ordinal) ||
                path.EndsWith(".gif", StringComparison.Ordinal) ||
                path.EndsWith(".bmp", StringComparison.Ordinal) ||
                path.EndsWith(".webp", StringComparison.Ordinal) ||
                path.EndsWith(".svg", StringComparison.Ordinal))
            {
                // This HTML becomes a privileged synthetic document. Encode every
                // URI-derived value before placing it into markup rather than relying
                // on System.Uri escaping to also satisfy HTML attribute/text syntax.
                var fileName = System.IO.Path.GetFileName(uri.LocalPath) ?? string.Empty;
                var encodedTitle = WebUtility.HtmlEncode(fileName);
                var encodedAlt = WebUtility.HtmlEncode(fileName);
                var encodedSrc = WebUtility.HtmlEncode(uri.AbsoluteUri);
                var syntheticHtml = $"<!DOCTYPE html><html style=\"width: 100%; height: 100%; background-color: rgb(14, 14, 14);\"><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>{encodedTitle}</title></head><body style=\"margin: 0; padding: 0; position: fixed; top: 0; left: 0; right: 0; bottom: 0; background-color: rgb(14, 14, 14); overflow: hidden;\"><img style=\"display: block; position: absolute; top: 0; bottom: 0; left: 0; right: 0; margin: auto; max-width: 100%; max-height: 100%; object-fit: contain; -webkit-user-select: none;\" src=\"{encodedSrc}\" alt=\"{encodedAlt}\"></body></html>";
                return new FetchResult { Status = FetchStatus.Success, Content = syntheticHtml, FinalUri = uri, ContentType = "text/html" };
            }

            // 2. Fetch content as a top-level document navigation so servers
            // see navigation semantics instead of subresource-style headers.
            var initiator = requestKind == NavigationRequestKind.UserInput ? null : referer;
            return await _resourceManager.FetchTextDetailedAsync(
                new FetchContext
                {
                    RequestUri = uri,
                    InitiatorUri = initiator,
                    FrameDocumentUri = initiator,
                    TopLevelDocumentUri = referer,
                    Destination = "document",
                    Mode = "navigate",
                    CredentialsMode = "include",
                    IsTopLevelNavigation = true,
                    IsUserInitiated = requestKind == NavigationRequestKind.UserInput,
                    Method = "GET"
                }).ConfigureAwait(false);
        }

        private static FetchResult DecodeDataNavigation(Uri uri)
        {
            try
            {
                var serialized = uri.OriginalString ?? uri.AbsoluteUri;
                const string prefix = "data:";
                if (!serialized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return DataUrlError(uri, "Malformed data URL");
                }

                var body = serialized.Substring(prefix.Length);
                int commaIndex = body.IndexOf(',');
                if (commaIndex < 0)
                {
                    return DataUrlError(uri, "Malformed data URL: missing payload separator");
                }

                var metadata = body.Substring(0, commaIndex);
                var payloadText = body.Substring(commaIndex + 1);

                bool base64 = false;
                string mediaTypeMetadata = metadata;
                int lastSemicolon = metadata.LastIndexOf(';');
                if (lastSemicolon >= 0 &&
                    string.Equals(metadata.Substring(lastSemicolon + 1).Trim(), "base64", StringComparison.OrdinalIgnoreCase))
                {
                    base64 = true;
                    mediaTypeMetadata = metadata.Substring(0, lastSemicolon);
                }

                var contentType = string.IsNullOrWhiteSpace(mediaTypeMetadata)
                    ? "text/plain;charset=US-ASCII"
                    : mediaTypeMetadata.Trim();

                byte[] percentDecoded = PercentDecodeDataPayload(payloadText, MaxDecodedDataUrlBytes);
                byte[] bytes;
                if (base64)
                {
                    string encoded = Encoding.ASCII.GetString(percentDecoded);
                    bytes = Convert.FromBase64String(RemoveAsciiWhitespace(encoded));
                    if (bytes.Length > MaxDecodedDataUrlBytes)
                    {
                        return new FetchResult
                        {
                            Status = FetchStatus.LimitExceeded,
                            FailureReason = FetchFailureReasonCode.LimitExceeded,
                            LimitType = "data-url-bytes",
                            ErrorDetail = "Decoded data URL exceeds browser limit",
                            FinalUri = uri
                        };
                    }
                }
                else
                {
                    bytes = percentDecoded;
                }

                var mimeEssence = GetMimeEssence(contentType);
                if (mimeEssence.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    var encodedSrc = WebUtility.HtmlEncode(serialized);
                    var syntheticHtml = $"<!DOCTYPE html><html style=\"width:100%;height:100%;background:#0e0e0e\"><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head><body style=\"margin:0;position:fixed;inset:0;background:#0e0e0e;overflow:hidden\"><img style=\"display:block;position:absolute;inset:0;margin:auto;max-width:100%;max-height:100%;object-fit:contain\" src=\"{encodedSrc}\" alt=\"\"></body></html>";
                    return new FetchResult
                    {
                        Status = FetchStatus.Success,
                        Content = syntheticHtml,
                        FinalUri = uri,
                        ContentType = "text/html"
                    };
                }

                if (!IsTextualDataMime(mimeEssence))
                {
                    return DataUrlError(uri, $"Unsupported top-level data URL media type '{mimeEssence}'");
                }

                var textEncoding = ResolveDataTextEncoding(contentType);
                return new FetchResult
                {
                    Status = FetchStatus.Success,
                    Content = textEncoding.GetString(bytes),
                    FinalUri = uri,
                    ContentType = contentType,
                    InputSizeBytes = bytes.Length
                };
            }
            catch (FormatException)
            {
                return DataUrlError(uri, "Malformed base64 data URL payload");
            }
            catch (InvalidOperationException ex)
            {
                return new FetchResult
                {
                    Status = FetchStatus.LimitExceeded,
                    FailureReason = FetchFailureReasonCode.LimitExceeded,
                    LimitType = "data-url-bytes",
                    ErrorDetail = ex.Message,
                    FinalUri = uri
                };
            }
            catch
            {
                return DataUrlError(uri, "Malformed data URL");
            }
        }

        private static byte[] PercentDecodeDataPayload(string payload, int maxBytes)
        {
            using var stream = new System.IO.MemoryStream(Math.Min(payload?.Length ?? 0, maxBytes));
            for (int i = 0; i < (payload?.Length ?? 0); i++)
            {
                char ch = payload[i];
                if (ch == '%' && i + 2 < payload.Length &&
                    TryParseHex(payload[i + 1], out int hi) &&
                    TryParseHex(payload[i + 2], out int lo))
                {
                    stream.WriteByte((byte)((hi << 4) | lo));
                    i += 2;
                }
                else if (ch <= 0x7F)
                {
                    stream.WriteByte((byte)ch);
                }
                else
                {
                    Span<char> chars = stackalloc char[2];
                    int charCount = 1;
                    chars[0] = ch;
                    if (char.IsHighSurrogate(ch) && i + 1 < payload.Length && char.IsLowSurrogate(payload[i + 1]))
                    {
                        chars[1] = payload[++i];
                        charCount = 2;
                    }

                    Span<byte> utf8 = stackalloc byte[4];
                    int encoded = Encoding.UTF8.GetBytes(chars[..charCount], utf8);
                    stream.Write(utf8[..encoded]);
                }

                if (stream.Length > maxBytes)
                    throw new InvalidOperationException("Decoded data URL exceeds browser limit");
            }

            return stream.ToArray();
        }

        private static bool TryParseHex(char value, out int digit)
        {
            if (value >= '0' && value <= '9')
            {
                digit = value - '0';
                return true;
            }
            if (value >= 'a' && value <= 'f')
            {
                digit = value - 'a' + 10;
                return true;
            }
            if (value >= 'A' && value <= 'F')
            {
                digit = value - 'A' + 10;
                return true;
            }

            digit = 0;
            return false;
        }

        private static string RemoveAsciiWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (ch is not (' ' or '\t' or '\r' or '\n' or '\f'))
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        private static string GetMimeEssence(string contentType)
        {
            int semicolon = contentType?.IndexOf(';') ?? -1;
            return (semicolon >= 0 ? contentType.Substring(0, semicolon) : contentType ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
        }

        private static bool IsTextualDataMime(string mimeEssence)
        {
            return mimeEssence.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                   mimeEssence == "application/xhtml+xml" ||
                   mimeEssence == "application/xml" ||
                   mimeEssence.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
                   mimeEssence == "application/json" ||
                   mimeEssence.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        }

        private static Encoding ResolveDataTextEncoding(string contentType)
        {
            const string charsetMarker = "charset=";
            int index = contentType?.IndexOf(charsetMarker, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (index >= 0)
            {
                var charset = contentType.Substring(index + charsetMarker.Length).Trim();
                int semicolon = charset.IndexOf(';');
                if (semicolon >= 0)
                    charset = charset.Substring(0, semicolon).Trim();
                charset = charset.Trim('"', '\'');

                if (charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
                    charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.UTF8;
                }
                if (charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase) ||
                    charset.Equals("ascii", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.ASCII;
                }
                if (charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) ||
                    charset.Equals("latin1", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.Latin1;
                }
                if (charset.Equals("utf-16le", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.Unicode;
                }
                if (charset.Equals("utf-16be", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.BigEndianUnicode;
                }
            }

            return Encoding.UTF8;
        }

        private static FetchResult DataUrlError(Uri uri, string detail)
        {
            return new FetchResult
            {
                Status = FetchStatus.UnknownError,
                FailureReason = FetchFailureReasonCode.MalformedInput,
                ErrorDetail = detail,
                FinalUri = uri
            };
        }

        private static bool HasKnownNavigationSchemePrefix(string url)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("fen://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeInternalFenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (url.Equals("fen://newtab/", StringComparison.OrdinalIgnoreCase))
            {
                return "fen://newtab";
            }

            if (url.Equals("fen://settings/", StringComparison.OrdinalIgnoreCase))
            {
                return "fen://settings";
            }

            return url;
        }

        private static bool IsFileNavigationAllowed(NavigationRequestKind requestKind)
        {
            var settings = BrowserSettings.Instance;
            if (!settings.AllowFileSchemeNavigation)
            {
                return false;
            }

            if (requestKind != NavigationRequestKind.Programmatic)
            {
                return true;
            }

            var webdriverEnabled = string.Equals(
                Environment.GetEnvironmentVariable("FEN_WEBDRIVER"),
                "1",
                StringComparison.Ordinal);
            var automationMode = string.Equals(
                Environment.GetEnvironmentVariable("FEN_AUTOMATION_MODE"),
                "1",
                StringComparison.Ordinal);

            if (!webdriverEnabled && !automationMode)
            {
                return true;
            }

            if (settings.AllowAutomationFileNavigation)
            {
                return true;
            }

            return string.Equals(
                Environment.GetEnvironmentVariable("FEN_ALLOW_AUTOMATION_FILE_NAVIGATION"),
                "1",
                StringComparison.Ordinal);
        }

        private static bool IsAutomationContext()
        {
            var webdriverEnabled = string.Equals(
                Environment.GetEnvironmentVariable("FEN_WEBDRIVER"),
                "1",
                StringComparison.Ordinal);
            var automationMode = string.Equals(
                Environment.GetEnvironmentVariable("FEN_AUTOMATION_MODE"),
                "1",
                StringComparison.Ordinal);
            return webdriverEnabled || automationMode;
        }
    }
}
