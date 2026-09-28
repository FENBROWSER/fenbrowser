using System.Text;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core;

// HTML "fetch a single module script" decodes as UTF-8; a classic script's
// fallback is its document's encoding and JSON is always UTF-8. x.com serves
// its modules as application/javascript with no charset, and the HTML
// Windows-1252 fallback rendered their "·" as "Â·".
public sealed class ScriptEncodingFallbackTests
{
    static ScriptEncodingFallbackTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly byte[] MiddleDotScript = Encoding.UTF8.GetBytes("var sep = \"·\";");

    [Theory]
    [InlineData("application/javascript")]
    [InlineData("text/javascript")]
    [InlineData("application/x-javascript")]
    [InlineData("application/json")]
    [InlineData("application/manifest+json")]
    public void AScriptOrJsonWithoutACharsetIsUtf8(string contentType)
    {
        Assert.Equal("var sep = \"·\";", EncodingSniffer.DecodeToUtf8(MiddleDotScript, contentType));
    }

    [Fact]
    public void ADeclaredCharsetStillWins()
    {
        var latin1 = Encoding.GetEncoding(1252).GetBytes("var sep = \"·\";");

        Assert.Equal("var sep = \"·\";", EncodingSniffer.DecodeToUtf8(latin1, "text/javascript; charset=windows-1252"));
    }

    [Fact]
    public void AnHtmlDocumentKeepsTheLegacyFallback()
    {
        var encoding = EncodingSniffer.DetermineEncoding(Encoding.ASCII.GetBytes("<p>x</p>"), "text/html");

        Assert.Equal(1252, encoding.CodePage);
    }
}
