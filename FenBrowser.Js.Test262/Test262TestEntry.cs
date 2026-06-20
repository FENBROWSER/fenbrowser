namespace FenBrowser.Js.Test262;

/// <summary>
/// Mutable test entry that replaces the 26 anonymous-type sites in
/// Test262Runner. This exists so we can set <see cref="DurationMs"/> AFTER the
/// test runs (anonymous types are immutable), and eliminates massive code
/// duplication across the runner methods.
/// </summary>
public sealed class TestEntry
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "";
    public long DurationMs { get; set; }
    public IReadOnlyList<string> Features { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Flags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Includes { get; set; } = Array.Empty<string>();
    public Test262NegativeMetadata? Negative { get; set; }
    public string? Esid { get; set; }
    public string? Description { get; set; }
    public string? Info { get; set; }
    public string? Locale { get; set; }
    public string? Category { get; set; }
    public string? Message { get; set; }
    public string? Details { get; set; }

    /// <summary>
    /// Populate fields from a <see cref="Test262FrontmatterMetadata"/> value.
    /// Call this after setting Path / Status / DurationMs / Category / Message.
    /// </summary>
    public void ApplyFrontmatter(Test262FrontmatterMetadata fm)
    {
        Features = fm.Features;
        Flags = fm.Flags;
        Includes = fm.Includes;
        Negative = fm.Negative;
        Esid = fm.Esid;
        Description = fm.Description;
        Info = fm.Info;
        Locale = fm.Locale;
    }

    /// <summary>Create a fresh instance pre-filled from frontmatter.</summary>
    public static TestEntry FromFrontmatter(string relativePath, Test262FrontmatterMetadata fm)
    {
        return new TestEntry
        {
            Path = relativePath,
            Features = fm.Features,
            Flags = fm.Flags,
            Includes = fm.Includes,
            Negative = fm.Negative,
            Esid = fm.Esid,
            Description = fm.Description,
            Info = fm.Info,
            Locale = fm.Locale,
        };
    }
}
