using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class CssStyleResolutionAllocationTests
{
    private readonly ITestOutputHelper _output;

    public CssStyleResolutionAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ResolveStyle_OrdinaryDeclarationsAvoidUnusedVariableTracking()
    {
        var element = new Element("div");
        var declarations = new Dictionary<string, CssDeclaration>(StringComparer.OrdinalIgnoreCase)
        {
            ["display"] = new CssDeclaration { Property = "display", Value = "block" },
            ["color"] = new CssDeclaration { Property = "color", Value = "rgb(20, 30, 40)" },
            ["width"] = new CssDeclaration { Property = "width", Value = "320px" },
            ["margin"] = new CssDeclaration { Property = "margin", Value = "8px 12px" }
        };

        GC.KeepAlive(CssLoader.ResolveStyle(element, parentCss: null, declarations));

        CssComputed resolved = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            resolved = CssLoader.ResolveStyle(element, parentCss: null, declarations);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"1,000 ordinary style resolutions allocated {allocated:N0} B.");

        Assert.NotNull(resolved);
        Assert.Equal("block", resolved!.Display);
        Assert.Equal(320, resolved.Width);
        Assert.Equal(8, resolved.Margin.Top);
        Assert.Equal(12, resolved.Margin.Right);
        Assert.InRange(allocated, 1, 6_400_000);
    }

    [Fact]
    public void ResolveStyle_VariableDeclarationsStillResolveCustomProperties()
    {
        var element = new Element("div");
        var declarations = new Dictionary<string, CssDeclaration>(StringComparer.OrdinalIgnoreCase)
        {
            ["--accent"] = new CssDeclaration { Property = "--accent", Value = "rgb(20, 30, 40)" },
            ["color"] = new CssDeclaration { Property = "color", Value = "var(--accent)" }
        };

        var resolved = CssLoader.ResolveStyle(element, parentCss: null, declarations);

        Assert.Equal("rgb(20, 30, 40)", resolved.CustomProperties["--accent"]);
        Assert.Equal("rgb(20, 30, 40)", resolved.Map["color"]);
    }
}
