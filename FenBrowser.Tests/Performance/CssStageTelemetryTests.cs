using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
public sealed class CssStageTelemetryTests
{
    [Fact]
    public async Task RenderTelemetryExposesCssStageBreakdown()
    {
        using var engine = new CustomHtmlEngine();

        await engine.RenderAsync(
            """
            <!doctype html>
            <html>
              <head><link rel="stylesheet" href="/site.css"></head>
              <body><div class="target">target</div></body>
            </html>
            """,
            new Uri("https://timing.test/"),
            async _ =>
            {
                await Task.Delay(25);
                return ".target { color: red; }";
            },
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: 320,
            viewportHeight: 200,
            forceJavascript: false);

        var telemetry = Assert.IsType<RenderTelemetrySnapshot>(engine.LastRenderTelemetry);
        Assert.True(telemetry.CssDiscoveryAndFetchMs >= 20);
        Assert.True(telemetry.CssRuleParseMs >= 0);
        Assert.True(telemetry.CssVariableResolutionMs >= 0);
        Assert.True(telemetry.CssCascadeMs >= 0);
        Assert.True(telemetry.CssTotalMs >= telemetry.CssDiscoveryAndFetchMs);
    }
}
