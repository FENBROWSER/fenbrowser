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

                            window.result_callback = function (test) {
                                emit('__FEN_WPT_RESULT__', {
                                    name: test && test.name ? test.name : '',
                                    status: test && typeof test.status !== 'undefined' ? test.status : -1,
                                    message: test && test.message ? String(test.message) : ''
                                });
                            };

                            window.completion_callback = function (tests, status) {
                                emit('__FEN_WPT_COMPLETE__', {
                                    status: status && typeof status.status !== 'undefined' ? String(status.status) : '',
                                    message: status && status.message ? String(status.message) : '',
                                    total: Array.isArray(tests) ? tests.length : 0
                                });
                            };

                            if (window.add_result_callback) {
                                window.add_result_callback(window.result_callback);
                            }

                            if (window.add_completion_callback) {
                                window.add_completion_callback(window.completion_callback);
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

            var normalizedPath = requestUri.LocalPath.Replace('\\', '/');
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
