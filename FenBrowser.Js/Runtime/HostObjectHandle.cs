namespace FenBrowser.Js.Runtime;

public readonly record struct HostObjectHandle(
    int Index,
    int Generation,
    int RealmId,
    int DocumentEpoch)
{
    public long ToInt64() => ((long)Generation << 32) | (uint)Index;

    public static HostObjectHandle FromInt64(long value, int realmId = 0, int documentEpoch = 0)
    {
        var index = (int)(value & 0xFFFFFFFF);
        var generation = (int)(value >> 32);
        return new HostObjectHandle(index, generation, realmId, documentEpoch);
    }
}
