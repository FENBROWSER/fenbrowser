namespace FenBrowser.Js.Runtime;

public readonly record struct SymbolHandle(int Index, int Generation)
{
    public long ToInt64() => ((long)Generation << 32) | (uint)Index;

    public static SymbolHandle FromInt64(long encoded)
    {
        var index = unchecked((int)(encoded & 0xFFFF_FFFFL));
        var generation = unchecked((int)(encoded >> 32));
        return new SymbolHandle(index, generation);
    }
}
