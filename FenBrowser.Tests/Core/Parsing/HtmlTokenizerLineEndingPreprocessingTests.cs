using System.IO;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerLineEndingPreprocessingTests
{
    [Fact]
    public void Tokenize_StringInput_NormalizesCrLfAndLoneCr()
    {
        string text = string.Concat(
            new HtmlTokenizer("a\r\nb\rc").Tokenize()
                .OfType<CharacterToken>()
                .Select(token => token.Data));

        Assert.Equal("a\nb\nc", text);
    }

    [Fact]
    public void ParseDocument_NormalizesTextRcDataAndScriptSource()
    {
        const string html = "<!doctype html><title>a\r\nb\rc</title>" +
            "<body><p>d\r\ne\rf</p><textarea>g\r\nh\ri</textarea>" +
            "<script>j\r\nk\rl</script></body>";

        Document document = HtmlParser.ParseDocument(html);
        Element title = document.Descendants().OfType<Element>().Single(e => e.LocalName == "title");
        Element paragraph = document.Descendants().OfType<Element>().Single(e => e.LocalName == "p");
        Element textarea = document.Descendants().OfType<Element>().Single(e => e.LocalName == "textarea");
        Element script = document.Descendants().OfType<Element>().Single(e => e.LocalName == "script");

        Assert.Equal("a\nb\nc", title.TextContent);
        Assert.Equal("d\ne\nf", paragraph.TextContent);
        Assert.Equal("g\nh\ni", textarea.TextContent);
        Assert.Equal("j\nk\nl", script.TextContent);
    }

    [Fact]
    public void ParseStream_NormalizesCrLfSplitAcrossReaderChunks()
    {
        const string html = "<!doctype html><body><p>a\r\nb\rc</p></body>";
        using var reader = new OneCharacterTextReader(html);

        Document document = HtmlParser.ParseStream(reader, options: null, out HtmlParsingOutcome outcome);
        Element paragraph = document.Descendants().OfType<Element>().Single(e => e.LocalName == "p");

        Assert.Equal(HtmlParsingOutcomeClass.Success, outcome.OutcomeClass);
        Assert.Equal("a\nb\nc", paragraph.TextContent);
        Assert.True(reader.ReadCallCount > html.Length);
    }

    [Fact]
    public void ParseStream_InputLimitCountsRawCrLfCharacters()
    {
        const string html = "\r\n\r\n\r\n\r\n\r\n";
        using var reader = new OneCharacterTextReader(html);
        var options = new HtmlParserOptions { MaxInputLengthChars = 6 };

        _ = HtmlParser.ParseStream(reader, options, out HtmlParsingOutcome outcome);

        Assert.Equal(HtmlParsingOutcomeClass.Degraded, outcome.OutcomeClass);
        Assert.Equal(HtmlParsingReasonCode.InputSizeLimitExceeded, outcome.ReasonCode);
        Assert.True(reader.CharactersRead <= 7);
    }

    [Fact]
    public void ParseDocument_InputLimitCountsRawCrLfCharacters()
    {
        const string html = "\r\n\r\n\r\n\r\n\r\n";
        var options = new HtmlParserOptions { MaxInputLengthChars = 6 };

        _ = HtmlParser.ParseDocument(html, options, out HtmlParsingOutcome outcome);

        Assert.Equal(HtmlParsingOutcomeClass.Degraded, outcome.OutcomeClass);
        Assert.Equal(HtmlParsingReasonCode.InputSizeLimitExceeded, outcome.ReasonCode);
    }

    private sealed class OneCharacterTextReader : TextReader
    {
        private readonly string _value;
        private int _position;

        public OneCharacterTextReader(string value) => _value = value;

        public int ReadCallCount { get; private set; }
        public int CharactersRead => _position;

        public override int Read(char[] buffer, int index, int count)
        {
            ReadCallCount++;
            if (_position >= _value.Length)
            {
                return 0;
            }

            buffer[index] = _value[_position++];
            return 1;
        }

        public override int Read()
        {
            ReadCallCount++;
            return _position < _value.Length ? _value[_position++] : -1;
        }
    }
}
