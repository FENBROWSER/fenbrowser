using System;
using System.IO;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tooling.Host
{
    internal static class ToolingBrowserHostOptions
    {
        public static BrowserHostOptions CreateForWpt(string wptRootPath)
        {
            return new BrowserHostOptions
            {
                RequestUriMapper = requestUri => MapWptUri(wptRootPath, requestUri),
                ScriptOverrideProvider = requestUri =>
                {
                    if (requestUri == null)
                    {
                        return null;
                    }

                    var uriText = requestUri.ToString();
                    if (uriText.IndexOf("testharnessreport.js", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return null;
                    }

                    return """
                        (function () {
                            function emit(marker, payload) {
                                try {
                                    console.log(marker + JSON.stringify(payload || {}));
                                } catch (err) {
                                    console.log(marker + '{"error":"serialization_failed"}');
                                }
                            }

                            function onResult(test) {
                                emit('__FEN_WPT_RESULT__', {
                                    name: test && test.name ? test.name : '',
                                    status: test && typeof test.status !== 'undefined' ? test.status : -1,
                                    message: test && test.message ? String(test.message) : ''
                                });
                            }

                            function onComplete(tests, status) {
                                emit('__FEN_WPT_COMPLETE__', {
                                    status: status && typeof status.status !== 'undefined' ? String(status.status) : '',
                                    message: status && status.message ? String(status.message) : '',
                                    total: Array.isArray(tests) ? tests.length : 0
                                });
                            }

                            var attached = false;
                            function tryAttach() {
                                if (attached) {
                                    return;
                                }

                                if (typeof window.add_result_callback === 'function') {
                                    window.add_result_callback(onResult);
                                } else {
                                    return false;
                                }

                                if (typeof window.add_completion_callback === 'function') {
                                    window.add_completion_callback(onComplete);
                                }

                                attached = true;
                                emit('__FEN_WPT_HOOKED__', { ok: true });
                                return true;
                            }

                            if (!tryAttach()) {
                                var retries = 0;
                                var maxRetries = 200;
                                var timer = window.setInterval(function () {
                                    retries++;
                                    if (tryAttach() || retries >= maxRetries) {
                                        window.clearInterval(timer);
                                        if (!attached) {
                                            emit('__FEN_WPT_HOOKED__', { ok: false, reason: 'callbacks_unavailable' });
                                        }
                                    }
                                }, 10);
                            }
                        })();
                        """;
                }
            };
        }

        private static Uri MapWptUri(string wptRootPath, Uri requestUri)
        {
            if (requestUri == null || string.IsNullOrWhiteSpace(wptRootPath))
            {
                return requestUri;
            }

            if (!requestUri.IsFile)
            {
                return requestUri;
            }

            if (File.Exists(requestUri.LocalPath))
            {
                return requestUri;
            }

            var normalizedPath = requestUri.LocalPath.Replace('\\', '/');

            // WPT tests commonly reference harness assets as absolute-from-root paths
            // (e.g. "/resources/testharness.js") even when the page itself is loaded
            // from file://. Map those into the configured WPT checkout root.
            var rootSupportMappings = new[] { "/resources/", "/common/", "/fonts/", "/infrastructure/" };
            foreach (var marker in rootSupportMappings)
            {
                var markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var supportRelativePath = normalizedPath[(markerIndex + 1)..];
                var mappedSupportPath = Path.Combine(wptRootPath, supportRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(mappedSupportPath))
                {
                    return new Uri(mappedSupportPath);
                }
            }

            if (!normalizedPath.Contains("/wpt/", StringComparison.OrdinalIgnoreCase) &&
                !normalizedPath.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
            {
                return requestUri;
            }

            string relativePath = normalizedPath;
            var wptIndex = normalizedPath.IndexOf("/wpt/", StringComparison.OrdinalIgnoreCase);
            if (wptIndex >= 0)
            {
                relativePath = normalizedPath[(wptIndex + "/wpt/".Length)..];
            }
            else
            {
                var testsIndex = normalizedPath.IndexOf("/tests/", StringComparison.OrdinalIgnoreCase);
                if (testsIndex >= 0)
                {
                    relativePath = normalizedPath[(testsIndex + "/tests/".Length)..];
                }
            }

            var mappedPath = Path.Combine(wptRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(mappedPath))
            {
                return requestUri;
            }

            return new Uri(mappedPath);
        }
    }
}
