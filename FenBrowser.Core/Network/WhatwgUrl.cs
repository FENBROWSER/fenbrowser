using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace FenBrowser.Core.Network
{
    // WHATWG URL Standard — https://url.spec.whatwg.org/
    // Full algorithmic implementation of the URL parser state machine.
    // URL bugs are origin bugs. Zero custom rules; spec-only.

    /// <summary>Origin type per WHATWG URL §7.</summary>
    public enum UrlOriginKind { Opaque, Tuple }

    /// <summary>Immutable URL origin (scheme, host, port) or opaque.</summary>
    public sealed class UrlOrigin
    {
        public static readonly UrlOrigin Opaque = new UrlOrigin();

        public UrlOriginKind Kind { get; }
        public string Scheme { get; }
        public WhatwgHost Host { get; }
        public int? Port { get; }
        public string Domain { get; }

        private UrlOrigin() => Kind = UrlOriginKind.Opaque;

        public UrlOrigin(string scheme, WhatwgHost host, int? port, string domain = null)
        {
            Kind = UrlOriginKind.Tuple;
            Scheme = scheme;
            Host = host;
            Port = port;
            Domain = domain;
        }

        public bool IsSameOrigin(UrlOrigin other)
        {
            if (other == null) return false;
            if (Kind == UrlOriginKind.Opaque || other.Kind == UrlOriginKind.Opaque) return false;
            return Scheme == other.Scheme &&
                   Host?.Serialize() == other.Host?.Serialize() &&
                   Port == other.Port;
        }

        public string Serialize()
        {
            if (Kind == UrlOriginKind.Opaque) return "null";
            var sb = new StringBuilder(Scheme).Append("://");
            sb.Append(Host?.Serialize() ?? "");
            if (Port.HasValue) sb.Append(':').Append(Port.Value);
            return sb.ToString();
        }

        public override string ToString() => Serialize();
    }

    /// <summary>Host kinds per WHATWG URL §3.3.</summary>
    public enum HostKind { Domain, Ip4, Ip6, Opaque, Empty }

    /// <summary>Parsed host representation.</summary>
    public sealed class WhatwgHost
    {
        public HostKind Kind { get; }
        public string Domain { get; }          // Domain or OpaqueHost
        public uint Ipv4Address { get; }
        public ushort[] Ipv6Pieces { get; }    // 8 pieces

        public static readonly WhatwgHost Empty = new WhatwgHost(HostKind.Empty, null, 0, null);

        private WhatwgHost(HostKind kind, string domain, uint ipv4, ushort[] ipv6)
        {
            Kind = kind; Domain = domain; Ipv4Address = ipv4; Ipv6Pieces = ipv6;
        }

        public static WhatwgHost FromDomain(string domain) =>
            new WhatwgHost(HostKind.Domain, domain, 0, null);

        public static WhatwgHost FromOpaque(string opaque) =>
            new WhatwgHost(HostKind.Opaque, opaque, 0, null);

        public static WhatwgHost FromIpv4(uint addr) =>
            new WhatwgHost(HostKind.Ip4, null, addr, null);

        public static WhatwgHost FromIpv6(ushort[] pieces) =>
            new WhatwgHost(HostKind.Ip6, null, 0, pieces);

        public string Serialize()
        {
            switch (Kind)
            {
                case HostKind.Domain: return Domain ?? "";
                case HostKind.Opaque: return Domain ?? "";
                case HostKind.Empty: return "";
                case HostKind.Ip4:
                    return $"{(Ipv4Address >> 24) & 0xFF}.{(Ipv4Address >> 16) & 0xFF}.{(Ipv4Address >> 8) & 0xFF}.{Ipv4Address & 0xFF}";
                case HostKind.Ip6:
                    return "[" + SerializeIpv6() + "]";
                default: return "";
            }
        }

        private string SerializeIpv6()
        {
            // Find longest run of consecutive zeroes for :: compression
            int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
            for (int i = 0; i < 8; i++)
            {
                if (Ipv6Pieces[i] == 0)
                {
                    if (curStart < 0) { curStart = i; curLen = 1; }
                    else curLen++;
                    if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
                }
                else { curStart = -1; curLen = 0; }
            }

            var sb = new StringBuilder();
            bool compressed = false;
            for (int i = 0; i < 8; i++)
            {
                if (bestLen >= 2 && i == bestStart)
                {
                    sb.Append("::");
                    compressed = true;
                    i += bestLen - 1;
                    continue;
                }
                if (i > 0 && !(compressed && i == bestStart + bestLen)) sb.Append(':');
                sb.Append(Ipv6Pieces[i].ToString("x"));
            }
            return sb.ToString();
        }

        public override string ToString() => Serialize();
    }

    /// <summary>Mutable URL record (internal). Serializes to immutable <see cref="WhatwgUrl"/>.</summary>
    internal sealed class UrlRecord
    {
        public string Scheme = "";
        public string Username = "";
        public string Password = "";
        public WhatwgHost Host;
        public int? Port;
        public List<string> Path = new List<string>();  // path segments
        public string Query;                             // null or string (no leading ?)
        public string Fragment;                          // null or string (no leading #)
        public bool CannotBeABaseUrl;

        public bool HasOpaquePath => CannotBeABaseUrl;

        public bool IsSpecial => UrlParser.IsSpecialScheme(Scheme);

        public bool IncludesCredentials =>
            !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password);

        public string ShortenedPath()
        {
            if (Scheme == "file" && Path.Count == 1 && IsNormalizedWindowsDriveLetter(Path[0]))
                return Path[0];
            if (Path.Count > 0) Path.RemoveAt(Path.Count - 1);
            return null;
        }

        private static bool IsNormalizedWindowsDriveLetter(string s) =>
            s?.Length == 2 && char.IsLetter(s[0]) && s[1] == ':';
    }

    /// <summary>
    /// Immutable, fully-parsed WHATWG URL. All parsing is via <see cref="UrlParser.Parse"/>.
    /// </summary>
    public sealed class WhatwgUrl
    {
        // Raw record components
        internal readonly UrlRecord _record;

        internal WhatwgUrl(UrlRecord r) => _record = r;

    /// <summary>Parse a URL string. Convenience alias for <see cref="UrlParser.Parse"/>.</summary>
    public static WhatwgUrl Parse(string input, WhatwgUrl baseUrl = null) => UrlParser.Parse(input, baseUrl);

        // ---- Getters per WHATWG URL §5.1 ----

        public string Href => Serialize();

        public string Origin => ComputeOrigin().Serialize();

        public string Protocol => _record.Scheme + ":";
        public string Username => _record.Username;
        public string Password => _record.Password;

        public string Host
        {
            get
            {
                if (_record.Host == null) return "";
                var h = _record.Host.Serialize();
                return _record.Port.HasValue ? h + ":" + _record.Port.Value : h;
            }
        }

        public string Hostname => _record.Host?.Serialize() ?? "";

        public string Port => _record.Port.HasValue ? _record.Port.Value.ToString() : "";

        public string Pathname
        {
            get
            {
                if (_record.HasOpaquePath)
                    return _record.Path.Count > 0 ? _record.Path[0] : "";
                return "/" + string.Join("/", _record.Path);
            }
        }

        public string Search => _record.Query != null ? "?" + _record.Query : "";

        public string Hash => _record.Fragment != null ? "#" + _record.Fragment : "";

        public string Scheme => _record.Scheme;

        /// <summary>Computes the URL's origin per spec §7.</summary>
        public UrlOrigin ComputeOrigin()
        {
            switch (_record.Scheme)
            {
                case "blob":
                    // Parse the path as a URL and return its origin
                    var inner = WhatwgUrl.Parse(_record.Path.Count > 0 ? _record.Path[0] : "");
                    return inner?.ComputeOrigin() ?? UrlOrigin.Opaque;
                case "ftp":
                case "http":
                case "https":
                case "ws":
                case "wss":
                    return new UrlOrigin(_record.Scheme, _record.Host, _record.Port);
                case "file":
                    // spec: opaque origin
                    return UrlOrigin.Opaque;
                default:
                    return UrlOrigin.Opaque;
            }
        }

        /// <summary>Serialize per WHATWG URL §5.3 (URL serializer).</summary>
        public string Serialize(bool excludeFragment = false)
        {
            var sb = new StringBuilder(_record.Scheme).Append(':');

            if (_record.Host != null)
            {
                sb.Append("//");
                if (_record.IncludesCredentials)
                {
                    sb.Append(PercentEncode(_record.Username, UrlEncoder.UserinfoSet));
                    if (!string.IsNullOrEmpty(_record.Password))
                        sb.Append(':').Append(PercentEncode(_record.Password, UrlEncoder.UserinfoSet));
                    sb.Append('@');
                }
                sb.Append(_record.Host.Serialize());
                if (_record.Port.HasValue) sb.Append(':').Append(_record.Port.Value);
            }
            else if (_record.Scheme == "file")
            {
                sb.Append("//");
            }

            if (_record.HasOpaquePath)
                sb.Append(_record.Path.Count > 0 ? _record.Path[0] : "");
            else
            {
                sb.Append('/').Append(string.Join("/", _record.Path));
            }

            if (_record.Query != null) sb.Append('?').Append(_record.Query);
            if (!excludeFragment && _record.Fragment != null) sb.Append('#').Append(_record.Fragment);

            return sb.ToString();
        }

        private static string PercentEncode(string s, bool[] encodeSet)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var bytes = Encoding.UTF8.GetBytes(s);
            var sb = new StringBuilder(s.Length * 2);
            foreach (var b in bytes)
            {
                if (b < encodeSet.Length && !encodeSet[b])
                    sb.Append((char)b);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        public override string ToString() => Serialize();

        /// <summary>True if URL is opaque-host (cannot-be-a-base).</summary>
        public bool IsOpaque => _record.CannotBeABaseUrl;
    }

    /// <summary>
    /// WHATWG URL parser — state machine implementation.
    /// Spec: https://url.spec.whatwg.org/#concept-url-parser
    /// </summary>
    public static class UrlParser
    {
        // Special schemes and their default ports (§3.1)
        private static readonly Dictionary<string, int?> SpecialSchemes = new()
        {
            ["ftp"]   = 21,
            ["file"]  = null,
            ["http"]  = 80,
            ["https"] = 443,
            ["ws"]    = 80,
            ["wss"]   = 443,
        };

        public static bool IsSpecialScheme(string scheme) => SpecialSchemes.ContainsKey(scheme);

        public static int? DefaultPortForScheme(string scheme) =>
            SpecialSchemes.TryGetValue(scheme, out var p) ? p : null;

        /// <summary>
        /// Parse a URL string, optionally relative to a base URL.
        /// Returns null on failure.
        /// </summary>
        public static WhatwgUrl Parse(string input, WhatwgUrl baseUrl = null)
        {
            if (input == null) return null;
            var record = RunStateMachine(input, baseUrl?._record ?? null, null, null);
            return record != null ? new WhatwgUrl(record) : null;
        }

        /// <summary>
        /// Parse and return failure/success with validation errors list.
        /// </summary>
        public static WhatwgUrl Parse(string input, WhatwgUrl baseUrl, out List<string> validationErrors)
        {
            var errors = new List<string>();
            var record = RunStateMachine(input, baseUrl?._record, null, errors);
            validationErrors = errors;
            return record != null ? new WhatwgUrl(record) : null;
        }

        // ---- State machine ----

        private enum State
        {
            SchemeStart, Scheme, NoScheme, SpecialRelativeOrAuthority, PathOrAuthority,
            RelativeOrAuthority, Relative, RelativeSlash, SpecialAuthoritySlashes,
            SpecialAuthorityIgnoreSlashes, Authority, Host, Hostname, Port,
            File, FileSlash, FileHost, PathStart, Path, CannotBeABaseUrlPath,
            Query, Fragment
        }

        private static UrlRecord RunStateMachine(
            string input,
            UrlRecord baseRecord,
            State? overrideState,
            List<string> errors)
        {
            // Pre-process: strip leading/trailing C0 controls + space, strip tab+newline throughout
            input = StripLeadingTrailingC0AndSpace(input, errors);
            input = StripTabAndNewline(input, errors);

            var url = new UrlRecord();
            var state = overrideState ?? State.SchemeStart;
            var buffer = new StringBuilder();
            bool atSignSeen = false, insideBrackets = false, passwordTokenSeen = false;

            int pointer = 0;
            int len = input.Length;

            // Ensure we process EOF too
            while (true)
            {
                int c = pointer < len ? (int)input[pointer] : -1; // -1 = EOF

                switch (state)
                {
                    case State.SchemeStart:
                        if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')
                        {
                            buffer.Append(char.ToLowerInvariant((char)c));
                            state = State.Scheme;
                        }
                        else if (overrideState == null)
                        {
                            state = State.NoScheme;
                            continue; // reprocess
                        }
                        else
                        {
                            errors?.Add("invalid-url-unit");
                            return null;
                        }
                        break;

                    case State.Scheme:
                        if (IsAsciiAlphanumeric(c) || c == '+' || c == '-' || c == '.')
                        {
                            buffer.Append(char.ToLowerInvariant((char)c));
                        }
                        else if (c == ':')
                        {
                            url.Scheme = buffer.ToString();
                            buffer.Clear();

                            if (overrideState != null)
                            {
                                var defPort = DefaultPortForScheme(url.Scheme);
                                if (defPort == url.Port) url.Port = null;
                                return url;
                            }

                            if (url.Scheme == "file")
                            {
                                state = State.File;
                            }
                            else if (url.IsSpecial && baseRecord != null && baseRecord.Scheme == url.Scheme)
                            {
                                state = State.SpecialRelativeOrAuthority;
                            }
                            else if (url.IsSpecial)
                            {
                                state = State.SpecialAuthoritySlashes;
                            }
                            else if (pointer + 1 < len && input[pointer + 1] == '/')
                            {
                                state = State.PathOrAuthority;
                                pointer++;
                            }
                            else
                            {
                                url.CannotBeABaseUrl = true;
                                url.Path.Add("");
                                state = State.CannotBeABaseUrlPath;
                            }
                        }
                        else if (overrideState == null)
                        {
                            buffer.Clear();
                            state = State.NoScheme;
                            pointer = -1; // will be incremented to 0
                        }
                        else
                        {
                            errors?.Add("invalid-url-unit");
                            return null;
                        }
                        break;

                    case State.NoScheme:
                        if (baseRecord == null || (baseRecord.CannotBeABaseUrl && c != '#'))
                        {
                            errors?.Add("missing-scheme-non-relative-url");
                            return null;
                        }
                        if (baseRecord.CannotBeABaseUrl && c == '#')
                        {
                            url.Scheme = baseRecord.Scheme;
                            url.Path = new List<string>(baseRecord.Path);
                            url.Query = baseRecord.Query;
                            url.Fragment = "";
                            url.CannotBeABaseUrl = true;
                            state = State.Fragment;
                        }
                        else if (baseRecord.Scheme != "file")
                        {
                            state = State.Relative;
                            continue;
                        }
                        else
                        {
                            state = State.File;
                            continue;
                        }
                        break;

                    case State.SpecialRelativeOrAuthority:
                        if (c == '/' && pointer + 1 < len && input[pointer + 1] == '/')
                        {
                            state = State.SpecialAuthorityIgnoreSlashes;
                            pointer++;
                        }
                        else
                        {
                            errors?.Add("special-scheme-missing-following-solidus");
                            state = State.Relative;
                            continue;
                        }
                        break;

                    case State.PathOrAuthority:
                        if (c == '/')
                            state = State.Authority;
                        else
                        {
                            state = State.Path;
                            continue;
                        }
                        break;

                    case State.Relative:
                        url.Scheme = baseRecord!.Scheme;
                        if (c == '/')
                        {
                            state = State.RelativeSlash;
                        }
                        else if (url.IsSpecial && c == '\\')
                        {
                            errors?.Add("invalid-reverse-solidus");
                            state = State.RelativeSlash;
                        }
                        else if (c == '?')
                        {
                            url.Username = baseRecord.Username;
                            url.Password = baseRecord.Password;
                            url.Host = baseRecord.Host;
                            url.Port = baseRecord.Port;
                            url.Path = new List<string>(baseRecord.Path);
                            url.Query = "";
                            state = State.Query;
                        }
                        else if (c == '#')
                        {
                            url.Username = baseRecord.Username;
                            url.Password = baseRecord.Password;
                            url.Host = baseRecord.Host;
                            url.Port = baseRecord.Port;
                            url.Path = new List<string>(baseRecord.Path);
                            url.Query = baseRecord.Query;
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                        else if (c != -1)
                        {
                            url.Username = baseRecord.Username;
                            url.Password = baseRecord.Password;
                            url.Host = baseRecord.Host;
                            url.Port = baseRecord.Port;
                            url.Path = new List<string>(baseRecord.Path);
                            url.ShortenedPath();
                            state = State.Path;
                            continue;
                        }
                        else
                        {
                            // EOF
                            url.Username = baseRecord.Username;
                            url.Password = baseRecord.Password;
                            url.Host = baseRecord.Host;
                            url.Port = baseRecord.Port;
                            url.Path = new List<string>(baseRecord.Path);
                            url.Query = baseRecord.Query;
                        }
                        break;

                    case State.RelativeSlash:
                        if (url.IsSpecial && (c == '/' || c == '\\'))
                        {
                            if (c == '\\') errors?.Add("invalid-reverse-solidus");
                            state = State.SpecialAuthorityIgnoreSlashes;
                        }
                        else if (c == '/')
                        {
                            state = State.Authority;
                        }
                        else
                        {
                            url.Username = baseRecord!.Username;
                            url.Password = baseRecord.Password;
                            url.Host = baseRecord.Host;
                            url.Port = baseRecord.Port;
                            state = State.Path;
                            continue;
                        }
                        break;

                    case State.SpecialAuthoritySlashes:
                        if (c == '/' && pointer + 1 < len && input[pointer + 1] == '/')
                        {
                            state = State.SpecialAuthorityIgnoreSlashes;
                            pointer++;
                        }
                        else
                        {
                            errors?.Add("special-scheme-missing-following-solidus");
                            state = State.SpecialAuthorityIgnoreSlashes;
                            continue;
                        }
                        break;

                    case State.SpecialAuthorityIgnoreSlashes:
                        if (c != '/' && c != '\\')
                        {
                            state = State.Authority;
                            continue;
                        }
                        else
                        {
                            errors?.Add("special-scheme-missing-following-solidus");
                        }
                        break;

                    case State.Authority:
                        if (c == '@')
                        {
                            errors?.Add("invalid-credentials");
                            if (atSignSeen) buffer.Insert(0, "%40");
                            atSignSeen = true;

                            foreach (var bChar in buffer.ToString())
                            {
                                if (bChar == ':' && !passwordTokenSeen)
                                {
                                    passwordTokenSeen = true;
                                    continue;
                                }
                                var encodedChar = PercentEncodeChar(bChar, UrlEncoder.UserinfoSet);
                                if (passwordTokenSeen)
                                    url.Password += encodedChar;
                                else
                                    url.Username += encodedChar;
                            }
                            buffer.Clear();
                        }
                        else if ((c == -1 || c == '/' || c == '?' || c == '#') ||
                                 (url.IsSpecial && c == '\\'))
                        {
                            if (atSignSeen && buffer.Length == 0)
                            {
                                errors?.Add("missing-host");
                                return null;
                            }
                            pointer -= buffer.Length + 1;
                            buffer.Clear();
                            state = State.Host;
                        }
                        else
                        {
                            buffer.Append((char)c);
                        }
                        break;

                    case State.Host:
                    case State.Hostname:
                        if (overrideState != null && url.Scheme == "file")
                        {
                            state = State.FileHost;
                            continue;
                        }
                        if (c == ':' && !insideBrackets)
                        {
                            if (buffer.Length == 0)
                            {
                                errors?.Add("missing-host");
                                return null;
                            }
                            if (overrideState == State.Hostname) return url;
                            var host = ParseHost(buffer.ToString(), !url.IsSpecial, errors);
                            if (host == null) return null;
                            url.Host = host;
                            buffer.Clear();
                            state = State.Port;
                        }
                        else if ((c == -1 || c == '/' || c == '?' || c == '#') ||
                                 (url.IsSpecial && c == '\\'))
                        {
                            if (url.IsSpecial && buffer.Length == 0)
                            {
                                errors?.Add("missing-host");
                                return null;
                            }
                            if (overrideState != null && buffer.Length == 0)
                                return url;
                            var host = ParseHost(buffer.ToString(), !url.IsSpecial, errors);
                            if (host == null) return null;
                            url.Host = host;
                            buffer.Clear();
                            state = State.PathStart;
                            if (overrideState != null) return url;
                            continue;
                        }
                        else
                        {
                            if (c == '[') insideBrackets = true;
                            if (c == ']') insideBrackets = false;
                            buffer.Append((char)c);
                        }
                        break;

                    case State.Port:
                        if (IsAsciiDigit(c))
                        {
                            buffer.Append((char)c);
                        }
                        else if ((c == -1 || c == '/' || c == '?' || c == '#') ||
                                 (url.IsSpecial && c == '\\') ||
                                 overrideState != null)
                        {
                            if (buffer.Length > 0)
                            {
                                if (!int.TryParse(buffer.ToString(), out int portNum) || portNum > 65535)
                                {
                                    errors?.Add("invalid-port");
                                    return null;
                                }
                                var defPort = DefaultPortForScheme(url.Scheme);
                                url.Port = defPort == portNum ? (int?)null : portNum;
                                buffer.Clear();
                            }
                            if (overrideState != null) return url;
                            state = State.PathStart;
                            continue;
                        }
                        else
                        {
                            errors?.Add("invalid-port");
                            return null;
                        }
                        break;

                    case State.File:
                        url.Scheme = "file";
                        url.Host = WhatwgHost.Empty;
                        if (c == '/' || c == '\\')
                        {
                            if (c == '\\') errors?.Add("invalid-reverse-solidus");
                            state = State.FileSlash;
                        }
                        else if (baseRecord != null && baseRecord.Scheme == "file")
                        {
                            url.Host = baseRecord.Host;
                            url.Path = new List<string>(baseRecord.Path);
                            url.Query = baseRecord.Query;
                            if (c == '?')
                            {
                                url.Query = "";
                                state = State.Query;
                            }
                            else if (c == '#')
                            {
                                url.Fragment = "";
                                state = State.Fragment;
                            }
                            else if (c != -1)
                            {
                                url.Query = null;
                                if (!StartsWithWindowsDriveLetter(input, pointer))
                                    url.ShortenedPath();
                                else
                                {
                                    errors?.Add("file-invalid-windows-drive-letter");
                                    url.Path.Clear();
                                }
                                state = State.Path;
                                continue;
                            }
                        }
                        else
                        {
                            state = State.Path;
                            continue;
                        }
                        break;

                    case State.FileSlash:
                        if (c == '/' || c == '\\')
                        {
                            if (c == '\\') errors?.Add("invalid-reverse-solidus");
                            state = State.FileHost;
                        }
                        else
                        {
                            if (baseRecord != null && baseRecord.Scheme == "file")
                            {
                                url.Host = baseRecord.Host;
                                if (!StartsWithWindowsDriveLetter(input, pointer) &&
                                    baseRecord.Path.Count > 0 &&
                                    IsNormalizedWindowsDriveLetter(baseRecord.Path[0]))
                                {
                                    url.Path.Add(baseRecord.Path[0]);
                                }
                            }
                            state = State.Path;
                            continue;
                        }
                        break;

                    case State.FileHost:
                        if (c == -1 || c == '/' || c == '\\' || c == '?' || c == '#')
                        {
                            if (overrideState == null && IsWindowsDriveLetter(buffer.ToString()))
                            {
                                errors?.Add("file-invalid-windows-drive-letter-host");
                                state = State.Path;
                            }
                            else if (buffer.Length == 0)
                            {
                                url.Host = WhatwgHost.Empty;
                                if (overrideState != null) return url;
                                state = State.PathStart;
                            }
                            else
                            {
                                var host = ParseHost(buffer.ToString(), true, errors);
                                if (host == null) return null;
                                if (host.Kind == HostKind.Domain && host.Domain == "localhost")
                                    host = WhatwgHost.Empty;
                                url.Host = host;
                                if (overrideState != null) return url;
                                buffer.Clear();
                                state = State.PathStart;
                            }
                            if (c != -1) continue;
                        }
                        else
                        {
                            buffer.Append((char)c);
                        }
                        break;

                    case State.PathStart:
                        if (url.IsSpecial)
                        {
                            if (c == '\\') errors?.Add("invalid-reverse-solidus");
                            state = State.Path;
                            if (c != '/' && c != '\\') continue;
                        }
                        else if (overrideState == null && c == '?')
                        {
                            url.Query = "";
                            state = State.Query;
                        }
                        else if (overrideState == null && c == '#')
                        {
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                        else if (c != -1)
                        {
                            state = State.Path;
                            if (c != '/') continue;
                        }
                        else if (overrideState != null && url.Host == null)
                        {
                            url.Path.Add("");
                        }
                        break;

                    case State.Path:
                        if ((c == -1 || c == '/') ||
                            (url.IsSpecial && c == '\\') ||
                            (overrideState == null && (c == '?' || c == '#')))
                        {
                            if (url.IsSpecial && c == '\\') errors?.Add("invalid-reverse-solidus");

                            var segment = buffer.ToString();
                            if (IsDoubleDotPathSegment(segment))
                            {
                                url.ShortenedPath();
                                if (c != '/' && !(url.IsSpecial && c == '\\'))
                                    url.Path.Add("");
                            }
                            else if (IsSingleDotPathSegment(segment))
                            {
                                if (c != '/' && !(url.IsSpecial && c == '\\'))
                                    url.Path.Add("");
                            }
                            else
                            {
                                if (url.Scheme == "file" && url.Path.Count == 0 && IsWindowsDriveLetter(segment))
                                {
                                    if (segment.Length == 2 && segment[1] == '|')
                                        segment = segment[0] + ":";
                                }
                                url.Path.Add(segment);
                            }

                            buffer.Clear();

                            if (c == '?')
                            {
                                url.Query = "";
                                state = State.Query;
                            }
                            else if (c == '#')
                            {
                                url.Fragment = "";
                                state = State.Fragment;
                            }
                        }
                        else
                        {
                            if (!IsUrlCodePoint(c) && c != '%')
                                errors?.Add("invalid-url-unit");
                            if (c == '%' && !IsAsciiHex(pointer + 1 < len ? input[pointer + 1] : '\0') &&
                                !IsAsciiHex(pointer + 2 < len ? input[pointer + 2] : '\0'))
                                errors?.Add("invalid-url-unit");
                            buffer.Append(PercentEncodeChar((char)c, UrlEncoder.PathSet));
                        }
                        break;

                    case State.CannotBeABaseUrlPath:
                        if (c == '?')
                        {
                            url.Query = "";
                            state = State.Query;
                        }
                        else if (c == '#')
                        {
                            url.Fragment = "";
                            state = State.Fragment;
                        }
                        else if (c != -1)
                        {
                            if (!IsUrlCodePoint(c) && c != '%')
                                errors?.Add("invalid-url-unit");
                            if (url.Path.Count == 0) url.Path.Add("");
                            url.Path[0] += PercentEncodeChar((char)c, UrlEncoder.C0Set);
                        }
                        break;

                    case State.Query:
                        if (c == '#' || c == -1)
                        {
                            bool useSpecialQuerySet = url.IsSpecial;
                            var queryEncoded = new StringBuilder();
                            foreach (var qChar in buffer.ToString())
                                queryEncoded.Append(PercentEncodeChar(qChar, useSpecialQuerySet ? UrlEncoder.SpecialQuerySet : UrlEncoder.QuerySet));
                            url.Query = queryEncoded.ToString();
                            buffer.Clear();
                            if (c == '#')
                            {
                                url.Fragment = "";
                                state = State.Fragment;
                            }
                        }
                        else if (c != -1)
                        {
                            if (!IsUrlCodePoint(c) && c != '%')
                                errors?.Add("invalid-url-unit");
                            buffer.Append((char)c);
                        }
                        break;

                    case State.Fragment:
                        if (c != -1)
                        {
                            if (!IsUrlCodePoint(c) && c != '%')
                                errors?.Add("invalid-url-unit");
                            url.Fragment += PercentEncodeChar((char)c, UrlEncoder.FragmentSet);
                        }
                        break;
                }

                if (c == -1) break;
                pointer++;
            }

            return url;
        }

        // ---- Host parsing §3.3 ----

        private static WhatwgHost ParseHost(string input, bool isOpaque, List<string> errors)
        {
            if (input.StartsWith("["))
            {
                if (!input.EndsWith("]")) { errors?.Add("invalid-ipv6-address"); return null; }
                return ParseIpv6(input.Substring(1, input.Length - 2), errors);
            }

            if (isOpaque) return WhatwgHost.FromOpaque(PercentEncodeOpaqueHost(input));

            // Percent-decode host, then IDNA encode
            var decoded = PercentDecodeString(input);
            var asciiDomain = DomainToAscii(decoded, errors);
            if (asciiDomain == null) return null;

            // Check for IPv4
            if (EndsWithNumber(asciiDomain))
            {
                var ipv4 = ParseIpv4(asciiDomain, errors);
                if (ipv4 == null) return null;
                return ipv4;
            }

            // Validate no forbidden host code points
            foreach (var ch in asciiDomain)
            {
                if (IsForbiddenHostCodePoint(ch))
                {
                    errors?.Add("host-missing");
                    return null;
                }
            }

            return WhatwgHost.FromDomain(asciiDomain);
        }

        private static string DomainToAscii(string domain, List<string> errors)
        {
            if (string.IsNullOrEmpty(domain)) { errors?.Add("host-missing"); return null; }
            try
            {
                // Use IdnMapping for IDNA processing
                var idn = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true };
                var result = idn.GetAscii(domain.ToLowerInvariant());
                return result;
            }
            catch
            {
                // Fall back to lowercased domain if IDNA fails (non-ASCII TLDs etc.)
                var lower = domain.ToLowerInvariant();
                foreach (var ch in lower)
                {
                    if (IsForbiddenHostCodePoint(ch))
                    {
                        errors?.Add("domain-to-ascii");
                        return null;
                    }
                }
                return lower;
            }
        }

        private static WhatwgHost ParseIpv4(string input, List<string> errors)
        {
            var parts = input.Split('.');
            if (parts.Length > 4) { errors?.Add("ipv4-too-many-parts"); return null; }

            var numbers = new List<long>();
            foreach (var part in parts)
            {
                if (part == "") { errors?.Add("ipv4-empty-part"); return null; }
                var n = ParseIpv4Number(part, out bool validationError);
                if (validationError) errors?.Add("ipv4-non-decimal-part");
                if (n == null) { errors?.Add("ipv4-non-numeric-part"); return null; }
                numbers.Add(n.Value);
            }

            foreach (var n in numbers.Take(numbers.Count - 1))
            {
                if (n > 255) { errors?.Add("ipv4-out-of-range-part"); return null; }
            }

            var last = numbers[numbers.Count - 1];
            if (last >= (long)Math.Pow(256, 5 - numbers.Count))
            { errors?.Add("ipv4-out-of-range-part"); return null; }

            long addr = 0;
            for (int i = 0; i < numbers.Count - 1; i++)
                addr += numbers[i] * (long)Math.Pow(256, 3 - i);
            addr += last;

            return WhatwgHost.FromIpv4((uint)addr);
        }

        private static long? ParseIpv4Number(string input, out bool validationError)
        {
            validationError = false;
            if (string.IsNullOrEmpty(input)) return null;

            int radix = 10;
            string digits = input;

            if (input.Length >= 2 && input[0] == '0' && (input[1] == 'x' || input[1] == 'X'))
            { radix = 16; digits = input.Substring(2); validationError = true; }
            else if (input.Length >= 2 && input[0] == '0')
            { radix = 8; digits = input.Substring(1); validationError = true; }

            try { return Convert.ToInt64(digits, radix); }
            catch { return null; }
        }

        private static WhatwgHost ParseIpv6(string input, List<string> errors)
        {
            var pieces = new ushort[8];
            int pieceIndex = 0;
            int compress = -1;
            int pointer = 0;

            if (pointer < input.Length && input[pointer] == ':')
            {
                if (pointer + 1 >= input.Length || input[pointer + 1] != ':')
                { errors?.Add("ipv6-invalid-compression"); return null; }
                pointer += 2;
                pieceIndex++;
                compress = pieceIndex;
            }

            while (pointer < input.Length)
            {
                if (pieceIndex == 8) { errors?.Add("ipv6-too-many-pieces"); return null; }

                if (input[pointer] == ':')
                {
                    if (compress != -1) { errors?.Add("ipv6-multiple-compression"); return null; }
                    pointer++;
                    pieceIndex++;
                    compress = pieceIndex;
                    continue;
                }

                int value = 0, length = 0;
                while (length < 4 && pointer < input.Length && IsAsciiHex(input[pointer]))
                {
                    value = value * 16 + HexVal(input[pointer]);
                    pointer++; length++;
                }

                if (pointer < input.Length && input[pointer] == '.')
                {
                    if (length == 0) { errors?.Add("ipv6-invalid-value"); return null; }
                    pointer -= length;
                    if (pieceIndex > 6) { errors?.Add("ipv6-elided-pairs-not-sufficient"); return null; }

                    int numbersSeen = 0;
                    while (pointer < input.Length)
                    {
                        int ipv4Piece = -1;
                        if (numbersSeen > 0)
                        {
                            if (input[pointer] == '.' && numbersSeen < 4) pointer++;
                            else { errors?.Add("ipv6-invalid-value"); return null; }
                        }
                        if (!IsAsciiDigit(input[pointer])) { errors?.Add("ipv6-invalid-value"); return null; }
                        while (pointer < input.Length && IsAsciiDigit(input[pointer]))
                        {
                            int n = input[pointer] - '0';
                            if (ipv4Piece == -1) ipv4Piece = n;
                            else if (ipv4Piece == 0) { errors?.Add("ipv6-invalid-value"); return null; }
                            else ipv4Piece = ipv4Piece * 10 + n;
                            if (ipv4Piece > 255) { errors?.Add("ipv6-out-of-range-part"); return null; }
                            pointer++;
                        }
                        pieces[pieceIndex] = (ushort)(pieces[pieceIndex] * 256 + ipv4Piece);
                        numbersSeen++;
                        if (numbersSeen == 2 || numbersSeen == 4) pieceIndex++;
                    }
                    if (numbersSeen != 4) { errors?.Add("ipv6-invalid-ipv4-address"); return null; }
                    break;
                }
                else if (pointer < input.Length && input[pointer] == ':')
                {
                    pointer++;
                    if (pointer == input.Length) { errors?.Add("ipv6-invalid-value"); return null; }
                }
                else if (pointer != input.Length)
                {
                    errors?.Add("ipv6-invalid-value"); return null;
                }

                pieces[pieceIndex] = (ushort)value;
                pieceIndex++;
            }

            if (compress != -1)
            {
                int swaps = pieceIndex - compress;
                pieceIndex = 7;
                while (pieceIndex != 0 && swaps > 0)
                {
                    var tmp = pieces[pieceIndex];
                    pieces[pieceIndex] = pieces[compress + swaps - 1];
                    pieces[compress + swaps - 1] = tmp;
                    pieceIndex--;
                    swaps--;
                }
            }
            else if (pieceIndex != 8)
            {
                errors?.Add("ipv6-too-few-pieces"); return null;
            }

            return WhatwgHost.FromIpv6(pieces);
        }

        // ---- Helpers ----

        private static string StripLeadingTrailingC0AndSpace(string input, List<string> errors)
        {
            int start = 0, end = input.Length;
            while (start < end && IsC0ControlOrSpace(input[start])) start++;
            while (end > start && IsC0ControlOrSpace(input[end - 1])) end--;
            if (start > 0 || end < input.Length) errors?.Add("invalid-url-unit");
            return input.Substring(start, end - start);
        }

        private static string StripTabAndNewline(string input, List<string> errors)
        {
            if (input.IndexOfAny(new[] { '\t', '\n', '\r' }) < 0) return input;
            errors?.Add("invalid-url-unit");
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (ch != '\t' && ch != '\n' && ch != '\r') sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string PercentDecodeString(string input)
        {
            if (input.IndexOf('%') < 0) return input;
            var bytes = new List<byte>();
            int i = 0;
            while (i < input.Length)
            {
                if (input[i] == '%' && i + 2 < input.Length &&
                    IsAsciiHex(input[i + 1]) && IsAsciiHex(input[i + 2]))
                {
                    bytes.Add((byte)(HexVal(input[i + 1]) * 16 + HexVal(input[i + 2])));
                    i += 3;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(input[i].ToString()));
                    i++;
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static string PercentEncodeChar(char c, bool[] encodeSet)
        {
            var bytes = Encoding.UTF8.GetBytes(new[] { c });
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                if (b < encodeSet.Length && !encodeSet[b])
                    sb.Append((char)b);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        private static string PercentEncodeOpaqueHost(string input)
        {
            var sb = new StringBuilder(input.Length * 3);
            foreach (var c in input)
                sb.Append(PercentEncodeChar(c, UrlEncoder.ForbiddenHostSet));
            return sb.ToString();
        }

        private static bool EndsWithNumber(string domain)
        {
            var parts = domain.Split('.');
            if (parts.Length == 0) return false;
            var last = parts[parts.Length - 1];
            if (last == "") return parts.Length >= 2 && IsNumber(parts[parts.Length - 2]);
            return IsNumber(last);
        }

        private static bool IsNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("0x") || s.StartsWith("0X")) return true;
            return s.All(c => c >= '0' && c <= '9');
        }

        private static bool StartsWithWindowsDriveLetter(string input, int pointer)
        {
            if (pointer + 1 >= input.Length) return false;
            if (!char.IsLetter(input[pointer])) return false;
            var next = input[pointer + 1];
            if (next != ':' && next != '|') return false;
            if (pointer + 2 >= input.Length) return true;
            var after = input[pointer + 2];
            return after == '/' || after == '\\' || after == '?' || after == '#';
        }

        private static bool IsWindowsDriveLetter(string s) =>
            s?.Length == 2 && char.IsLetter(s[0]) && (s[1] == ':' || s[1] == '|');

        private static bool IsNormalizedWindowsDriveLetter(string s) =>
            s?.Length == 2 && char.IsLetter(s[0]) && s[1] == ':';

        private static bool IsDoubleDotPathSegment(string s) =>
            s == ".." || s == ".%2E" || s == "%2E." || s == "%2E%2E" ||
            s.Equals("..", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("%2e.", StringComparison.OrdinalIgnoreCase) ||
            s.Equals(".%2e", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);

        private static bool IsSingleDotPathSegment(string s) =>
            s == "." || s.Equals("%2e", StringComparison.OrdinalIgnoreCase);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiAlphanumeric(int c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiDigit(int c) => c >= '0' && c <= '9';

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int HexVal(char c) =>
            c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c - 'A' + 10;

        private static bool IsC0ControlOrSpace(char c) => c <= 0x1F || c == ' ';

        private static bool IsUrlCodePoint(int c)
        {
            if (c < 0x21) return c == 0x20; // space is allowed in some contexts; handled elsewhere
            // URL code points: ASCII printable + high Unicode
            return (c >= 0x21 && c <= 0x7E) || c > 0x9F;
        }

        private static bool IsForbiddenHostCodePoint(char c)
        {
            // Forbidden host code points per spec §3.1
            return c == '\0' || c == '\t' || c == '\n' || c == '\r' || c == ' ' ||
                   c == '#' || c == '/' || c == ':' || c == '<' || c == '>' ||
                   c == '?' || c == '@' || c == '[' || c == '\\' || c == ']' ||
                   c == '^' || c == '|';
        }
    }

    /// <summary>Percent-encoding character sets per WHATWG URL §1.3.</summary>
    internal static class UrlEncoder
    {
        // C0 control percent-encode set: U+0000–001F, > U+007E
        public static readonly bool[] C0Set = BuildSet(c => c <= 0x1F || c > 0x7E);

        // Fragment percent-encode set: C0 + space " < > `
        public static readonly bool[] FragmentSet = BuildSet(c => c <= 0x1F || c > 0x7E ||
            c == ' ' || c == '"' || c == '<' || c == '>' || c == '`');

        // Query percent-encode set: C0 + space " # < >
        public static readonly bool[] QuerySet = BuildSet(c => c <= 0x1F || c > 0x7E ||
            c == ' ' || c == '"' || c == '#' || c == '<' || c == '>');

        // Special query percent-encode set: query + '
        public static readonly bool[] SpecialQuerySet = BuildSet(c => c <= 0x1F || c > 0x7E ||
            c == ' ' || c == '"' || c == '#' || c == '<' || c == '>' || c == '\'');

        // Path percent-encode set: query + ? ` { }
        public static readonly bool[] PathSet = BuildSet(c => c <= 0x1F || c > 0x7E ||
            c == ' ' || c == '"' || c == '#' || c == '<' || c == '>' || c == '\'' ||
            c == '?' || c == '`' || c == '{' || c == '}');

        // Userinfo percent-encode set: path + / : ; = @ [ \ ] ^ |
        public static readonly bool[] UserinfoSet = BuildSet(c => c <= 0x1F || c > 0x7E ||
            c == ' ' || c == '"' || c == '#' || c == '<' || c == '>' || c == '\'' ||
            c == '?' || c == '`' || c == '{' || c == '}' ||
            c == '/' || c == ':' || c == ';' || c == '=' || c == '@' ||
            c == '[' || c == '\\' || c == ']' || c == '^' || c == '|');

        // Forbidden host code points (for opaque host encoding)
        public static readonly bool[] ForbiddenHostSet = BuildSet(c =>
            c == '\0' || c == '\t' || c == '\n' || c == '\r' || c == ' ' ||
            c == '#' || c == '/' || c == ':' || c == '<' || c == '>' ||
            c == '?' || c == '@' || c == '[' || c == '\\' || c == ']' ||
            c == '^' || c == '|');

        // The encode set is: true = must encode, false = leave as-is
        private static bool[] BuildSet(Func<int, bool> shouldEncode)
        {
            var set = new bool[256];
            for (int i = 0; i < 256; i++)
                set[i] = shouldEncode(i);
            return set;
        }
    }
}
