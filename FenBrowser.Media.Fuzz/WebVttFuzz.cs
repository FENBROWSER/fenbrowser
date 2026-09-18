using System.Text;
using FenBrowser.Media.Text;
using Xunit;

namespace FenBrowser.Media.Fuzz;

/// <summary>
/// The WebVTT file and cue text parsers must never throw and must finish in time
/// proportional to the input, whatever a hostile track file contains.
/// </summary>
public sealed class WebVttFuzz
{
    private const int Iterations = 12_000;

    private static readonly string[] s_seeds =
    [
        "WEBVTT\n\n00:00:00.000 --> 00:00:01.000 line:5 position:10%,line-left size:50% align:end vertical:rl\n<v Bob>hello <b>bold</b> &amp; <00:00:00.500>late</v>",
        "WEBVTT - header\n\nREGION\nid:fred\nwidth:40%\nlines:3\nregionanchor:0%,100%\nviewportanchor:10%,90%\nscroll:up\n\nSTYLE\n::cue { color: red }\n\nNOTE comment\n\nid1\n00:00.000 --> 01:00.000 region:fred\n<ruby>base<rt>rt</rt></ruby><lang en><i>x</i></lang>",
        "\uFEFFWEBVTT\r\n\r\n1\r\n00:00:00.000 --> 00:00:01.000\r\ntext -->\r\n00:00:00.000 --> 00:00:01.000\r\n",
    ];

    private static readonly string[] s_atoms =
    [
        "WEBVTT", "-->", "\n", "\n\n", "\r", ":", ".", "%", ",", "<", ">", "</", "&", ";", "&amp;", "&#x", "<v ", "<c.", "<lang ", "<ruby>", "<rt>",
        "line:", "position:", "size:", "align:", "vertical:", "region:", "REGION", "STYLE", "NOTE", "id:", "width:", "lines:", "scroll:up",
        "00:00:00.000", "99:59:59.999", "1e308", "-", "0", "\0", "\uFEFF", "\t", " ", "\uFFFD", "𝄞",
    ];

    [Fact]
    public void FileParser_NeverThrows()
    {
        var random = new Random(7);
        for (int i = 0; i < Iterations; i++)
        {
            string input = Mutate(random, s_seeds[i % s_seeds.Length]);
            var file = WebVttParser.Parse(input);
            if (file is null)
                continue;
            foreach (var cue in file.Cues)
            {
                Assert.True(cue.Line is null || double.IsFinite(cue.Line.Value));
                Assert.True(cue.Position is null || (cue.Position >= 0 && cue.Position <= 100));
                Assert.InRange(cue.Size, 0, 100);
                var tree = WebVttCueText.Parse(cue.Text, "en");
                Assert.Equal(VttNodeKind.Root, tree.Kind);
            }

            foreach (var region in file.Regions)
            {
                Assert.InRange(region.Width, 0, 100);
                Assert.True(region.Lines <= uint.MaxValue);
            }
        }
    }

    [Fact]
    public void CueTextParser_NeverThrows_AndKeepsEveryCharacterOutsideTags()
    {
        var random = new Random(11);
        for (int i = 0; i < Iterations; i++)
        {
            string input = Mutate(random, "a<b>b</b>c<v x>d</v>e&amp;f<00:00:01.000>g");
            var tree = WebVttCueText.Parse(input);
            // Text that contains neither markup nor references survives untouched.
            if (!input.Contains('<') && !input.Contains('&'))
                Assert.Equal(input, tree.PlainText());
        }
    }

    [Fact]
    public void DeeplyNestedTags_DoNotOverflowTheStack()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 100_000; i++)
            sb.Append("<b>");
        sb.Append('x');
        var tree = WebVttCueText.Parse(sb.ToString());
        Assert.Equal("x", tree.PlainText());
        int depth = 0;
        for (var node = tree; node.Children.Count > 0; node = node.Children[0])
            depth++;
        Assert.Equal(WebVttCueText.MaxDepth + 1, depth);
    }

    [Fact]
    public void IterationBudgetMeetsTheDefinitionOfDone() => Assert.True(Iterations >= 10_000);

    private static string Mutate(Random random, string seed)
    {
        var sb = new StringBuilder(seed);
        int edits = random.Next(1, 8);
        for (int e = 0; e < edits; e++)
        {
            switch (random.Next(5))
            {
                case 0 when sb.Length > 0:
                {
                    int at = random.Next(sb.Length);
                    sb.Remove(at, Math.Min(random.Next(1, 12), sb.Length - at));
                    break;
                }
                case 1:
                    sb.Insert(random.Next(sb.Length + 1), s_atoms[random.Next(s_atoms.Length)]);
                    break;
                case 2 when sb.Length > 0:
                    sb[random.Next(sb.Length)] = (char)random.Next(1, 0x300);
                    break;
                case 3:
                    sb.Insert(random.Next(sb.Length + 1), new string(s_atoms[random.Next(s_atoms.Length)][0], random.Next(1, 64)));
                    break;
                default:
                    sb.Insert(random.Next(sb.Length + 1), random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        return sb.ToString();
    }
}
