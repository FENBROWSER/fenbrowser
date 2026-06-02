// ECMA-262 §22.2 — Match result type for the native regex VM.
// Replaces System.Text.RegularExpressions.Match from the .NET proxy era.

namespace FenBrowser.Js.Regex;

public sealed class RegexMatchResult
{
    /// <summary>Whether the match succeeded.</summary>
    public bool Success { get; }

    /// <summary>Start index (in chars, not code points) of the match in the input.</summary>
    public int Index { get; }

    /// <summary>Length of the full match in chars.</summary>
    public int Length { get; }

    /// <summary>The input string that was matched against.</summary>
    public string Input { get; }

    /// <summary>
    /// Capture groups. Flat array of (start, end) char-offset pairs.
    /// Group 0 = full match. Group N = slots 2*N (start) and 2*N+1 (end).
    /// -1 means "did not participate" (undefined).
    /// </summary>
    public int[] Captures { get; }

    /// <summary>Number of capturing groups (including group 0).</summary>
    public int GroupCount => Captures.Length / 2;

    /// <summary>Named group mapping (name → group number), or null.</summary>
    public IReadOnlyDictionary<string, int>? NamedGroups { get; }

    /// <summary>Match index information for the /d flag (indices array).</summary>
    public RegexMatchResult? Indices { get; internal set; }

    public RegexMatchResult(
        bool success,
        int index,
        int length,
        string input,
        int[] captures,
        IReadOnlyDictionary<string, int>? namedGroups = null)
    {
        Success = success;
        Index = index;
        Length = length;
        Input = input;
        Captures = captures;
        NamedGroups = namedGroups;
    }

    /// <summary>Empty (failed) match result.</summary>
    public static RegexMatchResult Empty(string input)
    {
        return new RegexMatchResult(false, -1, 0, input, new[] { -1, -1 });
    }

    /// <summary>Get the matched text for a capture group.</summary>
    public string? GetGroup(int groupNumber)
    {
        if (groupNumber < 0 || groupNumber >= GroupCount)
            return null;
        var start = Captures[groupNumber * 2];
        var end = Captures[groupNumber * 2 + 1];
        if (start < 0 || end < 0) return null;
        return Input.Substring(start, end - start);
    }

    /// <summary>Get the matched text for the full match.</summary>
    public string? GetMatch()
    {
        if (!Success) return null;
        return Input.Substring(Index, Length);
    }
}
