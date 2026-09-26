using FenBrowser.Media.Text;

namespace FenBrowser.Media.Tests;

/// <summary>
/// Expected trees mirror WPT <c>webvtt/parsing/cue-text-parsing</c> (tags, classes,
/// annotations, entities, timestamps, and the recovery rules for bad markup).
/// </summary>
public class WebVttCueTextTests
{
    [Fact]
    public void PlainText_IsOneTextNode()
    {
        var root = WebVttCueText.Parse("hello world");
        var node = Assert.Single(root.Children);
        Assert.Equal(VttNodeKind.Text, node.Kind);
        Assert.Equal("hello world", node.Text);
    }

    [Theory]
    [InlineData("<c>x</c>", VttNodeKind.Class)]
    [InlineData("<i>x</i>", VttNodeKind.Italic)]
    [InlineData("<b>x</b>", VttNodeKind.Bold)]
    [InlineData("<u>x</u>", VttNodeKind.Underline)]
    [InlineData("<ruby>x</ruby>", VttNodeKind.Ruby)]
    [InlineData("<v Bob>x</v>", VttNodeKind.Voice)]
    [InlineData("<lang en>x</lang>", VttNodeKind.Language)]
    public void Tags_MakeInternalNodes(string text, VttNodeKind kind)
    {
        var root = WebVttCueText.Parse(text);
        var node = Assert.Single(root.Children);
        Assert.Equal(kind, node.Kind);
        Assert.Equal("x", Assert.Single(node.Children).Text);
    }

    [Fact]
    public void Classes_AreSplitOnDots_EmptyOnesDropped()
    {
        var root = WebVttCueText.Parse("<c.a.b..c>x</c>");
        Assert.Equal(["a", "b", "c"], Assert.Single(root.Children).Classes);
        Assert.Empty(WebVttCueText.Parse("<c.>x</c>").Children[0].Classes);
    }

    [Fact]
    public void Voice_AnnotationCollapsesWhitespace_AndCanCarryClasses()
    {
        var root = WebVttCueText.Parse("<v.loud.fast  Bob   Smith >x</v>");
        var voice = Assert.Single(root.Children);
        Assert.Equal(VttNodeKind.Voice, voice.Kind);
        Assert.Equal("Bob Smith", voice.Annotation);
        Assert.Equal(["loud", "fast"], voice.Classes);
    }

    [Fact]
    public void Voice_WithoutAnnotation_IsStillAVoice()
    {
        var root = WebVttCueText.Parse("<v>x</v>");
        Assert.Equal(VttNodeKind.Voice, Assert.Single(root.Children).Kind);
        Assert.Equal("", root.Children[0].Annotation);
    }

    [Fact]
    public void Language_SetsTheLanguageOfDescendants()
    {
        var root = WebVttCueText.Parse("a<lang fr>b<i>c</i></lang>d", "en");
        Assert.Equal("en", root.Children[0].Language);
        var lang = root.Children[1];
        Assert.Equal("fr", lang.Annotation);
        Assert.Equal("fr", lang.Children[0].Language);
        Assert.Equal("fr", lang.Children[1].Language);
        Assert.Equal("fr", lang.Children[1].Children[0].Language);
        Assert.Equal("en", root.Children[2].Language);
    }

    [Fact]
    public void RubyText_OnlyInsideRuby()
    {
        var root = WebVttCueText.Parse("<ruby>base<rt>text</rt></ruby>");
        var ruby = Assert.Single(root.Children);
        Assert.Equal(2, ruby.Children.Count);
        Assert.Equal(VttNodeKind.RubyText, ruby.Children[1].Kind);

        // rt outside ruby is an unknown tag: dropped, its content stays.
        root = WebVttCueText.Parse("<rt>text</rt>");
        Assert.Equal("text", Assert.Single(root.Children).Text);
    }

    [Fact]
    public void RubyEndTag_ClosesAnOpenRubyText()
    {
        var root = WebVttCueText.Parse("<ruby>base<rt>text</ruby>after");
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(VttNodeKind.Ruby, root.Children[0].Kind);
        Assert.Equal("after", root.Children[1].Text);
    }

    [Fact]
    public void UnknownTags_AreDropped_ContentKept()
    {
        var root = WebVttCueText.Parse("a<span>b</span>c<x.y z>d");
        Assert.Equal("abcd", root.PlainText());
        Assert.All(root.Children, n => Assert.Equal(VttNodeKind.Text, n.Kind));
    }

    [Fact]
    public void MismatchedEndTags_AreIgnored()
    {
        var root = WebVttCueText.Parse("<b>x</i>y</b>z");
        var b = root.Children[0];
        Assert.Equal(VttNodeKind.Bold, b.Kind);
        Assert.Equal("xy", b.PlainText());
        Assert.Equal("z", root.Children[1].Text);
    }

