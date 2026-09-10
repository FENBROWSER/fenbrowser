namespace FenBrowser.Js.Diagnostics;

/// <summary>Why an array stopped keeping its elements in the dense vector.</summary>
public enum ArrayMaterialiseReason
{
    /// <summary>A write past the end, which would leave a hole behind it.</summary>
    WritePastEnd,

    /// <summary>A length the vector cannot represent - almost always one grown past the count.</summary>
    LengthUnrepresentable,

    /// <summary>An element deleted from anywhere but the end.</summary>
    ElementDeleted,

    /// <summary>An accessor, or a frozen or hidden element - not a plain data property.</summary>
    UnrepresentableDescriptor,

    /// <summary>A write to a non-extensible array, which cannot grow the vector.</summary>
    NotExtensible,
}

/// <summary>
/// How many arrays gave up the dense vector, and for which reason.
/// </summary>
/// <remarks>
/// An array that is no longer dense answers every indexed read through a
/// string-keyed property lookup, with the key built from the index first. On
/// reCAPTCHA's bundle that was 1,991,043 reads - two thirds of every element
/// read that missed a cache, and the largest single item left in either miss
/// table. A count of them says the cost is there; only the reason says whether
/// it is fixable, so it is recorded next to the reads it explains.
/// </remarks>
public static class ArrayShapeStats
{
    /// <summary>
    /// Behind the same switch as the rest of the coverage report, and read here
    /// rather than passed in so the object model keeps knowing nothing about the
    /// interpreter that reports it.
    /// </summary>
    public static readonly bool Enabled = string.Equals(
        System.Environment.GetEnvironmentVariable("FEN_JS_INTERP2_LOG"),
        "1",
        System.StringComparison.Ordinal);

    private static readonly long[] Counts = new long[System.Enum.GetValues<ArrayMaterialiseReason>().Length];

    internal static void Record(ArrayMaterialiseReason reason)
    {
        if (Enabled) Counts[(int)reason]++;
    }

    public static void Reset() => System.Array.Clear(Counts);

    /// <summary>The ranked reasons, or an empty string when no array gave up.</summary>
    public static string Describe()
    {
        var total = 0L;
        foreach (var count in Counts) total += count;
        if (total == 0) return string.Empty;

        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Counts.Length; i++)
        {
            if (Counts[i] == 0) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append((ArrayMaterialiseReason)i).Append('=').Append(Counts[i]);
        }

        return text.ToString();
    }
}
