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

    /// <summary>Fails unless the receiver routes writes through itself.</summary>
    GuardNotProxy,

    /// <summary>
    /// Fails on an array. Emitted only for the key "length", whose write on an
    /// array may delete elements and so can never be a slot store.
    /// </summary>
    GuardNotArray,

    /// <summary>
    /// Yields the writable own data slot to store into, and terminates. The
    /// write itself belongs to the caller: barriers and the heap are not this
    /// layer's concern.
    /// </summary>
    StoreSlotResult,

    /// <summary>
    /// Fails unless the receiver is an array still keeping its elements in the
    /// dense vector - which is the only state in which its length is the
    /// vector's count rather than a stored property.
    /// </summary>
    GuardDenseArray,

    /// <summary>
    /// Yields a dense array's length, and terminates. It is not a slot in any
    /// shape - the array synthesises it from the vector - so no shape guard can
    /// describe it and, before this, no site could cache it. On a real page
    /// `length` is the most-read property there is: every loop bound reads one.
    /// </summary>
    LoadArrayLengthResult,
}

internal enum CacheRunResult : byte
{
    Hit,

    /// <summary>A guard rejected this receiver; try the next program.</summary>
    Miss,

    /// <summary>Guards passed but the slot is no longer a plain data property.</summary>
    Stale,
}
