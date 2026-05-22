using FenBrowser.Js.Builtins;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class EcmaSpecReferenceAttributeTests
{
    [EcmaSpecReference("21.3", AbstractOperation = "Math", Url = "https://tc39.es/ecma262/#sec-math-object")]
    private sealed class Sample;

    [EcmaSpecReference("21.3.2.1")]
    [EcmaSpecReference("21.3.2.10")]
    private static class MultiSample
    {
        public static int Stub() => 0;
    }

    [Fact]
    public void StoresSectionAndOptionalFields()
    {
        var attr = typeof(Sample)
            .GetCustomAttributes(typeof(EcmaSpecReferenceAttribute), inherit: false)[0]
            as EcmaSpecReferenceAttribute;

        Assert.NotNull(attr);
        Assert.Equal("21.3", attr!.Section);
        Assert.Equal("Math", attr.AbstractOperation);
        Assert.Equal("https://tc39.es/ecma262/#sec-math-object", attr.Url);
    }

    [Fact]
    public void AllowsMultipleAttributesPerTarget()
    {
        var attrs = typeof(MultiSample)
            .GetCustomAttributes(typeof(EcmaSpecReferenceAttribute), inherit: false);

        Assert.Equal(2, attrs.Length);
    }

    [Fact]
    public void RejectsEmptySection()
    {
        Assert.Throws<ArgumentException>(() => new EcmaSpecReferenceAttribute(string.Empty));
    }

    [Fact]
    public void RejectsNullSection()
    {
        Assert.Throws<ArgumentNullException>(() => new EcmaSpecReferenceAttribute(null!));
    }
}
