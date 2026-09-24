using System.Text;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core;

// CSS Syntax 3 section 3.2 "determine the fallback encoding" for stylesheets.
public sealed class CssStylesheetEncodingTests
{
    static CssStylesheetEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // "#И { visibility:hidden }" with U+0418 as windows-1251 byte 0xC8.
    private static byte[] Sheet(string prefix) =>
        Encoding.ASCII.GetBytes(prefix).Concat(new byte[] { (byte)'#', 0xC8, (byte)' ', (byte)'{', (byte)'}' }).ToArray();

    [Theory]
    [InlineData("", "text/css")]
    [InlineData("@charset \"bogus\";\n", "text/css")]
    [InlineData("", "text/css; charset=bogus")]
    public void AnUnusableDeclarationFallsBackToTheDocumentEncoding(string prefix, string contentType)
    {
        var text = EncodingSniffer.DecodeCssToUtf8(Sheet(prefix), contentType, linkCharset: null, documentCharset: "windows-1251");

        Assert.Contains("#И", text);
    }

    [Fact]
    public void AnUnusableLinkCharsetFallsBackToTheDocumentEncoding()
    {
        var text = EncodingSniffer.DecodeCssToUtf8(Sheet("@charset \"bogus\";\n"), "text/css", linkCharset: "bogus", documentCharset: "windows-1251");

        Assert.Contains("#И", text);
    }
}
