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
    /// dense vector - which is the only state in which its length is a field on
    /// the object rather than a stored property.
    /// </summary>
    GuardDenseArray,

    /// <summary>
    /// Fails unless the receiver's [[Prototype]] is still the object it was at
    /// attach time. Shapes here do not encode the prototype, so reassigning it
    /// leaves the shape alone and this is the only thing that catches it.
    /// </summary>
    GuardProto,

    /// <summary>
    /// Fails unless the object holding the property still has the layout it had
    /// at attach time - so the property is still the same one, in the same slot.
    /// </summary>
    GuardHolderShape,

    /// <summary>
    /// Reads a known slot of the object that holds the property, rather than of
    /// the receiver, and terminates. This is what makes a method call on a class
    /// instance cacheable: the method is on the prototype, and before this every
    /// such read walked the chain.
    /// </summary>
    LoadHolderSlotResult,

    /// <summary>
    /// Fails unless the receiver is a string primitive. A string is not an
    /// object, so it has no shape and every read off one missed every site.
    /// Its own properties are exactly `length` and its integer indices; every
    /// other name resolves on the realm's %String.prototype%, which is what
    /// makes the answer cacheable at all.
    /// </summary>
    GuardStringReceiver,

    /// <summary>
    /// Fails unless the realm's %String.prototype% is still the object recorded
    /// at attach time. A string receiver carries nothing that says which realm
    /// it came from, so without this a site warmed in one frame would hand a
    /// second frame the first one's methods.
    /// </summary>
    GuardStringPrototype,

    /// <summary>
    /// Yields a string primitive's length, and terminates. Read without
    /// flattening the value, so `s.length` inside the loop that builds `s` does
    /// not put the quadratic cost back.
    /// </summary>
    LoadStringLengthResult,

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
