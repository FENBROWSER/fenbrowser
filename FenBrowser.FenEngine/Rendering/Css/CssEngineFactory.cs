using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public class CustomCssEngine : ICssEngine
    {
        public string EngineName => "FenBrowser.Custom";

        private Dictionary<Node, CssComputed> _lastComputed;

        public List<CssLoader.CssSource> LastSources { get; private set; }

        public async Task<Dictionary<Node, CssComputed>> ComputeStylesAsync(
            Element root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            double? viewportHeight = null,
            FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            try
            {
                var result = await CssLoader.ComputeWithResultAsync(
                    root,
                    baseUri,
                    fetchExternalCssAsync,
                    viewportWidth,
                    viewportHeight,
                    null,
                    deadline);

                _lastComputed = result.Computed;
                LastSources = null;
                return result.Computed;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[CustomCssEngine] ComputeStylesAsync error: {ex.Message}", LogCategory.Rendering);
                return new Dictionary<Node, CssComputed>();
            }
        }

        public CssComputed GetComputedStyle(Element element)
        {
            if (_lastComputed != null && _lastComputed.TryGetValue(element, out var style))
            {
                return style;
            }

            return new CssComputed();
        }

        public Dictionary<string, string> ParseInlineStyle(string styleValue)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(styleValue))
            {
                return result;
            }

            foreach (var declaration in SplitDeclarations(styleValue))
            {
                var colon = FindTopLevelColon(declaration);
                if (colon <= 0)
                {
                    continue;
                }

                var name = declaration[..colon].Trim().ToLowerInvariant();
                var value = declaration[(colon + 1)..].Trim();
                if (name.Length != 0)
                {
                    result[name] = value;
                }
            }

            return result;
        }

        private static IEnumerable<string> SplitDeclarations(string input)
        {
            var current = new StringBuilder();
            var depth = 0;
            var quote = '\0';
            var escaped = false;
            var inComment = false;

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];

                if (inComment)
                {
                    if (c == '*' && i + 1 < input.Length && input[i + 1] == '/')
                    {
                        inComment = false;
                        i++;
                    }
                    continue;
                }

                if (quote != '\0')
                {
                    current.Append(c);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
                {
                    inComment = true;
                    i++;
                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                    current.Append(c);
                    continue;
                }

                if (c is '(' or '[' or '{')
                {
                    depth++;
                    current.Append(c);
                    continue;
                }

                if (c is ')' or ']' or '}')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                    current.Append(c);
                    continue;
                }

                if (c == ';' && depth == 0)
                {
                    if (current.Length != 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length != 0)
            {
                yield return current.ToString();
            }
        }

        private static int FindTopLevelColon(string input)
        {
            var depth = 0;
            var quote = '\0';
            var escaped = false;

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (quote != '\0')
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                }
                else if (c == ':' && depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public static class CssEngineFactory
    {
        public static ICssEngine GetEngine()
        {
            return new CustomCssEngine();
        }

        public static void ClearCache()
        {
        }
    }
}
