using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

// WHATWG MIME Sniffing §4.4 "parse a MIME type" and §4.5 serialization.
public class MimeTypeTests
{
    [Theory]
    [InlineData("video/webm", "video/webm")]
    [InlineData("  VIDEO/WebM  ", "video/webm")]
    [InlineData("text/html;charset=gbk", "text/html;charset=gbk")]
    [InlineData("TEXT/HTML;CHARSET=GBK", "text/html;charset=GBK")]
    [InlineData("text/html;charset=gbk;charset=windows-1255", "text/html;charset=gbk")]
    [InlineData("text/html;charset=\"gbk\"", "text/html;charset=gbk")]
    [InlineData("text/html;charset=\"shift_jis\"iso-2022-jp", "text/html;charset=shift_jis")]
    [InlineData("text/html;charset=;charset=foo", "text/html;charset=foo")]
    [InlineData("text/html;charset", "text/html")]
    [InlineData("text/html;;;;charset=gbk", "text/html;charset=gbk")]
    [InlineData("text/html;charset= gbk", "text/html;charset=\" gbk\"")]
    [InlineData("text/html;charset=\"\"", "text/html;charset=\"\"")]
    [InlineData("text/html;charset=\"\\\\\\\"\"", "text/html;charset=\"\\\\\\\"\"")]
    [InlineData("text/html;charset=\"gbk", "text/html;charset=gbk")]
    [InlineData("text/html;charset=\"gbk\\", "text/html;charset=\"gbk\\\\\"")]
    [InlineData("text/html;test=ÿ;charset=gbk", "text/html;test=\"ÿ\";charset=gbk")]
    [InlineData("text/html ;charset=gbk", "text/html;charset=gbk")]
    [InlineData("video/mp4; codecs=\"avc1.42E01E, mp4a.40.2\"", "video/mp4;codecs=\"avc1.42E01E, mp4a.40.2\"")]
    public void Parse_AndSerialize_MatchTheSpec(string input, string serialized)
    {
        var mime = MimeType.Parse(input);
        Assert.NotNull(mime);
        Assert.Equal(serialized, mime.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("video")]
    [InlineData("video/")]
    [InlineData("/webm")]
    [InlineData("vi deo/webm")]
    [InlineData("video/we bm")]
    [InlineData("video/webm/extraĀ")]
    [InlineData("Ā/webm")]
    [InlineData("video/\"webm\"")]
    [InlineData(";charset=gbk")]
    public void Parse_Fails(string input)
    {
        Assert.Null(MimeType.Parse(input));
    }

    [Fact]
    public void Parameters_AreLowercasedNamesAndRawValues()
    {
        var mime = MimeType.Parse("Audio/Ogg; CODECS=Opus; x=1")!;
        Assert.Equal("audio", mime.Type);
        Assert.Equal("ogg", mime.Subtype);
        Assert.Equal("audio/ogg", mime.Essence);
        Assert.Equal("Opus", mime.GetParameter("codecs"));
        Assert.Equal("1", mime.GetParameter("x"));
        Assert.Null(mime.GetParameter("CODECS"));
        Assert.Equal(2, mime.ParameterCount);
    }

    [Fact]
    public void InvalidParameterValue_IsDropped()
    {
        // U+0100 is outside the HTTP quoted-string token code points.
        var mime = MimeType.Parse("text/plain;a=Ā;b=ok")!;
        Assert.Null(mime.GetParameter("a"));
        Assert.Equal("ok", mime.GetParameter("b"));
    }
}
