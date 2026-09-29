using FenBrowser.Core.Logging;

namespace FenBrowser.Tests.Logging;

public sealed class LogUrlTests
{
    [Theory]
    [InlineData("https://Shop.Example.com/account/42/avatar.svg?session=abc123#x", "https://shop.example.com #")]
    [InlineData("http://user:pass@example.com:8080/a.png", "http://example.com:8080 #")]
    [InlineData("https://example.com:443/a.png", "https://example.com #")]
    public void NetworkUrls_KeepOnlyTheOriginAndAHash(string url, string prefix)
    {
        string described = LogUrl.Describe(url);

        Assert.StartsWith(prefix, described, StringComparison.Ordinal);
        Assert.Matches("#[0-9a-f]{8}$", described);
        Assert.DoesNotContain("account", described);
        Assert.DoesNotContain("session", described);
        Assert.DoesNotContain("pass", described);
    }

    [Fact]
    public void TheSameUrl_AlwaysDescribesTheSameWay_AndDifferentPathsDiffer()
    {
        Assert.Equal(LogUrl.Describe("https://a.test/x.svg"), LogUrl.Describe("https://a.test/x.svg"));
        Assert.NotEqual(LogUrl.Describe("https://a.test/x.svg"), LogUrl.Describe("https://a.test/y.svg"));
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=", "data:image/svg+xml (42 chars)")]
    [InlineData("data:,hello", "data:unknown (11 chars)")]
    [InlineData("data:image/png<script>;base64,AAAA", "data:image/pngscript (34 chars)")]
    public void DataUrls_NeverIncludeThePayload(string url, string expected)
    {
        Assert.Equal(expected, LogUrl.Describe(url));
    }

    [Theory]
    [InlineData("images/private/me.png", "(relative #")]
    [InlineData("file:///C:/Users/me/secret.svg", "file: #")]
    [InlineData("blob:https://a.test/0f1e", "blob: #")]
    public void OtherUrls_KeepOnlyTheirScheme(string url, string prefix)
    {
        string described = LogUrl.Describe(url);

        Assert.StartsWith(prefix, described, StringComparison.Ordinal);
        Assert.DoesNotContain("private", described);
        Assert.DoesNotContain("secret", described);
    }

    [Fact]
    public void MissingUrls_AreNamedAsMissing()
    {
        Assert.Equal("(none)", LogUrl.Describe((string?)null!));
        Assert.Equal("(none)", LogUrl.Describe((Uri?)null!));
    }
}
