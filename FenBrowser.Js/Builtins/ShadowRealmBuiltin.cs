using System;
using System.Collections.Generic;

namespace FenBrowser.Js.Builtins;

/// <summary>
/// ShadowRealm is intentionally not exposed until FenJS has a distinct Realm Record,
/// global object/global environment, intrinsic set, and the required cross-realm
/// wrapping semantics. Exposing a same-realm implementation would falsely advertise
/// an isolation boundary and allow code passed to evaluate() to run in the caller's
/// global scope.
/// </summary>
[EcmaSpecReference("3.1", AbstractOperation = "ShadowRealm", Url = "https://tc39.es/proposal-shadowrealm/")]
public sealed class ShadowRealmBuiltin : IBuiltinModule
{
    public string Name => "ShadowRealm";

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Array.Empty<BuiltinBinding>();
    }
}
