namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// Where one free identifier resolved the last time this function looked, so
/// the next look does not repeat the walk.
/// </summary>
/// <remarks>
/// <para>
/// Reading a variable a function does not declare is the most expensive
/// ordinary operation this engine performs: about 107ns against 27ns for a
/// property read on an object already in a register. The cost is not the walk
/// itself - on a real page the chain averages 1.55 links - it is that each link
/// is a string-keyed dictionary lookup, and the name has to be recovered from
/// the slot index before any of it can start.
/// </para>
/// <para>
/// Lexical scoping is static, so the answer does not move: a given identifier in
/// a given function resolves the same number of links out, in the same kind of
/// record, on every call. That makes it cacheable per (function, slot), which is
/// what this is. Two shapes of answer are worth caching:
/// </para>
/// <list type="bullet">
/// <item>a binding in a record that numbers its variables by slot, where the
/// cached slot index turns the whole thing into an array read; and</item>
/// <item>a property of the global object, where the shape-guarded load cache
/// the engine already uses for <c>o.x</c> does the work.</item>
/// </list>
/// <para>
/// Neither is trusted. The slot form re-checks that the record found at the
/// cached depth still numbers slots for the same function, and the global form
/// re-checks the global's lexical version and then runs a shape guard - so a
/// stale entry produces a miss and a fresh walk, never a wrong value. The
/// bodies that could invalidate the *shape* of a chain - <c>with</c>, direct
/// <c>eval</c>, a deleted binding - are refused by the layout before any of this
/// is reached.
/// </para>
/// <para>
/// It is an immutable class rather than a mutable struct in an array because
/// several script threads can execute the same function: publishing a whole
/// entry with one reference write cannot be read half-updated, where four
/// separate field writes could.
/// </para>
/// </remarks>
public sealed class FreeSlotSite
{
    private FreeSlotSite(int hops, object? slotOwner, int targetSlot, int lexicalVersion)
    {
        Hops = hops;
        SlotOwner = slotOwner;
        TargetSlot = targetSlot;
        LexicalVersion = lexicalVersion;
    }

    /// <summary>How many <c>OuterEnv</c> links out the binding was found.</summary>
    public int Hops { get; }

    /// <summary>
    /// The function whose slot numbering the terminal record holds, or null when
    /// the answer was a property of the global object.
    /// </summary>
    public object? SlotOwner { get; }

    /// <summary>The index the terminal record numbers this name at.</summary>
    public int TargetSlot { get; }

    /// <summary>
    /// The global record's lexical version when the answer was cached. A
    /// top-level <c>let</c> appearing since then would shadow the property this
    /// site resolved to, and bumps the version so the guard fails.
    /// </summary>
    public int LexicalVersion { get; }

    public static FreeSlotSite AtSlot(int hops, object slotOwner, int targetSlot)
        => new(hops, slotOwner, targetSlot, lexicalVersion: 0);

    public static FreeSlotSite OnGlobalObject(int hops, int lexicalVersion)
        => new(hops, slotOwner: null, targetSlot: -1, lexicalVersion);
}
