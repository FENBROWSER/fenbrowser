using System.Text;

namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// Coverage and shape of what the register-window loop actually ran, recorded
/// only when <see cref="Interp2Options.Log"/> is set.
/// </summary>
/// <remarks>
/// Two numbers decide whether this loop is worth anything on a real page, and
/// neither is visible from a benchmark: how much of the code is eligible, and
/// how many of the calls it makes it can enter without going back through the
/// old one. Both are counted here, alongside the reason every declined function
/// was declined - a ranked bailout table is the work queue for what to
/// implement next.
///
/// Every call site is behind the one flag, and the counters are plain statics
/// rather than interlocked: an approximate count taken off the hot path is worth
/// more than an exact one that changes what it measures. The flag is off in
/// every configuration that has not asked for it.
/// </remarks>
/// <summary>Where a named property actually lived, when its site could not answer.</summary>
public enum PropertyMissKind
{
    /// <summary>A string primitive: `s.length`, `s.charCodeAt`, and everything else on one.</summary>
    StringReceiver,

    /// <summary>Some other primitive - a number, a boolean, a symbol.</summary>
    OtherPrimitiveReceiver,

    /// <summary>A host object: the DOM, and anything else the embedder owns.</summary>
    HostReceiver,

    /// <summary>A proxy or a module namespace, which routes access through itself.</summary>
    ExoticReceiver,

    /// <summary>
    /// An own data property in a shape slot - the exact thing a site caches, so
    /// a miss here is the site failing to hold enough shapes.
    /// </summary>
    OwnDataSlot,

    /// <summary>An own accessor: reading it is a call into user code, never a slot.</summary>
    OwnAccessor,

    /// <summary>An own property the shape does not describe - no slot to cache.</summary>
    OwnNotInShape,

    /// <summary>Found on the prototype chain, which the cache refuses to attach for.</summary>
    OnPrototype,

    /// <summary>Not found anywhere; the read answers undefined.</summary>
    Absent,
}

/// <summary>
/// What an <c>o[k]</c> read was, when neither the dense-element path nor the
/// string-key site could answer it.
/// </summary>
/// <remarks>
/// Named misses got a table before this one and it paid for itself twice - a
/// dense array's length and a prototype method were both invisible until the
/// names said so. Element reads had only a count: 3.0 million of them against
/// 10.4 million hits on one page, with nothing to say what a cache would have
/// to describe to answer them. These are the receiver and key together, because
/// that pair is exactly what a new cache form would have to guard.
/// </remarks>
public enum ElementMissKind
{
    /// <summary>A dense array asked for an index it does not hold.</summary>
    DenseArrayOutOfRange,

    /// <summary>An array that has stopped keeping its elements in the vector.</summary>
    SparseArrayIndex,

    /// <summary>A typed array, whose elements come from the buffer.</summary>
    TypedArrayIndex,

    /// <summary>A character of a string primitive: `s[i]`, which allocates one.</summary>
    StringIndex,

    /// <summary>A name off a string primitive: `s[k]` where k is not an index.</summary>
    StringName,

    /// <summary>An ordinary object read with an integer key - a dictionary keyed by number.</summary>
    ObjectIndexKey,

    /// <summary>An ordinary object read with a name, which the named cache should have held.</summary>
    ObjectNameKey,

    /// <summary>A host object: the DOM, and anything else the embedder owns.</summary>
    HostReceiver,

    /// <summary>A proxy or a module namespace, which routes access through itself.</summary>
    ExoticReceiver,

    /// <summary>Some other primitive - a number, a boolean.</summary>
    OtherPrimitiveReceiver,

    /// <summary>A key that is neither a string nor a number, so ToPropertyKey has work to do.</summary>
    OtherKey,
}

public static class Interp2Stats
{
    private static readonly long[] ElementMissKinds = new long[Enum.GetValues<ElementMissKind>().Length];

    // Where an element miss happened, not just what it was. A site that has
    // given up and a site that never saw this receiver are different problems
    // and a miss count cannot tell them apart.
    private static long _elementMissAtMegamorphicSite;
    private static long _elementMissAtFullSite;
    private static long _elementMissAtOpenSite;
    private static readonly Dictionary<string, long> ElementNameKeys = new(StringComparer.Ordinal);

