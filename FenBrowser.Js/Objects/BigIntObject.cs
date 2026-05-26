using System.Numerics;
namespace FenBrowser.Js.Objects;
public sealed class BigIntObject : JsObject
{
    public BigInteger Value { get; }
    public BigIntObject(BigInteger value) { Value = value; }
}
