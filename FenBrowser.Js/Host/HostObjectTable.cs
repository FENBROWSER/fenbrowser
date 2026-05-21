using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Host;

// ECMA-262 has no equivalent type - this is FenBrowser-side per plan §23.2.
//
// The table owns the mapping from JS-side HostObjectHandle to renderer-side browser
// object. Each row carries a HostObjectEntry (security/lifetime metadata) and a
// reference to the browser object. Resolve cross-checks the handle against the
// entry: any mismatch produces an Invalid result so the interpreter can throw a
// SecurityError or TypeError without ever dereferencing the browser pointer.
//
// Slots are recycled via a generation counter: freeing a handle keeps the row but
// flips IsAlive=false; the next Register reuses the slot with Generation+1 so stale
// handles holding the old generation are rejected. The "weak reference" angle from
// the plan (gc-aware tracking) lands in a follow-up - v1 uses strong refs and
// relies on the host to call Free at the right time.
public sealed class HostObjectTable
{
    private readonly List<Slot> _slots = new();
    private readonly Stack<int> _freeList = new();

    public int LiveCount { get; private set; }

    public HostObjectHandle Register(object? hostObject, HostObjectEntry entry)
    {
        int index;
        int generation;

        if (_freeList.Count > 0)
        {
            index = _freeList.Pop();
            var previous = _slots[index];
            generation = previous.Generation + 1;
            _slots[index] = new Slot(generation, entry with { Generation = generation }, hostObject, IsAlive: true);
        }
        else
        {
            index = _slots.Count;
            generation = 1;
            _slots.Add(new Slot(generation, entry with { Generation = generation }, hostObject, IsAlive: true));
        }

        LiveCount++;

        // HostObjectHandle packs values into 16 bits each; truncate the epoch the
        // same way the handle's ToInt64 does so a round-trip through the long form
        // produces identical handles. The full epoch lives in the entry and is what
        // Resolve actually checks against the context.
        var packedEpoch = (int)(entry.DocumentEpoch.Value & 0xFFFF);
        return new HostObjectHandle(index, generation, entry.RealmId, packedEpoch);
    }

    public HostObjectResolution Resolve(HostObjectHandle handle, HostObjectResolveContext context)
    {
        if ((uint)handle.Index >= (uint)_slots.Count)
        {
            return HostObjectResolution.Invalid("index out of range");
        }

        var slot = _slots[handle.Index];
        if (!slot.IsAlive)
        {
            return HostObjectResolution.Invalid("slot freed");
        }

        if (slot.Generation != handle.Generation)
        {
            return HostObjectResolution.Invalid("stale generation");
        }

        if (slot.Entry.RealmId != context.CurrentRealmId)
        {
            return HostObjectResolution.Invalid("cross-realm access");
        }

        if (!slot.Entry.DocumentEpoch.IsValidIn(context.CurrentDocumentEpoch))
        {
            return HostObjectResolution.Invalid("document navigated");
        }

        if (!slot.Entry.NavigationEpoch.IsValidIn(context.CurrentNavigationEpoch))
        {
            return HostObjectResolution.Invalid("top-level navigated");
        }

        return HostObjectResolution.Ok(slot.Entry, slot.HostObject);
    }

    public bool Free(HostObjectHandle handle)
    {
        if ((uint)handle.Index >= (uint)_slots.Count)
        {
            return false;
        }

        var slot = _slots[handle.Index];
        if (!slot.IsAlive || slot.Generation != handle.Generation)
        {
            return false;
        }

        _slots[handle.Index] = slot with { HostObject = null, IsAlive = false };
        _freeList.Push(handle.Index);
        LiveCount--;
        return true;
    }

    public int SlotCountForTest => _slots.Count;

    private readonly record struct Slot(
        int Generation,
        HostObjectEntry Entry,
        object? HostObject,
        bool IsAlive);
}