    [Fact]
    public void UnclosedTags_CloseAtTheEnd()
    {
        var root = WebVttCueText.Parse("<b><i>x");
        Assert.Equal(VttNodeKind.Italic, root.Children[0].Children[0].Kind);
        Assert.Equal("x", root.PlainText());
    }

    [Fact]
    public void Entities_FollowTheHtmlCharacterReferenceRules()
    {
        Assert.Equal("a&b <c>   ©", WebVttCueText.Parse("a&amp;b &lt;c&gt; &nbsp; &copy;").PlainText());
        // Legacy names need no semicolon, and the longest name wins: "&not" + "it;".
        Assert.Equal("& b", WebVttCueText.Parse("&amp b").PlainText());
        Assert.Equal("¬it;", WebVttCueText.Parse("&notit;").PlainText());
        Assert.Equal("¬anentity;", WebVttCueText.Parse("&notanentity;").PlainText());
        Assert.Equal("A €  ", WebVttCueText.Parse("&#65; &#x20AC; &#32;").PlainText());
        Assert.Equal("&", WebVttCueText.Parse("&").PlainText());
        Assert.Equal("&&", WebVttCueText.Parse("&&").PlainText());
        Assert.Equal("&1;", WebVttCueText.Parse("&1;").PlainText());
        Assert.Equal("&;", WebVttCueText.Parse("&;").PlainText());
        Assert.Equal("&#", WebVttCueText.Parse("&#").PlainText());
        Assert.Equal("� €", WebVttCueText.Parse("&#0; &#x80;").PlainText());
        Assert.Equal("&", WebVttCueText.Parse("&<c>").PlainText());
    }

    [Fact]
    public void Entities_UseTheSuppliedTable()
    {
        var table = new FakeReferences();
        Assert.Equal("∲", WebVttCueText.Parse("&ClockwiseContourIntegral;", "", table).PlainText());
        Assert.Equal("&ClockwiseContourIntegral;", WebVttCueText.Parse("&ClockwiseContourIntegral;").PlainText());
    }

    private sealed class FakeReferences : IHtmlNamedCharacterReferences
    {
        public bool TryMatchLongest(string input, int position, out string replacement, out int length)
        {
            const string name = "ClockwiseContourIntegral;";
            if (string.CompareOrdinal(input, position, name, 0, name.Length) == 0)
            {
                replacement = "∲";
                length = name.Length;
                return true;
            }

            replacement = "";
            length = 0;
            return false;
        }
    }

    [Fact]
    public void Entities_InAnnotations_Decode()
    {
        var root = WebVttCueText.Parse("<v Bob &amp; Sue>x</v>");
        Assert.Equal("Bob & Sue", root.Children[0].Annotation);
    }

    [Fact]
    public void Timestamps_BecomeTimestampNodes()
    {
        var root = WebVttCueText.Parse("a<00:00:01.500>b<01:02.250>c<0:1.0>d");
        Assert.Equal(6, root.Children.Count);
        Assert.Equal(VttNodeKind.Timestamp, root.Children[1].Kind);
        Assert.Equal(1.5, root.Children[1].Timestamp.TotalSeconds);
        Assert.Equal(62.25, root.Children[3].Timestamp.TotalSeconds);
        // A malformed timestamp tag is dropped.
        Assert.Equal("d", root.Children[5].Text);
        Assert.Equal("abcd", root.PlainText());
    }

    [Fact]
    public void Newlines_StayInTextNodes()
    {
        var root = WebVttCueText.Parse("line one\nline two");
        Assert.Equal("line one\nline two", Assert.Single(root.Children).Text);
    }

    [Fact]
    public void LessThanAtTheEnd_IsAnEmptyStartTag()
    {
        Assert.Equal("a", WebVttCueText.Parse("a<").PlainText());
        Assert.Equal("ab", WebVttCueText.Parse("a<>b").PlainText());
        Assert.Equal("a", WebVttCueText.Parse("a</").PlainText());
    }

    [Fact]
    public void GreaterThan_AloneIsText()
    {
        Assert.Equal("a>b", WebVttCueText.Parse("a>b").PlainText());
    }

    [Fact]
    public void Nested_StructureIsPreserved()
    {
        var root = WebVttCueText.Parse("<v Bob><b>bold <i>both</i></b> plain</v>");
        var voice = Assert.Single(root.Children);
        Assert.Equal(2, voice.Children.Count);
        var bold = voice.Children[0];
        Assert.Equal(VttNodeKind.Bold, bold.Kind);
        Assert.Equal("bold ", bold.Children[0].Text);
        Assert.Equal(VttNodeKind.Italic, bold.Children[1].Kind);
        Assert.Equal(" plain", voice.Children[1].Text);
    }
}
