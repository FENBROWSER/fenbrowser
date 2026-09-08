using FenBrowser.Js.Jit.CacheIR;
using FenBrowser.Js.Runtime;

#if !PUBLISH_AOT
namespace FenBrowser.Js.Jit.Baseline;

/// <summary>
/// The object operands one compiled body needs, gathered so the code can reach
/// them by index.
/// </summary>
/// <remarks>
/// IL cannot carry an object reference the way an expression tree's constant
/// can, so a compiled body loads this once on entry and indexes it afterwards.
/// It hangs off the function it was built for and dies with it.
/// </remarks>
internal sealed class BaselinePool(JsValue[] constants, string[] names, CacheIRSite[] sites)
{
    internal readonly JsValue[] Constants = constants;
    internal readonly string[] Names = names;
    internal readonly CacheIRSite[] Sites = sites;
}
#endif
