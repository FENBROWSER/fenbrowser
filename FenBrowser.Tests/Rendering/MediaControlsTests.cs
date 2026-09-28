using FenBrowser.Core.Accessibility;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// HTML §4.8.13: the user agent controls. One geometry serves paint, hit testing and the
/// accessibility tree, so these pin down that geometry and what each part does.
/// </summary>
public sealed class MediaControlsTests
{
    [Fact]
    public void VideoControlsSitAlongTheBottomEdge()
    {
        var box = new SKRect(10, 20, 410, 320);
        var geometry = MediaControls.Layout(box, isVideo: true);

        Assert.False(geometry.IsEmpty);
        Assert.Equal(box.Bottom, geometry.Bar.Bottom);
        Assert.Equal(MediaControls.BarHeight, geometry.Bar.Height);
        Assert.Equal(box.Left, geometry.Bar.Left);
        Assert.Equal(box.Right, geometry.Bar.Right);
        Assert.True(geometry.PlayButton.Left < geometry.Timeline.Left);
        Assert.True(geometry.Timeline.Right < geometry.MuteButton.Left);
        Assert.False(float.IsNaN(geometry.TimeTextOrigin.X), "a 400px bar has room for the time display");
    }

    [Fact]
    public void AudioControlsFillTheElement()
    {
        var box = new SKRect(0, 0, 300, 54);
        var geometry = MediaControls.Layout(box, isVideo: false);
        Assert.Equal(box, geometry.Bar);
    }

    [Fact]
    public void NarrowBarsDropTheTimeDisplay()
    {
        var geometry = MediaControls.Layout(new SKRect(0, 0, 120, 40), isVideo: true);
        Assert.True(float.IsNaN(geometry.TimeTextOrigin.X));
        Assert.True(geometry.Timeline.Width > 0, "the timeline still fits");
    }

