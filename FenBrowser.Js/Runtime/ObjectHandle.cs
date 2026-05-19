namespace FenBrowser.Js.Runtime;

public readonly record struct ObjectHandle(int Index, int Generation)
{
    public long ToInt64() => ((long)Generation << 32) | (uint)Index;

    public static ObjectHandle FromInt64(long value)
    {
        var index = (int)(value & 0xFFFFFFFF);
        var generation = (int)(value >> 32);
        return new ObjectHandle(index, generation);
    }
}