    // What the busiest o[k] site actually saw. A site holds four programs and a
    // program guards a shape and a key together, so it can be exhausted by one
    // receiver read under many names or by many receivers read under one - and
    // those want different fixes. Bounded, and only while the switch is on.
    private sealed class SiteProfile
    {
        internal long Misses;
        internal readonly HashSet<string> Keys = new(StringComparer.Ordinal);
        internal readonly HashSet<object> Shapes = new(ReferenceEqualityComparer.Instance);
    }

    private static readonly Dictionary<(int Function, int Offset), SiteProfile> ElementSiteProfiles = new();


    private static readonly long[] BailoutCounts = new long[Enum.GetValues<Interp2Bailout>().Length];

    private static readonly Dictionary<FenBrowser.Js.Bytecode.OpCode, long> UnsupportedOpCodes = new();

    private static long _eligibleFunctions;
    private static long _declinedFunctions;
    private static long _framesEntered;
    private static long _callsInLoop;
    private static long _callsDelegated;
    private static long _callsDelegatedToDeclinedBody;
    private static long _propertyReadsCached;
    private static long _propertyReadsMissed;
    private static long _elementReadsCached;
    private static long _elementReadsMissed;
    private static readonly long[] PropertyMissKinds = new long[Enum.GetValues<PropertyMissKind>().Length];
    private static long _maxDepth;
    private static long _maxStackSlots;

    public static long EligibleFunctions => _eligibleFunctions;
    public static long DeclinedFunctions => _declinedFunctions;
    public static long FramesEntered => _framesEntered;

    /// <summary>Calls entered as a new window on the same loop - the point of all this.</summary>
    public static long CallsInLoop => _callsInLoop;

    /// <summary>Calls handed back to the old loop: natives, closures, everything declined.</summary>
    public static long CallsDelegated => _callsDelegated;

    internal static void RecordLayout(FrameLayout layout)
    {
        if (layout.Eligible)
        {
            _eligibleFunctions++;
            return;
        }

        _declinedFunctions++;
        BailoutCounts[(int)layout.Bailout]++;
        if (layout.BailoutOpCode is { } op)
        {
            UnsupportedOpCodes[op] = UnsupportedOpCodes.TryGetValue(op, out var seen) ? seen + 1 : 1;
        }
    }

    internal static void RecordFrameEntered(int depth, int stackSlots)
    {
        _framesEntered++;
        if (depth > _maxDepth) _maxDepth = depth;
        if (stackSlots > _maxStackSlots) _maxStackSlots = stackSlots;
    }

    internal static void RecordCallInLoop() => _callsInLoop++;

    /// <summary>
    /// Whether a property read was answered by its site's shape cache. A read
    /// that misses walks the object and its prototypes, which is roughly ten
    /// times the work - so the hit rate, not the count, says what a page's
    /// property access costs.
    /// </summary>
    internal static void RecordPropertyRead(bool cached)
    {
        if (cached) _propertyReadsCached++; else _propertyReadsMissed++;
    }

    /// <summary>Why a named property read could not be answered from its site.</summary>
    internal static void RecordPropertyMiss(PropertyMissKind kind) => PropertyMissKinds[(int)kind]++;

    private static readonly Dictionary<string, long> UncacheableKeys = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> StringReceiverKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Which names are behind the misses a cache cannot describe. A count of
    /// misses says there is something to fix; only the names say what.
    /// </summary>
    internal static void RecordUncacheableKey(string key)
    {
        if (UncacheableKeys.Count < 4096)
        {
            UncacheableKeys[key] = UncacheableKeys.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }
    }

    /// <summary>Which names a page reads off a string primitive.</summary>
    internal static void RecordStringReceiverKey(string key)
    {
        if (StringReceiverKeys.Count < 4096)
        {
            StringReceiverKeys[key] = StringReceiverKeys.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }
    }

    internal static void RecordElementRead(bool cached)
    {
        if (cached) _elementReadsCached++; else _elementReadsMissed++;
    }

    /// <summary>What an element read was, when no site could answer it.</summary>
    internal static void RecordElementMiss(ElementMissKind kind) => ElementMissKinds[(int)kind]++;

    /// <summary>
    /// How full the site was when it missed. <paramref name="programCount"/> is
    /// -1 for a site that does not exist yet.
    /// </summary>
    internal static void RecordElementMissSite(int programCount, bool megamorphic)
    {
        if (megamorphic) _elementMissAtMegamorphicSite++;
        else if (programCount >= FenBrowser.Js.Jit.CacheIR.CacheIRSite.Capacity) _elementMissAtFullSite++;
        else _elementMissAtOpenSite++;
    }

