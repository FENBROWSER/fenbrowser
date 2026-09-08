namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>
/// Operations a cache program is built from. Every program is a sequence of
/// guards ending in a load, and every guard fails closed.
/// </summary>
internal enum CacheOp : byte
{
    /// <summary>Fails unless the receiver routes property access through itself.</summary>
    GuardNotExotic,

    /// <summary>Fails unless the receiver's shape is the one recorded at attach time.</summary>
    GuardShape,

    /// <summary>Fails unless the runtime key matches the one recorded at attach time.</summary>
    GuardKey,

    /// <summary>Reads the own data property at a known slot and terminates.</summary>
    LoadSlotResult,
}

internal enum CacheRunResult : byte
{
    Hit,

    /// <summary>A guard rejected this receiver; try the next program.</summary>
    Miss,

    /// <summary>Guards passed but the slot is no longer a plain data property.</summary>
    Stale,
}
