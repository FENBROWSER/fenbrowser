using System.Globalization;

namespace FenBrowser.Media.Text;

/// <summary>WebVTT §4.3 region: a rectangle cues can be assigned to.</summary>
public sealed class VttRegion
{
    public string Id { get; set; } = "";

    /// <summary>Percentage of the video width.</summary>
    public double Width { get; set; } = 100;

    public ulong Lines { get; set; } = 3;

    public double RegionAnchorX { get; set; }

    public double RegionAnchorY { get; set; } = 100;

    public double ViewportAnchorX { get; set; }

    public double ViewportAnchorY { get; set; } = 100;

    /// <summary>"" or "up".</summary>
    public string Scroll { get; set; } = "";
}

public enum VttVertical
{
    Horizontal,
    RightToLeft,
    LeftToRight,
}

public enum VttLineAlign
{
    Start,
    Center,
    End,
}

public enum VttPositionAlign
{
    Auto,
    LineLeft,
    Center,
    LineRight,
}

public enum VttAlign
{
    Start,
    Center,
    End,
    Left,
    Right,
}

/// <summary>WebVTT §4.1 cue: identifier, timings, settings and the raw cue text.</summary>
public sealed class VttCue
{
    public string Id { get; set; } = "";

    public MediaTime Start { get; set; }

    public MediaTime End { get; set; }

    public bool PauseOnExit { get; set; }

    public VttRegion? Region { get; set; }

    public VttVertical Vertical { get; set; }

    public bool SnapToLines { get; set; } = true;

    /// <summary>The line, or null for "auto".</summary>
    public double? Line { get; set; }

    public VttLineAlign LineAlign { get; set; }

    /// <summary>The position percentage, or null for "auto".</summary>
    public double? Position { get; set; }

    public VttPositionAlign PositionAlign { get; set; }

    public double Size { get; set; } = 100;

    public VttAlign Align { get; set; } = VttAlign.Center;

    public string Text { get; set; } = "";
}

/// <summary>What a WebVTT file holds after parsing (§6.1): regions, cues and stylesheet text blocks.</summary>
public sealed class WebVttFile
{
    public List<VttRegion> Regions { get; } = [];

    public List<VttCue> Cues { get; } = [];

    /// <summary>STYLE block bodies, verbatim; the CSS engine parses them for <c>::cue</c> selectors.</summary>
    public List<string> Stylesheets { get; } = [];
}

/// <summary>
/// The WebVTT file parser (W3C WebVTT §6.1 "WebVTT parser algorithm", §6.2 cue timings and
/// settings, §6.3 region settings) as a pure function over the whole text. Bad lines and
/// bad settings are skipped exactly where the specification skips them, so a malformed
/// file yields the cues the other engines yield.
/// </summary>
public static class WebVttParser
{
    /// <summary>Parses a whole file. Returns null when the file does not start with a WebVTT signature.</summary>
    public static WebVttFile? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Newline and NULL normalisation (§6.1 steps 1 and 2, via the "decoding" rules).
        string input = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\0', '\uFFFD');
        int position = 0;
        if (input.Length > 0 && input[0] == '\uFEFF')
            position = 1;

        // The signature: "WEBVTT" followed by a space, a tab, a line feed or the end.
        if (input.Length - position < 6 || string.CompareOrdinal(input, position, "WEBVTT", 0, 6) != 0)
            return null;
        if (input.Length - position > 6 && input[position + 6] is not (' ' or '\t' or '\n'))
            return null;

        var file = new WebVttFile();
        position = SkipToLineEnd(input, position);
        if (position >= input.Length)
            return file;
        position++; // the LF after the signature line

        // The header block: everything up to the first blank line is discarded (§6.1 step 10).
        bool seenCue = false;
        CollectBlock(input, ref position, file, inHeader: true, ref seenCue);

        while (position < input.Length)
            CollectBlock(input, ref position, file, inHeader: false, ref seenCue);

