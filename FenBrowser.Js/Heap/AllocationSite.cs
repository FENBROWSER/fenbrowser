using System.Runtime.CompilerServices;

namespace FenBrowser.Js.Heap;

public readonly record struct AllocationSite(string MemberName, string FilePath, int LineNumber)
{
    public static AllocationSite Current(
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        return new AllocationSite(memberName, filePath, lineNumber);
    }
}
