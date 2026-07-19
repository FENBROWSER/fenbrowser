using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Storage;
using Xunit;

namespace FenBrowser.Tests.Diagnostics
{
    /// <summary>
    /// Regression coverage for <see cref="CookieDiagnostics"/> - the PII-safe
    /// cookie ingress/egress logger added for the Google-search investigation.
    ///
    /// Invariants under test:
    ///   - Logging is off unless the setting is enabled.
    ///   - When on, every Set-Cookie via the cookie jar emits a structured
    ///     <c>set-cookie</c> log entry with name/length/tag fields.
    ///   - When on, building a request cookie header emits an
    ///     <c>outbound-cookie-header</c> entry.
    ///   - Cookie VALUES never appear in any emitted log message or data field.
    ///   - Rejected cookies still produce a <c>set-cookie-rejected</c> entry.
    /// </summary>
    [Collection("Engine Tests")]
    public class CookieDiagnosticsTests : IDisposable
    {
        private readonly bool _originalLogCookies;
        private readonly List<LogEntry> _captured = new();
        private readonly Action<LogEntry> _handler;

        public CookieDiagnosticsTests()
        {
            _originalLogCookies = BrowserSettings.Instance.Logging.LogCookies;
            BrowserSettings.Instance.Logging.LogCookies = true;

            _handler = entry =>
            {
                lock (_captured)
                {
                    _captured.Add(entry);
                }
            };
            LogManager.LogEntryAdded += _handler;
        }

        public void Dispose()
        {
            LogManager.LogEntryAdded -= _handler;
            BrowserSettings.Instance.Logging.LogCookies = _originalLogCookies;
        }

        [Fact]
        public void SetDocumentCookie_EmitsIngressEntryWithRedactedValue()
        {
            var jar = new BrowserCookieJar();
            var docUri = new Uri("https://www.google.com/search?q=test");

            // Opaque token-like value - must not appear in any log entry.
            const string secretValue = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789==";
            jar.SetDocumentCookie(docUri, $"SG_SS={secretValue}; Path=/");

            var ingress = SnapshotEntries()
                .Where(e => Equals(GetData(e, "event"), "set-cookie"))
                .ToList();

            Assert.NotEmpty(ingress);
            var entry = ingress.Single();
            Assert.Equal("SG_SS", GetData(entry, "name"));
            Assert.Equal(true, GetData(entry, "accepted"));
            Assert.False(entry.Data.ContainsKey("valueLength"));
            Assert.False(entry.Data.ContainsKey("valueTag"));
            Assert.DoesNotContain(secretValue, entry.Message ?? string.Empty);
            AssertNoPlaintextValue(entry, secretValue);
        }

        [Fact]
        public void StoreResponseCookies_EmitsIngressEntryPerCookie()
        {
            var jar = new BrowserCookieJar();
            var responseUri = new Uri("https://www.google.com/search");

            using var request = new HttpRequestMessage(HttpMethod.Get, responseUri);
            using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "SG_SS=opaqueValueOne; Path=/; Secure");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "NID=opaqueValueTwo; Path=/; Domain=.google.com");

            jar.StoreResponseCookies(response);

            var ingress = SnapshotEntries()
                .Where(e => Equals(GetData(e, "event"), "set-cookie"))
                .ToList();

            Assert.Equal(2, ingress.Count);
            Assert.Contains(ingress, e => "SG_SS".Equals(GetData(e, "name")));
            Assert.Contains(ingress, e => "NID".Equals(GetData(e, "name")));

            foreach (var entry in ingress)
            {
                Assert.Equal("http-response", GetData(entry, "source"));
                AssertNoPlaintextValue(entry, "opaqueValueOne");
                AssertNoPlaintextValue(entry, "opaqueValueTwo");
            }
        }

        [Fact]
        public void GetRequestCookieHeader_EmitsEgressEntryWithCookieNames()
        {
            var jar = new BrowserCookieJar();
            var docUri = new Uri("https://www.google.com/search");
            jar.SetDocumentCookie(docUri, "SG_SS=value1; Path=/");
            jar.SetDocumentCookie(docUri, "OTHER=value2; Path=/");

            ClearCaptured();

            var header = jar.GetRequestCookieHeader(docUri, docUri, isTopLevelNavigation: true, requestMethod: "GET");

            Assert.False(string.IsNullOrEmpty(header));
            var egress = SnapshotEntries()
                .Where(e => Equals(GetData(e, "event"), "outbound-cookie-header"))
                .ToList();

            Assert.Single(egress);
            var entry = egress[0];
            Assert.Equal("www.google.com", GetData(entry, "requestHost"));
            Assert.Equal(true, GetData(entry, "topLevelNavigation"));
            var names = GetData(entry, "cookieNames") as IEnumerable<string>;
            Assert.NotNull(names);
            var nameList = names.ToList();
            Assert.Contains("SG_SS", nameList);
            Assert.Contains("OTHER", nameList);

            AssertNoPlaintextValue(entry, "value1");
            AssertNoPlaintextValue(entry, "value2");
        }

        [Fact]
        public void Disabled_EmitsNothing()
        {
            BrowserSettings.Instance.Logging.LogCookies = false;
            ClearCaptured();

            var jar = new BrowserCookieJar();
            var docUri = new Uri("https://www.google.com/");
            jar.SetDocumentCookie(docUri, "X=y; Path=/");
            _ = jar.GetRequestCookieHeader(docUri);

            var cookieEvents = SnapshotEntries()
                .Where(e => GetData(e, "event") is string s && s.StartsWith("set-cookie", StringComparison.Ordinal)
                            || GetData(e, "event") is string s2 && s2 == "outbound-cookie-header")
                .ToList();

            Assert.Empty(cookieEvents);
        }

        [Fact]
        public void DocumentCookie_MalformedHeader_EmitsRejection()
        {
            var jar = new BrowserCookieJar();
            var docUri = new Uri("https://www.google.com/");

            jar.SetDocumentCookie(docUri, "this-has-no-equals-sign");

            var rejections = SnapshotEntries()
                .Where(e => Equals(GetData(e, "event"), "set-cookie-rejected"))
                .ToList();

            Assert.NotEmpty(rejections);
            var entry = rejections[0];
            Assert.Equal(false, GetData(entry, "accepted"));
            Assert.False(string.IsNullOrEmpty(GetData(entry, "reason") as string));
        }

        // helpers

        private List<LogEntry> SnapshotEntries()
        {
            lock (_captured)
            {
                return _captured.ToList();
            }
        }

        private void ClearCaptured()
        {
            lock (_captured)
            {
                _captured.Clear();
            }
        }

        private static object GetData(LogEntry entry, string key)
        {
            if (entry?.Data == null) return null;
            return entry.Data.TryGetValue(key, out var v) ? v : null;
        }

        private static void AssertNoPlaintextValue(LogEntry entry, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Assert.DoesNotContain(value, entry.Message ?? string.Empty);
            if (entry.Data == null) return;
            foreach (var kv in entry.Data)
            {
                switch (kv.Value)
                {
                    case string s:
                        Assert.DoesNotContain(value, s);
                        break;
                    case IEnumerable<string> list:
                        foreach (var item in list)
                        {
                            Assert.DoesNotContain(value, item ?? string.Empty);
                        }
                        break;
                }
            }
        }
    }
}