        return file;
    }

    private static int SkipToLineEnd(string input, int position)
    {
        int end = input.IndexOf('\n', position);
        return end < 0 ? input.Length : end;
    }

    /// <summary>
    /// §6.1 "collect a WebVTT block". <paramref name="seenCue"/> is the parser's flag: once a cue
    /// has been seen, STYLE and REGION blocks are no longer collected (they must precede the cues).
    /// </summary>
    private static void CollectBlock(string input, ref int position, WebVttFile file, bool inHeader, ref bool seenCue)
    {
        int lineCount = 0;
        int previousPosition = position;
        var buffer = new System.Text.StringBuilder();
        bool seenEof = false;
        bool seenArrow = false;
        VttCue? cue = null;
        bool stylesheet = false;
        VttRegion? region = null;

        while (true)
        {
            int lineEnd = SkipToLineEnd(input, position);
            string line = input[position..lineEnd];
            lineCount++;
            if (lineEnd >= input.Length)
            {
                seenEof = true;
                position = lineEnd;
            }
            else
            {
                position = lineEnd + 1;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                if (!inHeader && (lineCount == 1 || (lineCount == 2 && !seenArrow)))
                {
                    seenArrow = true;
                    previousPosition = position;
                    cue = new VttCue { Id = buffer.ToString() };
                    if (!TryParseTimingsAndSettings(line, file.Regions, cue))
                        cue = null;
                    buffer.Clear();
                    // A line that only looks like timings ("-->" alone) is not a cue, so it
                    // does not end the header's REGION and STYLE blocks (WPT regions-edge-case).
                    if (cue is not null)
                        seenCue = true;
                }
                else
                {
                    position = previousPosition;
                    break;
                }
            }
            else if (line.Length == 0)
            {
                break;
            }
            else
            {
                if (!inHeader && lineCount == 2)
                {
                    string head = buffer.ToString();
                    if (!seenCue && IsBlockKeyword(head, "STYLE"))
                    {
                        stylesheet = true;
                        buffer.Clear();
                    }
                    else if (!seenCue && IsBlockKeyword(head, "REGION"))
                    {
                        region = new VttRegion();
                        buffer.Clear();
                    }
                }

                if (buffer.Length > 0)
                    buffer.Append('\n');
                buffer.Append(line);
                previousPosition = position;
            }

            if (seenEof)
                break;
        }

        if (cue is not null)
        {
            cue.Text = buffer.ToString();
            file.Cues.Add(cue);
        }
        else if (stylesheet)
        {
            file.Stylesheets.Add(buffer.ToString());
        }
        else if (region is not null)
        {
            ParseRegionSettings(buffer.ToString(), region);
            file.Regions.Add(region);
        }
    }

    /// <summary>The first line of a block is "STYLE" or "REGION" followed only by ASCII whitespace.</summary>
    private static bool IsBlockKeyword(string head, string keyword)
    {
        if (!head.StartsWith(keyword, StringComparison.Ordinal))
            return false;
        for (int i = keyword.Length; i < head.Length; i++)
        {
            if (!IsAsciiWhitespace(head[i]))
                return false;
        }

        return true;
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    /// <summary>§6.2 "collect WebVTT cue timings and settings".</summary>
    private static bool TryParseTimingsAndSettings(string line, List<VttRegion> regions, VttCue cue)
    {
        int position = 0;
        SkipWhitespace(line, ref position);
        if (!TryParseTimestamp(line, ref position, out double start))
            return false;
        SkipWhitespace(line, ref position);
        if (position + 3 > line.Length || string.CompareOrdinal(line, position, "-->", 0, 3) != 0)
            return false;
        position += 3;
        SkipWhitespace(line, ref position);
        if (!TryParseTimestamp(line, ref position, out double end))
            return false;

        cue.Start = MediaTime.FromSeconds(start);
        cue.End = MediaTime.FromSeconds(end);
        ParseSettings(line[position..], regions, cue);
        return true;
    }

    private static void SkipWhitespace(string input, ref int position)
    {
        while (position < input.Length && IsAsciiWhitespace(input[position]))
            position++;
    }

    /// <summary>§6.2.1 "collect a WebVTT timestamp": (hh:)mm:ss.ttt with two-digit minutes and seconds, three-digit milliseconds.</summary>
    public static bool TryParseTimestamp(string input, ref int position, out double seconds)
    {
        seconds = 0;
        bool hours = false;
        int start = position;
        if (position >= input.Length || !char.IsAsciiDigit(input[position]))
            return false;
        string first = CollectDigits(input, ref position);
        if (first.Length != 2)
            hours = true;
        if (!TryDigits(first, out long value1))
            return false;
        if (position >= input.Length || input[position] != ':')
            return false;
        position++;
        string second = CollectDigits(input, ref position);
        if (second.Length != 2 || !TryDigits(second, out long value2))
            return false;

        long value3;
        if (hours || (position < input.Length && input[position] == ':'))
        {
            if (position >= input.Length || input[position] != ':')
                return false;
            position++;
            string third = CollectDigits(input, ref position);
            if (third.Length != 2 || !TryDigits(third, out value3))
                return false;
        }
        else
        {
            value3 = value2;
            value2 = value1;
            value1 = 0;
        }

        if (position >= input.Length || input[position] != '.')
            return false;
        position++;
        string fraction = CollectDigits(input, ref position);
        if (fraction.Length != 3 || !TryDigits(fraction, out long value4))
            return false;
        if (value2 > 59 || value3 > 59)
            return false;

        seconds = value1 * 3600.0 + value2 * 60.0 + value3 + value4 / 1000.0;
        _ = start;
        return true;
    }

    private static string CollectDigits(string input, ref int position)
    {
        int start = position;
        while (position < input.Length && char.IsAsciiDigit(input[position]))
            position++;
        return input[start..position];
    }

    private static bool TryDigits(string digits, out long value)
    {
        value = 0;
        foreach (char c in digits)
        {
            if (value > (long.MaxValue - 9) / 10)
            {
                value = long.MaxValue;
                continue;
            }

            value = value * 10 + (c - '0');
        }

        return digits.Length > 0;
    }

    /// <summary>§6.2 step 9: the settings after the timings, one "name:value" per whitespace-separated item.</summary>
    private static void ParseSettings(string input, List<VttRegion> regions, VttCue cue)
    {
        foreach (string setting in input.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = setting.IndexOf(':');
            if (colon <= 0 || colon == setting.Length - 1)
                continue;
            string name = setting[..colon];
            string value = setting[(colon + 1)..];
            switch (name)
            {
                case "region":
                    cue.Region = regions.LastOrDefault(r => r.Id == value);
                    break;
                case "vertical":
                    if (value == "rl") cue.Vertical = VttVertical.RightToLeft;
                    else if (value == "lr") cue.Vertical = VttVertical.LeftToRight;
                    break;
                case "line":
                    ParseLine(value, cue);
                    break;
                case "position":
                    ParsePosition(value, cue);
                    break;
                case "size":
                    if (TryParsePercentage(value, out double size))
                        cue.Size = size;
                    break;
                case "align":
                    cue.Align = value switch
                    {
                        "start" => VttAlign.Start,
                        "center" => VttAlign.Center,
                        "end" => VttAlign.End,
                        "left" => VttAlign.Left,
                        "right" => VttAlign.Right,
                        _ => cue.Align,
                    };
                    break;
            }
        }
    }

    private static void ParseLine(string value, VttCue cue)
    {
        string linepos = value;
        string? linealign = null;
        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            linepos = value[..comma];
            linealign = value[(comma + 1)..];
        }

        if (!linepos.Any(char.IsAsciiDigit))
            return;

        double number;
        bool snap;
        if (linepos.Contains('%'))
        {
            if (!TryParsePercentage(linepos, out number))
                return;
            snap = false;
        }
        else
        {
            if (linepos.Any(c => !(char.IsAsciiDigit(c) || c == '-' || c == '.')))
                return;
            if (linepos.IndexOf('-', 1) >= 0)
                return;
            int dot = linepos.IndexOf('.');
            if (dot >= 0 && (linepos.IndexOf('.', dot + 1) >= 0 || dot == 0 || dot == linepos.Length - 1 || (dot == 1 && linepos[0] == '-')))
                return;
            if (!double.TryParse(linepos, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) || !double.IsFinite(number))
                return; // past the double range the value is not a number the cue can hold
            snap = true;
        }

        VttLineAlign alignment = cue.LineAlign;
        if (linealign is not null)
        {
            switch (linealign)
            {
                case "start": alignment = VttLineAlign.Start; break;
                case "center": alignment = VttLineAlign.Center; break;
                case "end": alignment = VttLineAlign.End; break;
                default: return;
            }
        }

        cue.Line = number == 0 ? 0 : number; // "-0" reads back as 0, as the other engines report it
        cue.SnapToLines = snap;
        cue.LineAlign = alignment;
    }

    private static void ParsePosition(string value, VttCue cue)
    {
        string colpos = value;
        string? colalign = null;
        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            colpos = value[..comma];
            colalign = value[(comma + 1)..];
        }

        if (!TryParsePercentage(colpos, out double number))
            return;

        VttPositionAlign alignment = cue.PositionAlign;
        if (colalign is not null)
        {
            switch (colalign)
            {
                case "line-left": alignment = VttPositionAlign.LineLeft; break;
                case "center": alignment = VttPositionAlign.Center; break;
                case "line-right": alignment = VttPositionAlign.LineRight; break;
                default: return;
            }
        }

        cue.Position = number;
        cue.PositionAlign = alignment;
    }

    /// <summary>§6.2 "parse a percentage string": digits, an optional fraction, then "%", within 0..100.</summary>
    public static bool TryParsePercentage(string input, out double value)
    {
        value = 0;
        if (input.Length < 2 || input[^1] != '%')
            return false;
        string number = input[..^1];
        if (number.Length == 0 || !char.IsAsciiDigit(number[0]))
            return false;
        int dot = number.IndexOf('.');
        if (dot >= 0)
        {
            if (dot == number.Length - 1 || number.IndexOf('.', dot + 1) >= 0)
                return false;
        }

        if (number.Any(c => !(char.IsAsciiDigit(c) || c == '.')))
            return false;
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        return value is >= 0 and <= 100;
    }

    /// <summary>§6.3 "collect WebVTT region settings".</summary>
    private static void ParseRegionSettings(string input, VttRegion region)
    {
        foreach (string setting in input.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = setting.IndexOf(':');
            if (colon <= 0 || colon == setting.Length - 1)
                continue;
            string name = setting[..colon];
            string value = setting[(colon + 1)..];
            switch (name)
            {
                case "id":
                    if (!value.Contains("-->", StringComparison.Ordinal))
                        region.Id = value;
                    break;
                case "width":
                    if (TryParsePercentage(value, out double width))
                        region.Width = width;
                    break;
                case "lines":
                    // §6.3: digits only, held as an unsigned long (WebVTT §5.1 VTTRegion.lines).
                    if (value.Length > 0 && value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong lines))
                        region.Lines = lines > uint.MaxValue ? uint.MaxValue : lines;
                    break;
                case "regionanchor":
                    if (TryParseAnchor(value, out double rx, out double ry))
                    {
                        region.RegionAnchorX = rx;
                        region.RegionAnchorY = ry;
                    }
                    break;
                case "viewportanchor":
                    if (TryParseAnchor(value, out double vx, out double vy))
                    {
                        region.ViewportAnchorX = vx;
                        region.ViewportAnchorY = vy;
                    }
                    break;
                case "scroll":
                    if (value == "up")
                        region.Scroll = "up";
                    break;
            }
        }
    }

    private static bool TryParseAnchor(string value, out double x, out double y)
    {
        x = 0;
        y = 0;
        int comma = value.IndexOf(',');
        if (comma < 0)
            return false;
        return TryParsePercentage(value[..comma], out x) && TryParsePercentage(value[(comma + 1)..], out y);
    }
}
