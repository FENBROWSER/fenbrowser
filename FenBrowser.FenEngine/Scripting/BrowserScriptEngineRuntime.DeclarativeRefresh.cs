using System;
using System.Globalization;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// HTML §4.2.5.3 "Refresh state" (<c>&lt;meta http-equiv="refresh"&gt;</c>). github.com's
/// "Continue with Google" lands on a blank "Redirecting to Google" page whose only way
/// forward is <c>content="0;url=https://accounts.google.com/..."</c>; without this the
/// tab stayed white.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>
    /// Runs once the document has completely loaded (the refresh timer counts from
    /// there): the first refresh meta in tree order whose content parses schedules a
    /// navigation of this document's navigable after its delay. A document inside a
    /// sandboxed iframe has the sandboxed automatic features flag and never refreshes.
    /// </summary>
    private void ScheduleDeclarativeRefresh(Document document)
    {
        if (document == null || _embeddingFrameElement?.HasAttribute("sandbox") == true)
        {
            return;
        }

        foreach (var meta in document.Descendants().OfType<Element>())
        {
            if (!string.Equals(meta.LocalName, "meta", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(meta.GetAttribute("http-equiv")?.Trim(), "refresh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = meta.GetAttribute("content");
            if (string.IsNullOrEmpty(content) ||
                !TryParseDeclarativeRefresh(content, out var delaySeconds, out var urlString))
            {
                continue;
            }

            var documentUrl = _currentBaseUri;
            Uri target;
            if (urlString == null)
            {
                target = documentUrl;
            }
            else if (!Uri.TryCreate(documentUrl, urlString, out target) &&
                     !Uri.TryCreate(urlString, UriKind.Absolute, out target))
            {
                return;
            }

            if (target == null)
            {
                return;
            }

            ScheduleRefreshNavigation(target, delaySeconds);
            return;
        }
    }

    private void ScheduleRefreshNavigation(Uri target, double delaySeconds)
    {
        FenBrowser.Core.EngineLogCompat.Debug(
            $"[DeclarativeRefresh] {target} after {delaySeconds.ToString(CultureInfo.InvariantCulture)}s",
            LogCategory.Navigation);

        // A host timer ties the refresh to this document: it is dropped with the
        // document's timers when the page navigates or is torn down first.
        var navigate = _interpreter.AllocateNativeFunction(
            "declarativeRefresh",
            (_, _) =>
            {
                NavigateOwningBrowsingContext(target);
                return JsValue.Undefined;
            },
            length: 0);
        double delayMs = Math.Min(delaySeconds * 1000.0, int.MaxValue);
        ScheduleFenJsTimer(new[] { navigate, JsValue.FromNumber(delayMs) }, repeat: false);
    }

    /// <summary>
    /// The "shared declarative refresh steps" parse: a non-negative integer delay (a
    /// fractional part is ignored), then optionally <c>;</c> or <c>,</c>, an optional
    /// case-insensitive <c>url=</c>, and a URL that may be quoted. A null URL means the
    /// document's own URL.
    /// </summary>
    internal static bool TryParseDeclarativeRefresh(string input, out double delaySeconds, out string url)
    {
        delaySeconds = 0;
        url = null;
        int position = 0;
        SkipAsciiWhitespace(input, ref position);

        int digitsStart = position;
        while (position < input.Length && input[position] is >= '0' and <= '9')
        {
            position++;
        }

        if (position == digitsStart)
        {
            if (position >= input.Length || input[position] != '.')
            {
                return false;
            }
        }
        else if (!double.TryParse(input.AsSpan(digitsStart, position - digitsStart), NumberStyles.None, CultureInfo.InvariantCulture, out delaySeconds))
        {
            return false;
        }

        while (position < input.Length && (input[position] is >= '0' and <= '9' || input[position] == '.'))
        {
            position++;
        }

        if (position >= input.Length)
        {
            return true;
        }

        if (input[position] is not (';' or ',') && !IsAsciiWhitespace(input[position]))
        {
            return false;
        }

        SkipAsciiWhitespace(input, ref position);
        if (position < input.Length && input[position] is ';' or ',')
        {
            position++;
        }

        SkipAsciiWhitespace(input, ref position);
        if (position >= input.Length)
        {
            return true;
        }

        int urlStart = position;
        if (input.Length - position >= 3 &&
            string.Compare(input, position, "url", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
        {
            int afterUrl = position + 3;
            SkipAsciiWhitespace(input, ref afterUrl);
            if (afterUrl < input.Length && input[afterUrl] == '=')
            {
                position = afterUrl + 1;
                SkipAsciiWhitespace(input, ref position);
            }
            else
            {
                position = urlStart;
            }
        }

        char quote = '\0';
        if (position < input.Length && input[position] is '\'' or '"')
        {
            quote = input[position];
            position++;
        }

        var rest = input.Substring(position);
        if (quote != '\0')
        {
            int end = rest.IndexOf(quote);
            if (end >= 0)
            {
                rest = rest.Substring(0, end);
            }
        }

        url = rest.Trim(' ', '\t', '\n', '\f', '\r');
        return true;
    }

    private static void SkipAsciiWhitespace(string input, ref int position)
    {
        while (position < input.Length && IsAsciiWhitespace(input[position]))
        {
            position++;
        }
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';
}