    /// <summary>
    /// What one <c>o[k]</c> site saw when it missed: which key, and which
    /// receiver layout. Together they say whether a site was exhausted by the
    /// names it reads or by the objects it reads them from.
    /// </summary>
    internal static void RecordElementSite(int functionId, int offset, string key, object? shape)
    {
        if (!ElementSiteProfiles.TryGetValue((functionId, offset), out var profile))
        {
            if (ElementSiteProfiles.Count >= 4096) return;
            profile = new SiteProfile();
            ElementSiteProfiles[(functionId, offset)] = profile;
        }

        profile.Misses++;
        if (profile.Keys.Count < 64) profile.Keys.Add(key);
        if (shape is not null && profile.Shapes.Count < 64) profile.Shapes.Add(shape);
    }

    /// <summary>Which names a page reads through <c>o[k]</c> rather than <c>o.name</c>.</summary>
    internal static void RecordElementNameKey(string key)
    {
        if (ElementNameKeys.Count < 4096)
        {
            ElementNameKeys[key] = ElementNameKeys.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }
    }

    internal static void RecordCallDelegated(bool calleeIsJavaScript)
    {
        _callsDelegated++;
        if (calleeIsJavaScript) _callsDelegatedToDeclinedBody++;
    }

    public static void Reset()
    {
        Array.Clear(BailoutCounts);
        UnsupportedOpCodes.Clear();
        _eligibleFunctions = 0;
        _declinedFunctions = 0;
        _framesEntered = 0;
        _callsInLoop = 0;
        _callsDelegated = 0;
        _callsDelegatedToDeclinedBody = 0;
        _propertyReadsCached = 0;
        _propertyReadsMissed = 0;
        _elementReadsCached = 0;
        _elementReadsMissed = 0;
        Array.Clear(PropertyMissKinds);
        Array.Clear(ElementMissKinds);
        ElementNameKeys.Clear();
        ElementSiteProfiles.Clear();
        _elementMissAtMegamorphicSite = 0;
        _elementMissAtFullSite = 0;
        _elementMissAtOpenSite = 0;
        FenBrowser.Js.Diagnostics.ArrayShapeStats.Reset();
        UncacheableKeys.Clear();
        StringReceiverKeys.Clear();
        _maxDepth = 0;
        _maxStackSlots = 0;
    }

