using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Host;

// ECMA-262 has no equivalent type - this is FenBrowser-side per plan §23.2.
//
// The table owns the mapping from JS-side HostObjectHandle to renderer-side browser
// object. Each row carries a HostObjectEntry (security/lifetime metadata) and a
// reference to the browser object. Resolve cross-checks the complete packed handle
// identity before dereferencing the browser object.
//
// Slots are recycled via a generation counter: freeing a handle keeps the row but
// flips IsAlive=false; the next Register reuses the slot with Generation+1 so stale
// handles holding the old generation are rejected. Slots whose 16-bit generation is
// exhausted are permanently retired rather than wrapping into an old handle value.
public sealed class HostObjectTable
{
    private const int MaxPackedValue = ushort.MaxValue;
    private const int MaxSlots = ushort.MaxValue + 1;

    private readonly List<Slot> _slots = new();
    private readonly Stack<int> _freeList = new();

    public int LiveCount { get; private set; }

    public HostObjectHandle Register(object? hostObject, HostObjectEntry entry)
    {
        ValidatePackedPart(nameof(entry.RealmId), entry.RealmId);

        int index = -1;
        int generation = 1;

        // Reuse only slots whose next generation still fits the packed handle. Once a
        // slot reaches generation 65535, reusing it would either fail serialization or
        // wrap into a generation that can collide with an ancient stale handle.
        while (_freeList.Count > 0)
        {
            var candidate = _freeList.Pop();
            var previous = _slots[candidate];
            if (previous.Generation >= MaxPackedValue)
            {
                continue; // permanently retire this index
            }

            index = candidate;
            generation = previous.Generation + 1;
            break;
        }

        if (index >= 0)
        {
            _slots[index] = new Slot(
                generation,
                entry with { Generation = generation },
                hostObject,
                IsAlive: true);
        }
        else
        {
            if (_slots.Count >= MaxSlots)
            {
                throw new InvalidOperationException(
                    "Host object table exhausted its 16-bit handle index space.");
            }

            index = _slots.Count;
            _slots.Add(new Slot(
                generation,
                entry with { Generation = generation },
                hostObject,
                IsAlive: true));
        }

        LiveCount++;

        // HostObjectHandle packs the document epoch into 16 bits. The full epoch
        // remains in HostObjectEntry and is checked against the current document;
        // the packed low bits are still part of the handle identity and must match.
        var packedEpoch = PackEpoch(entry.DocumentEpoch);
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

        if (slot.Entry.RealmId != handle.RealmId)
        {
            return HostObjectResolution.Invalid("handle realm mismatch");
        }

        if (PackEpoch(slot.Entry.DocumentEpoch) != handle.DocumentEpoch)
        {
            return HostObjectResolution.Invalid("handle document epoch mismatch");
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
        if (!slot.IsAlive ||
            slot.Generation != handle.Generation ||
            slot.Entry.RealmId != handle.RealmId ||
            PackEpoch(slot.Entry.DocumentEpoch) != handle.DocumentEpoch)
        {
            return false;
        }

        _slots[handle.Index] = slot with { HostObject = null, IsAlive = false };
        if (slot.Generation < MaxPackedValue)
        {
            _freeList.Push(handle.Index);
        }

        LiveCount--;
        return true;
    }

    public int SlotCountForTest => _slots.Count;

    private static int PackEpoch(DocumentEpoch epoch) => (int)(epoch.Value & MaxPackedValue);

    private static void ValidatePackedPart(string name, int value)
    {
        if ((uint)value > MaxPackedValue)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                "HostObjectHandle part must fit in 16 bits.");
        }
    }

    private readonly record struct Slot(
        int Generation,
        HostObjectEntry Entry,
        object? HostObject,
        bool IsAlive);
}
