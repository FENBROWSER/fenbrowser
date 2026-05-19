namespace FenBrowser.Js.Runtime;

public readonly record struct HostObjectHandle(
    int Index,
    int Generation,
    int RealmId,
    int DocumentEpoch)
{
    public long ToInt64()
    {
        ValidatePart(nameof(Index), Index);
        ValidatePart(nameof(Generation), Generation);
        ValidatePart(nameof(RealmId), RealmId);
        ValidatePart(nameof(DocumentEpoch), DocumentEpoch);
        return ((long)(ushort)DocumentEpoch << 48) |
               ((long)(ushort)RealmId << 32) |
               ((long)(ushort)Generation << 16) |
               (ushort)Index;
    }

    public static HostObjectHandle FromInt64(long value)
    {
        var index = (int)(ushort)(value & 0xFFFF);
        var generation = (int)(ushort)((value >> 16) & 0xFFFF);
        var realmId = (int)(ushort)((value >> 32) & 0xFFFF);
        var documentEpoch = (int)(ushort)((value >> 48) & 0xFFFF);
        return new HostObjectHandle(index, generation, realmId, documentEpoch);
    }

    private static void ValidatePart(string name, int value)
    {
        if ((uint)value > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, "HostObjectHandle part must fit in 16 bits.");
        }
    }
}
