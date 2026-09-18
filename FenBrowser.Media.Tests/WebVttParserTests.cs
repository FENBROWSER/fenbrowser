using System.Text.RegularExpressions;
using FenBrowser.Media.Text;

namespace FenBrowser.Media.Tests;

/// <summary>
/// Expected values mirror WPT <c>webvtt/parsing/file-parsing</c>: the signature variants,
/// the header block, cue identifiers, timings, and every settings value those tests feed.
/// </summary>
public class WebVttParserTests
{
    [Theory]
    [InlineData("WEBVTT")]
    [InlineData("WEBVTT\n")]
    [InlineData("WEBVTT ")]
    [InlineData("WEBVTT\t")]
    [InlineData("WEBVTT garbage\n")]
    [InlineData("\uFEFFWEBVTT\n")]
    public void Signature_Variants_AreAccepted(string text)
    {
        var file = WebVttParser.Parse(text);
        Assert.NotNull(file);
        Assert.Empty(file.Cues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WEBVT")]
    [InlineData("webvtt\n")]
    [InlineData("WEBVTTgarbage\n")]
    [InlineData("WEBVTT\f\n")]
    [InlineData("\uFEFF\uFEFFWEBVTT\n")]
    [InlineData("WEBVTT\0\n")]
    [InlineData("WEBSRT\n")]
    public void Signature_Invalid_IsRejected(string text) => Assert.Null(WebVttParser.Parse(text));

    [Fact]
    public void Header_TimingsRightAfterTheSignature_StillMakeACue()
    {
        var file = Parse("WEBVTT\n00:00:00.000 --> 00:00:01.000\ntext");
        var cue = Assert.Single(file.Cues);
        Assert.Equal("text", cue.Text);
        Assert.Equal(0, cue.Start.TotalSeconds);
        Assert.Equal(1, cue.End.TotalSeconds);
    }

    [Fact]
    public void Header_GarbageBeforeTheBlankLine_IsDropped()
    {
        var file = Parse("WEBVTT\ngarbage\nmore garbage\n\n00:00:00.000 --> 00:00:01.000\ntext");
        Assert.Single(file.Cues);
    }

    [Fact]
    public void Ids_KeepTheirWhitespace()
    {
        var file = Parse("WEBVTT\n\n leading space\n00:00:00.000 --> 00:00:01.000\ntext0\n\ntrailing space \n00:00:00.000 --> 00:00:01.000\ntext1\n\n-- >\n00:00:00.000 --> 00:00:01.000\ntext2\n\n->\n00:00:00.000 --> 00:00:01.000\ntext3\n\n \n00:00:00.000 --> 00:00:01.000\ntext4");
        Assert.Equal([" leading space", "trailing space ", "-- >", "->", " "], file.Cues.Select(c => c.Id));
    }

    [Fact]
    public void Arrows_InTheCueTextEndTheBlock()
    {
        var file = Parse("WEBVTT\n\n-->\n00:00:00.000 --> 00:00:01.000\ntext0\nfoo-->\n00:00:00.000 --> 00:00:01.000\ntext1\n-->foo\n00:00:00.000 --> 00:00:01.000\ntext2\n--->\n00:00:00.000 --> 00:00:01.000\ntext3\n-->-->\n00:00:00.000 --> 00:00:01.000\ntext4\n00:00:00.000 --> 00:00:01.000\ntext5\n\n00:00:00.000 -a -->\n\n00:00:00.000 -- -->");
        Assert.Equal(6, file.Cues.Count);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal("", file.Cues[i].Id);
            Assert.Equal("text" + i, file.Cues[i].Text);
        }

        // Anything after the end timestamp is a settings list; "-->" there is just a bad setting.
        Assert.Equal("text", Assert.Single(Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000 -->\ntext").Cues).Text);
    }

    [Fact]
    public void Newlines_CrLfAndCrAreLineFeeds()
    {
        var file = Parse("WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\ntext0\rmore\r\n\r00:00:00.000 --> 00:00:01.000\rtext1");
        Assert.Equal(2, file.Cues.Count);
        Assert.Equal("text0\nmore", file.Cues[0].Text);
        Assert.Equal("text1", file.Cues[1].Text);
    }

    [Fact]
    public void Nulls_BecomeReplacementCharacters()
    {
        var file = Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\ntext\0text");
        Assert.Equal("text\uFFFDtext", Assert.Single(file.Cues).Text);
    }

    [Fact]
    public void Timings_MinutesAndSecondsAbove59AreRejected_HoursAreNot()
    {
        var file = Parse("WEBVTT\n\n00:00:60.000 --> 00:00:01.000\ninvalid\n\n00:60:00.000 --> 00:00:01.000\ninvalid\n\n00:00:00.000 --> 00:00:60.000\ninvalid\n\n00:00:00.000 --> 00:60:00.000\ninvalid\n\n00:00:00.000 --> 60:00:01.000\ntext1\n\n60:00:00.000 --> 60:00:01.000\ntext2");
        Assert.Equal(2, file.Cues.Count);
        Assert.Equal(0, file.Cues[0].Start.TotalSeconds);
        Assert.Equal(216001, file.Cues[0].End.TotalSeconds);
        Assert.Equal(216000, file.Cues[1].Start.TotalSeconds);
    }

    [Fact]
    public void Timings_OmittedHoursAndNegative()
    {
        var file = Parse("WEBVTT\n\n00:00.000 --> 00:01.000\ntext0\n\n-00:00:00.000 --> 00:00:01.000\ninvalid\n\n00:00:00.000 --> -00:00:01.000\ninvalid\n\n00:00.000 --> 01:00.000\ntext1");
        Assert.Equal(2, file.Cues.Count);
        Assert.Equal(1, file.Cues[0].End.TotalSeconds);
        Assert.Equal(60, file.Cues[1].End.TotalSeconds);
    }

    [Theory]
    [InlineData("0000:00.000 --> 00:00:01.000")]      // four digits are hours, so the minutes are missing
    [InlineData("00:0:00.000 --> 00:00:01.000")]      // one-digit minutes
    [InlineData("00:00:00.00 --> 00:00:01.000")]      // two-digit milliseconds
    [InlineData("00:00:00.0000 --> 00:00:01.000")]    // four-digit milliseconds
    [InlineData("00:00:0.000 --> 00:00:01.000")]      // one-digit seconds
    [InlineData("00:00:00,000 --> 00:00:01.000")]     // SRT comma
    [InlineData("00:00:00.000 -> 00:00:01.000")]
    [InlineData("00:00:00.000x--> 00:00:01.000")]
    [InlineData("00:00:00.000 -->x00:00:01.000")]
    [InlineData("00:00.00.000 --> 00:00:01.000")]
    public void Timings_Malformed_DropTheCue(string timings)
    {
        var file = Parse("WEBVTT\n\n" + timings + "\ntext");
        Assert.Empty(file.Cues);
    }

    [Fact]
    public void Timings_OneDigitHours_AndTrailingGarbage_StillParse()
    {
        Assert.Equal(1, Assert.Single(Parse("WEBVTT\n\n0:00:00.000 --> 00:00:01.000\ntext").Cues).End.TotalSeconds);
        Assert.Equal(1, Assert.Single(Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000garbage\ntext").Cues).End.TotalSeconds);
    }

    [Fact]
    public void Timings_LongHours_ParseToLargeSeconds()
    {
        var file = Parse("WEBVTT\n\n000000000000:00:00.000 --> 000000000001:00:00.000\ntext");
        var cue = Assert.Single(file.Cues);
        Assert.Equal(3600, cue.End.TotalSeconds);
    }

    [Fact]
    public void Timings_AtEof_AreACueWithoutText()
    {
        var file = Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000");
        Assert.Equal("", Assert.Single(file.Cues).Text);
    }

    [Theory]
    [InlineData("-1", -1.0, true)]
    [InlineData("0", 0.0, true)]
    [InlineData("-0", 0.0, true)]
    [InlineData("1", 1.0, true)]
    [InlineData("101", 101.0, true)]
    [InlineData("18446744073709552000", 18446744073709552000.0, true)]
    [InlineData("1.5", 1.5, true)]
    [InlineData("0%", 0.0, false)]
    [InlineData("00%", 0.0, false)]
    [InlineData("100%", 100.0, false)]
    public void Settings_Line_Valid(string value, double expected, bool snap)
    {
        var cue = CueWith("line:" + value);
        Assert.Equal(expected, cue.Line);
        Assert.Equal(snap, cue.SnapToLines);
    }

    [Fact]
    public void Settings_Line_DoubleRangeEdges()
    {
        string max = "179769313486231570000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
        Assert.Equal(double.MaxValue, CueWith("line:" + max).Line);
        Assert.Equal(-double.MaxValue, CueWith("line:-" + max).Line);
        string overflow = max.Replace("157", "159");
        Assert.Null(CueWith("line:" + overflow).Line);
        Assert.Null(CueWith("line:-" + overflow).Line);
        string min = "0." + new string('0', 323) + "5";
        Assert.Equal(double.Epsilon, CueWith("line:" + min).Line);
        Assert.Equal(double.Epsilon, CueWith("line:" + min + "%").Line);
        Assert.Equal(0, CueWith("line:0." + new string('0', 323) + "2").Line);
    }

    [Theory]
    [InlineData("65536%")]
    [InlineData("-0%")]
    [InlineData("101%")]
    [InlineData("1%-")]
    [InlineData("1-")]
    [InlineData("%1")]
    [InlineData("1%%")]
    [InlineData("0%0")]
    [InlineData("0%x")]
    [InlineData("-")]
    [InlineData("%")]
    [InlineData("1..5")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1e2")]
    [InlineData("100%,middle")]
    [InlineData("100%,")]
    [InlineData("100%,startend")]
    public void Settings_Line_Invalid_LeavesAuto(string value)
    {
        var cue = CueWith("line:" + value);
        Assert.Null(cue.Line);
        Assert.True(cue.SnapToLines);
        Assert.Equal(VttLineAlign.Start, cue.LineAlign);
    }

    [Fact]
    public void Settings_Line_ASpaceSplitsTheSetting()
    {
        var cue = CueWith("line: 0%");
        Assert.Null(cue.Line);
    }

    [Theory]
    [InlineData("100%,start", VttLineAlign.Start)]
    [InlineData("100%,center", VttLineAlign.Center)]
    [InlineData("100%,end", VttLineAlign.End)]
    public void Settings_Line_Alignment(string value, VttLineAlign expected)
    {
        var cue = CueWith("line:" + value);
        Assert.Equal(100, cue.Line);
        Assert.False(cue.SnapToLines);
        Assert.Equal(expected, cue.LineAlign);
    }

    [Theory]
    [InlineData("0%", 0.0, VttPositionAlign.Auto)]
    [InlineData("100%", 100.0, VttPositionAlign.Auto)]
    [InlineData("1.5%", 1.5, VttPositionAlign.Auto)]
    [InlineData("50%,line-left", 50.0, VttPositionAlign.LineLeft)]
    [InlineData("50%,center", 50.0, VttPositionAlign.Center)]
    [InlineData("50%,line-right", 50.0, VttPositionAlign.LineRight)]
    public void Settings_Position_Valid(string value, double expected, VttPositionAlign align)
    {
        var cue = CueWith("position:" + value);
        Assert.Equal(expected, cue.Position);
        Assert.Equal(align, cue.PositionAlign);
    }

    [Theory]
    [InlineData("-1%")]
    [InlineData("101%")]
    [InlineData("1")]
    [InlineData("%")]
    [InlineData("1%%")]
    [InlineData("50%,start")]
    [InlineData("50%,line-leftx")]
    [InlineData("50%,")]
    public void Settings_Position_Invalid_LeavesAuto(string value)
    {
        var cue = CueWith("position:" + value);
        Assert.Null(cue.Position);
        Assert.Equal(VttPositionAlign.Auto, cue.PositionAlign);
    }

    [Theory]
    [InlineData("0%", 0.0)]
    [InlineData("100%", 100.0)]
    [InlineData("50.5%", 50.5)]
    [InlineData("101%", 100.0)]
    [InlineData("-1%", 100.0)]
    [InlineData("50", 100.0)]
    public void Settings_Size(string value, double expected) => Assert.Equal(expected, CueWith("size:" + value).Size);

    [Theory]
    [InlineData("start", VttAlign.Start)]
    [InlineData("center", VttAlign.Center)]
    [InlineData("end", VttAlign.End)]
    [InlineData("left", VttAlign.Left)]
    [InlineData("right", VttAlign.Right)]
    [InlineData("middle", VttAlign.Center)]
    [InlineData("START", VttAlign.Center)]
    public void Settings_Align(string value, VttAlign expected) => Assert.Equal(expected, CueWith("align:" + value).Align);

    [Theory]
    [InlineData("rl", VttVertical.RightToLeft)]
    [InlineData("lr", VttVertical.LeftToRight)]
    [InlineData("RL", VttVertical.Horizontal)]
    [InlineData("", VttVertical.Horizontal)]
    public void Settings_Vertical(string value, VttVertical expected) => Assert.Equal(expected, CueWith("vertical:" + value).Vertical);

    [Fact]
    public void Settings_Multiple_LastOneWins_AndBadOnesAreSkipped()
    {
        var cue = CueWith("align:start align:end line:5 line:xyz size:50% vertical:rl bogus:1 :x y:");
        Assert.Equal(VttAlign.End, cue.Align);
        Assert.Equal(5, cue.Line);
        Assert.Equal(50, cue.Size);
        Assert.Equal(VttVertical.RightToLeft, cue.Vertical);
    }

    [Fact]
    public void Regions_AreParsedFromTheHeaderAndReferencedBySettings()
    {
        var file = Parse("WEBVTT\n\nREGION\nid:fred\nwidth:40%\nlines:3\nregionanchor:0%,100%\nviewportanchor:10%,90%\nscroll:up\n\nREGION\nid:bill\n\n00:00:00.000 --> 00:00:01.000 region:fred\ntext\n\n00:00:00.000 --> 00:00:01.000 region:nobody\ntext");
        Assert.Equal(2, file.Regions.Count);
        var fred = file.Regions[0];
        Assert.Equal("fred", fred.Id);
        Assert.Equal(40, fred.Width);
        Assert.Equal(3UL, fred.Lines);
        Assert.Equal((0, 100), (fred.RegionAnchorX, fred.RegionAnchorY));
        Assert.Equal((10, 90), (fred.ViewportAnchorX, fred.ViewportAnchorY));
        Assert.Equal("up", fred.Scroll);
        Assert.Same(fred, file.Cues[0].Region);
        Assert.Null(file.Cues[1].Region);
    }

    [Fact]
    public void Regions_SameLineForm_IsOneSettingsLine()
    {
        var file = Parse("WEBVTT\n\nREGION id:fred width:40%\n\n00:00:00.000 --> 00:00:01.000 region:fred\ntext");
        // "REGION id:fred ..." on one line is not the keyword on its own line: the block is discarded.
        Assert.Empty(file.Regions);
        Assert.Null(Assert.Single(file.Cues).Region);
    }

    [Fact]
    public void Regions_LinesAndAnchorsRejectBadValues()
    {
        var file = Parse("WEBVTT\n\nREGION\nid:big\nlines:4294967295\n\nREGION\nid:r\nlines:-1\nlines:1.5\nwidth:101%\nregionanchor:0%\nregionanchor:0%,101%\nviewportanchor:a,b\nscroll:down\n\n00:00:00.000 --> 00:00:01.000\ntext");
        Assert.Equal(2, file.Regions.Count);
        Assert.Equal(uint.MaxValue, file.Regions[0].Lines);
        var region = file.Regions[1];
        Assert.Equal(3UL, region.Lines);
        Assert.Equal(100, region.Width);
        Assert.Equal((0, 100), (region.RegionAnchorX, region.RegionAnchorY));
        Assert.Equal((0, 100), (region.ViewportAnchorX, region.ViewportAnchorY));
        Assert.Equal("", region.Scroll);
    }

    [Fact]
    public void Regions_SurviveALineThatOnlyLooksLikeTimings()
    {
        var file = Parse("WEBVTT\n\nREGION\nid:foo lines:1\n\n-->\nREGION\nid:foo\nlines:2\n-->\n\nREGION\nid:bill\nlines:2\n\nREGION\nREGION\nid:jill\nlines:3\n\nREGION\n--->\nid:jill lines:4\n\nREGION\nid:jack--> lines:5\n\nREGION\nid:jack lines:4\n\n00:00:00.000 --> 00:00:01.000 region:foo\ntext\n\n00:00:00.000 --> 00:00:01.000 region:bill\ntext\n\n00:00:00.000 --> 00:00:01.000 region:jill\ntext\n\n00:00:00.000 --> 00:00:01.000 region:jack\ntext");
        Assert.Equal(["foo:1", "bill:2", "jill:3", "jack:4"], file.Regions.Select(r => r.Id + ":" + r.Lines));
        Assert.Equal(["foo", "bill", "jill", "jack"], file.Cues.Select(c => c.Region?.Id));
    }

    [Fact]
    public void Regions_SettingsSplitOnAnyAsciiWhitespace()
    {
        var file = Parse("WEBVTT\n\nREGION\nid:r\nlines:5\f\f\fregionanchor:40%,20%    viewportanchor:30%,80% \f\tscroll:up\n\n00:00:00.000 --> 00:00:01.000 region:r\ntext");
        var region = Assert.Single(file.Regions);
        Assert.Equal(5UL, region.Lines);
        Assert.Equal((40, 20), (region.RegionAnchorX, region.RegionAnchorY));
        Assert.Equal((30, 80), (region.ViewportAnchorX, region.ViewportAnchorY));
        Assert.Equal("up", region.Scroll);
    }

    [Fact]
    public void Regions_MustComeBeforeTheFirstCue()
    {
        var file = Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\ntext\n\nREGION\nid:late\n\n00:00:00.000 --> 00:00:01.000 region:late\ntext");
        Assert.Empty(file.Regions);
        Assert.Equal(2, file.Cues.Count);
    }

    [Fact]
    public void Stylesheets_AreCollectedBeforeTheFirstCue()
    {
        var file = Parse("WEBVTT\n\nSTYLE\n::cue { color: red }\n\nSTYLE\n::cue(b) {\n  color: blue\n}\n\n00:00:00.000 --> 00:00:01.000\ntext\n\nSTYLE\n::cue { color: green }");
        Assert.Equal(["::cue { color: red }", "::cue(b) {\n  color: blue\n}"], file.Stylesheets);
        Assert.Single(file.Cues);
    }

    [Fact]
    public void Comments_AreDropped()
    {
        var file = Parse("WEBVTT\n\nNOTE this is a comment\n\nNOTE\nanother one\n\n00:00:00.000 --> 00:00:01.000\ntext");
        Assert.Single(file.Cues);
    }

    [Fact]
    public void WhitespaceChars_AsciiWhitespaceSeparatesTimings_NothingElseDoes()
    {
        Assert.Equal("   text0", Assert.Single(Parse("WEBVTT\n\n   00:00:00.000    -->  00:00:01.000 \n   text0").Cues).Text);
        Assert.Single(Parse("WEBVTT\n\n\t\t00:00:00.000\t-->\t00:00:01.000\t\ntext").Cues);
        Assert.Single(Parse("WEBVTT\n\n\f\f00:00:00.000\f-->\f00:00:01.000\f\ntext").Cues);
        Assert.Empty(Parse("WEBVTT\n\n\v00:00:00.000\v-->\v00:00:01.000\ntext").Cues);
        Assert.Empty(Parse("WEBVTT\n\n00:00:00.000\u00A0-->\u00A000:00:01.000\ntext").Cues);
    }

    [Fact]
    public void Timestamp_CanBeParsedOnItsOwn()
    {
        int at = 0;
        Assert.True(WebVttParser.TryParseTimestamp("01:02:03.004", ref at, out double seconds));
        Assert.Equal(3723.004, seconds, 9);
        Assert.Equal(12, at);
        at = 0;
        Assert.True(WebVttParser.TryParseTimestamp("02:03.004", ref at, out seconds));
        Assert.Equal(123.004, seconds, 9);
    }

    /// <summary>
    /// Every WPT file-parsing case, when the checkout is present: the cue count each test asserts must match.
    /// </summary>
    [Fact]
    public void WptFileParsingCorpus_CueCountsMatch()
    {
        string root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        string dir = Path.Combine(root, "webvtt", "parsing", "file-parsing", "support");
        if (!Directory.Exists(dir))
            return;

        var failures = new List<string>();
        int checkedCount = 0;
        foreach (string path in Directory.EnumerateFiles(dir, "*.test"))
        {
            string content = File.ReadAllText(path);
            int split = content.IndexOf("\n===\n", StringComparison.Ordinal);
            if (split < 0)
                continue;
            var count = Regex.Match(content[..split], @"assert_equals\(cues\.length,\s*(\d+)\)");
            if (!count.Success)
                continue;
            string vtt = Unescape(content[(split + 5)..]);
            var file = WebVttParser.Parse(vtt);
            int actual = file?.Cues.Count ?? 0;
            int expected = int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            checkedCount++;
            if (actual != expected)
                failures.Add($"{Path.GetFileName(path)}: expected {expected} cues, got {actual}");
        }

        Assert.True(checkedCount > 20, "the corpus was not found");
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // The generator (tools/build.py) runs the VTT part through Python's unicode_escape.
        static string Unescape(string text) =>
            Regex.Replace(text, @"\\x([0-9a-fA-F]{2})|\\u([0-9a-fA-F]{4})|\\([rntf0\\])", m =>
            {
                if (m.Groups[3].Success)
                {
                    return m.Groups[3].Value switch
                    {
                        "r" => "\r",
                        "n" => "\n",
                        "t" => "\t",
                        "f" => "\f",
                        "0" => "\0",
                        _ => "\\",
                    };
                }

                string hex = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                return ((char)Convert.ToInt32(hex, 16)).ToString();
            });
    }

    private static WebVttFile Parse(string text)
    {
        var file = WebVttParser.Parse(text);
        Assert.NotNull(file);
        return file;
    }

    private static VttCue CueWith(string settings)
    {
        var file = Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000 " + settings + "\ntext");
        return Assert.Single(file.Cues);
    }
}
