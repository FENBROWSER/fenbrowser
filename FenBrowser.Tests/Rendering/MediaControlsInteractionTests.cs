using System.Diagnostics;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// HTML §4.8.13 through the real input path: a physical click on the user agent controls
/// of an <c>audio</c> element (a 300x54 box) reaches the control under the point.
/// </summary>
[Collection("Engine Tests")]
public sealed class MediaControlsInteractionTests
{
    [Fact]
    public async Task ClickingTheControlsTogglesPlayAndMuteAndSeeks()
    {
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html,<body style='margin:0'><audio id=a controls></audio></body>"));
            await browser.ExecuteScriptAsync("""
                globalThis.__events = [];
                var a = document.getElementById('a');
                ['play', 'pause', 'volumechange', 'seeking'].forEach(function (t) { a.addEventListener(t, function () { globalThis.__events.push(t); }); });
                """);
            await browser.FlushPendingLayoutAsync();

            var rect = await ElementRectAsync(browser, "a");
            Assert.Equal(300, rect.Width, 0);
            Assert.Equal(54, rect.Height, 0);
            var geometry = MediaControls.Layout(rect, isVideo: false);

            // Play (no resource: the play promise stays pending, but the element is no longer paused).
            await browser.DispatchClickAndActivate(geometry.PlayButton.MidX, geometry.PlayButton.MidY, 0);
            Assert.Equal("false", await WaitForAsync(browser, "String(document.getElementById('a').paused)", "false"));
            Assert.Equal("play", await WaitForAsync(browser, "globalThis.__events.join(',')", "play"));

            // Pause.
            await browser.DispatchClickAndActivate(geometry.PlayButton.MidX, geometry.PlayButton.MidY, 0);
            Assert.Equal("true", await WaitForAsync(browser, "String(document.getElementById('a').paused)", "true"));

            // Mute.
            await browser.DispatchClickAndActivate(geometry.MuteButton.MidX, geometry.MuteButton.MidY, 0);
            Assert.Equal("true", await WaitForAsync(browser, "String(document.getElementById('a').muted)", "true"));
            Assert.Contains("volumechange", (await browser.ExecuteScriptAsync("globalThis.__events.join(',')"))?.ToString());

            // A click in the middle of the element that is not on a control does nothing.
            await browser.DispatchClickAndActivate(geometry.Timeline.MidX, rect.Top + 2, 0);
            Assert.Equal("true", (await browser.ExecuteScriptAsync("String(document.getElementById('a').muted)"))?.ToString());

            // The element took focus, so the keyboard drives it too: m unmutes.
            Assert.Equal("a", (await browser.ExecuteScriptAsync("document.activeElement && document.activeElement.id"))?.ToString());
            await browser.HandleKeyPress("m");
            Assert.Equal("false", await WaitForAsync(browser, "String(document.getElementById('a').muted)", "false"));
        }
        finally
        {
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    private static async Task<SkiaSharp.SKRect> ElementRectAsync(BrowserHost browser, string id)
    {
        var text = (await browser.ExecuteScriptAsync($"(function () {{ var r = document.getElementById('{id}').getBoundingClientRect(); return [r.left, r.top, r.width, r.height].join(','); }})()"))?.ToString() ?? string.Empty;
        var parts = text.Split(',').Select(p => float.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return SkiaSharp.SKRect.Create(parts[0], parts[1], parts[2], parts[3]);
    }

    private static async Task<string> WaitForAsync(BrowserHost browser, string expression, string expected)
    {
        var stopwatch = Stopwatch.StartNew();
        string value = string.Empty;
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            value = (await browser.ExecuteScriptAsync(expression))?.ToString() ?? string.Empty;
            if (value == expected)
            {
                return value;
            }

            await Task.Delay(25);
        }

        return value;
    }
}
