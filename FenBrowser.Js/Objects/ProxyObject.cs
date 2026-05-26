using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 28.2 Proxy exotic object.
// Minimal stub — full revocability and trap dispatch deferred.
public sealed class ProxyObject : JsObject
{
#pragma warning disable CS0414
    private bool _revoked;
#pragma warning restore CS0414

    public ProxyObject(ObjectHandle target, ObjectHandle handler)
    {
    }

    public void Revoke()
    {
        _revoked = true;
    }
}