    [Fact]
    public void HitTestMapsPointsToActions()
    {
        var geometry = MediaControls.Layout(new SKRect(0, 0, 400, 300), isVideo: true);

        Assert.Equal(MediaControlAction.TogglePlay, MediaControls.HitTest(geometry, geometry.PlayButton.MidX, geometry.PlayButton.MidY, out _));
        Assert.Equal(MediaControlAction.ToggleMute, MediaControls.HitTest(geometry, geometry.MuteButton.MidX, geometry.MuteButton.MidY, out _));
        Assert.Equal(MediaControlAction.None, MediaControls.HitTest(geometry, 200, 100, out _));

        var quarter = geometry.Timeline.Left + geometry.Timeline.Width * 0.25f;
        Assert.Equal(MediaControlAction.Seek, MediaControls.HitTest(geometry, quarter, geometry.Timeline.MidY, out var fraction));
        Assert.Equal(0.25, fraction, 3);
        Assert.Equal(MediaControlAction.Seek, MediaControls.HitTest(geometry, geometry.Timeline.Right + 2, geometry.Timeline.MidY, out fraction));
        Assert.Equal(1.0, fraction, 3);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725.5, "1:02:05")]
    [InlineData(double.NaN, "0:00")]
    [InlineData(double.PositiveInfinity, "0:00")]
    public void TimeDisplayFormat(double seconds, string expected) => Assert.Equal(expected, MediaControls.FormatTime(seconds));

    [Fact]
    public void PaintNodesCoverEveryControlAndReflectTheState()
    {
        var doc = new HtmlParser("<video controls></video>", new Uri("https://example.test/")).Parse();
        var video = doc.QuerySelector("video");
        var nodes = new List<PaintNodeBase>();
        var state = new MediaControlsState(Paused: false, Ended: false, CurrentTime: 30, Duration: 120, Muted: false, Volume: 1, BufferedEnd: 60);
        MediaControls.BuildPaintNodes(video, new SKRect(0, 0, 400, 300), isVideo: true, state, nodes);

        Assert.All(nodes, node => Assert.Same(video, node.SourceNode));
        var text = Assert.Single(nodes.OfType<TextPaintNode>());
        Assert.Equal("0:30 / 2:00", text.FallbackText);
        // Bar, track, buffered, played, knob backgrounds; play and mute icons.
        Assert.Equal(5, nodes.OfType<BackgroundPaintNode>().Count());
        Assert.Equal(2, nodes.OfType<CustomPaintNode>().Count());

        var geometry = MediaControls.Layout(new SKRect(0, 0, 400, 300), isVideo: true);
        var backgrounds = nodes.OfType<BackgroundPaintNode>().ToList();
        Assert.Equal(geometry.Bar, backgrounds[0].Bounds);
        Assert.Equal(geometry.Timeline.Left + geometry.Timeline.Width * 0.5f, backgrounds[2].Bounds.Right, 2); // buffered to 50 %
        Assert.Equal(geometry.Timeline.Left + geometry.Timeline.Width * 0.25f, backgrounds[3].Bounds.Right, 2); // played to 25 %
    }

    [Fact]
    public void TheHoveredButtonGetsAHighlightAndTheTimelineKnobGrows()
    {
        var doc = new HtmlParser("<video controls></video>", new Uri("https://example.test/")).Parse();
        var video = doc.QuerySelector("video");
        var box = new SKRect(0, 0, 400, 300);
        var geometry = MediaControls.Layout(box, isVideo: true);
        var state = new MediaControlsState(Paused: true, Ended: false, CurrentTime: 30, Duration: 120, Muted: false, Volume: 1, BufferedEnd: 60);

        List<PaintNodeBase> Paint()
        {
            var nodes = new List<PaintNodeBase>();
            MediaControls.BuildPaintNodes(video, box, isVideo: true, state, nodes);
            return nodes;
        }

        Assert.True(MediaControls.SetHovered(video, MediaControlAction.TogglePlay));
        Assert.False(MediaControls.SetHovered(video, MediaControlAction.TogglePlay));
        var hovered = Paint();
        // The highlight disc sits behind the play icon: a third custom node, on the play button.
        Assert.Equal(3, hovered.OfType<CustomPaintNode>().Count());
        Assert.Equal(geometry.PlayButton, hovered.OfType<CustomPaintNode>().First().Bounds);

        Assert.True(MediaControls.SetHovered(video, MediaControlAction.None));
        var knob = Paint().OfType<BackgroundPaintNode>().Last().Bounds.Width;
        MediaControls.SetHovered(video, MediaControlAction.Seek);
        Assert.True(Paint().OfType<BackgroundPaintNode>().Last().Bounds.Width > knob);
        MediaControls.SetHovered(video, MediaControlAction.None);
    }

    [Fact]
    public void AccessibilityTreeExposesTheControls()
    {
        var previous = AccessibilityTreeBuilder.MediaControlsProvider;
        try
        {
            AccessibilityTreeBuilder.MediaControlsProvider = _ => new MediaControlsAccessibility(Paused: false, Muted: true, CurrentTime: 65, Duration: 200);
            var doc = new HtmlParser("<body><video controls></video><video></video></body>", new Uri("https://example.test/")).Parse();
            var tree = AccessibilityTreeBuilder.Build(doc);

            var buttons = Flatten(tree).Where(n => n.Role == AriaRole.Button).ToList();
            var sliders = Flatten(tree).Where(n => n.Role == AriaRole.Slider).ToList();
            Assert.Equal(["Pause", "Unmute"], buttons.Select(b => b.Name));
            var seek = Assert.Single(sliders);
            Assert.Equal("Seek", seek.Name);
            Assert.Equal("65", seek.States["aria-valuenow"]);
            Assert.Equal("200", seek.States["aria-valuemax"]);
            Assert.Equal("1:05 of 3:20", seek.States["aria-valuetext"]);
            Assert.All(buttons.Concat(sliders), n => Assert.Equal("video", n.SourceElement.LocalName));
        }
        finally
        {
            AccessibilityTreeBuilder.MediaControlsProvider = previous;
        }
    }

    private static IEnumerable<AccessibilityNode> Flatten(AccessibilityNode node)
    {
        if (node == null)
        {
            yield break;
        }

        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