    /// <summary>A one-screen summary, ordered so the largest gap reads first.</summary>
    public static string Report()
    {
        var report = new StringBuilder();
        var functions = _eligibleFunctions + _declinedFunctions;
        var calls = _callsInLoop + _callsDelegated;
        report.Append("[interp2] functions=").Append(functions)
              .Append(" eligible=").Append(_eligibleFunctions)
              .Append(Percent(_eligibleFunctions, functions))
              .Append(" frames=").Append(_framesEntered)
              .Append(" maxDepth=").Append(_maxDepth)
              .Append(" maxStackSlots=").Append(_maxStackSlots)
              .AppendLine();
        report.Append("[interp2] calls=").Append(calls)
              .Append(" inLoop=").Append(_callsInLoop)
              .Append(Percent(_callsInLoop, calls))
              .Append(" delegated=").Append(_callsDelegated)
              .Append(" [toDeclinedBody=").Append(_callsDelegatedToDeclinedBody)
              .Append(" toNative=").Append(_callsDelegated - _callsDelegatedToDeclinedBody)
              .Append(']')
              .AppendLine();

        var propertyReads = _propertyReadsCached + _propertyReadsMissed;
        var elementReads = _elementReadsCached + _elementReadsMissed;
        if (propertyReads + elementReads > 0)
        {
            report.Append("[interp2] o.name reads=").Append(propertyReads)
                  .Append(" cached=").Append(_propertyReadsCached).Append(Percent(_propertyReadsCached, propertyReads))
                  .Append("  o[k] reads=").Append(elementReads)
                  .Append(" cached=").Append(_elementReadsCached).Append(Percent(_elementReadsCached, elementReads))
                  .AppendLine();
        }

        var missTotal = 0L;
        foreach (var count in PropertyMissKinds) missTotal += count;
        if (missTotal > 0)
        {
            report.Append("[interp2] o.name misses:");
            for (var i = 0; i < PropertyMissKinds.Length; i++)
            {
                if (PropertyMissKinds[i] == 0) continue;
                report.Append(' ').Append((PropertyMissKind)i).Append('=').Append(PropertyMissKinds[i])
                      .Append(Percent(PropertyMissKinds[i], missTotal));
            }

            report.AppendLine();

            if (UncacheableKeys.Count > 0)
            {
                report.Append("[interp2] not-in-shape names:");
                var top = UncacheableKeys.OrderByDescending(static pair => pair.Value).Take(12);
                foreach (var (name, count) in top)
                {
                    report.Append(' ').Append(name).Append('=').Append(count);
                }

                report.AppendLine();
            }

            if (StringReceiverKeys.Count > 0)
            {
                report.Append("[interp2] on-string names:");
                foreach (var (name, count) in StringReceiverKeys.OrderByDescending(static p => p.Value).Take(12))
                {
                    report.Append(' ').Append(name).Append('=').Append(count);
                }

                report.AppendLine();
            }
        }

        var elementMissTotal = 0L;
        foreach (var count in ElementMissKinds) elementMissTotal += count;
        if (elementMissTotal > 0)
        {
            report.Append("[interp2] o[k] misses:");
            for (var i = 0; i < ElementMissKinds.Length; i++)
            {
                if (ElementMissKinds[i] == 0) continue;
                report.Append(' ').Append((ElementMissKind)i).Append('=').Append(ElementMissKinds[i])
                      .Append(Percent(ElementMissKinds[i], elementMissTotal));
            }

            report.AppendLine();

            var sited = _elementMissAtMegamorphicSite + _elementMissAtFullSite + _elementMissAtOpenSite;
            if (sited > 0)
            {
                report.Append("[interp2] o[k] miss sites: megamorphic=").Append(_elementMissAtMegamorphicSite)
                      .Append(Percent(_elementMissAtMegamorphicSite, sited))
                      .Append(" full=").Append(_elementMissAtFullSite).Append(Percent(_elementMissAtFullSite, sited))
                      .Append(" open=").Append(_elementMissAtOpenSite).Append(Percent(_elementMissAtOpenSite, sited))
                      .AppendLine();
            }

            if (ElementNameKeys.Count > 0)
            {
                report.Append("[interp2] o[k] names (").Append(ElementNameKeys.Count).Append(" distinct):");
                foreach (var (name, count) in ElementNameKeys.OrderByDescending(static p => p.Value).Take(12))
                {
                    report.Append(' ').Append(name).Append('=').Append(count);
                }

                report.AppendLine();
            }

            if (ElementSiteProfiles.Count > 0)
            {
                report.Append("[interp2] widest o[k] sites (of ").Append(ElementSiteProfiles.Count).Append("):");
                foreach (var profile in ElementSiteProfiles.Values
                             .OrderByDescending(static p => p.Misses).Take(4))
                {
                    report.Append(" [misses=").Append(profile.Misses)
                          .Append(" keys=").Append(profile.Keys.Count)
                          .Append(" shapes=").Append(profile.Shapes.Count)
                          .Append(" first=").Append(profile.Keys.FirstOrDefault() ?? "?")
                          .Append(']');
                }

                report.AppendLine();
            }
        }

        var materialised = FenBrowser.Js.Diagnostics.ArrayShapeStats.Describe();
        if (materialised.Length > 0)
        {
            // Printed next to the element misses it explains: an array that gave
            // up its vector answers every later indexed read through a
            // string-keyed lookup, with the key built from the index first.
            report.Append("[interp2] arrays no longer dense: ").Append(materialised).AppendLine();
        }

        var ranked = new List<(Interp2Bailout Reason, long Count)>();
        for (var i = 1; i < BailoutCounts.Length; i++)
        {
            if (BailoutCounts[i] > 0) ranked.Add(((Interp2Bailout)i, BailoutCounts[i]));
        }

        ranked.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        if (ranked.Count > 0)
        {
            report.Append("[interp2] declined:");
            foreach (var (reason, count) in ranked)
            {
                report.Append(' ').Append(reason).Append('=').Append(count);
            }

            report.AppendLine();
        }

        if (UnsupportedOpCodes.Count > 0)
        {
            report.Append("[interp2] unimplemented:");
            foreach (var (op, count) in UnsupportedOpCodes.OrderByDescending(static pair => pair.Value))
            {
                report.Append(' ').Append(op).Append('=').Append(count);
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    private static string Percent(long part, long total)
        => total == 0 ? string.Empty : $"({part * 100.0 / total:F1}%)";
}
