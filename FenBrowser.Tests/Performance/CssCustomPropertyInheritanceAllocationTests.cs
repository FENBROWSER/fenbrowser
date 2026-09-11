using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

/// <summary>
/// Design-token heavy sites (github.com declares ~2,000 custom properties on :root) must not
/// pay one full copy of that table per element and per inherited text style. CSS Variables
/// Level 1 §2 makes every custom property inherited, so the table is shared copy-on-write.
/// </summary>
public sealed class CssCustomPropertyInheritanceAllocationTests
{
    private const int TokenCount = 2_000;
    private readonly ITestOutputHelper _output;

    public CssCustomPropertyInheritanceAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CssComputed BuildRootWithTokens()
    {
        var declarations = new Dictionary<string, CssDeclaration>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < TokenCount; i++)
        {
            var name = "--token-" + i;
            declarations[name] = new CssDeclaration { Property = name, Value = "#" + i.ToString("x6") };
        }
        return CssLoader.ResolveStyle(new Element("html"), parentCss: null, declarations);
    }

    [Fact]
    public void ResolveStyle_ElementsWithoutOwnVariablesShareTheParentTable()
    {
        var root = BuildRootWithTokens();
        Assert.Equal(TokenCount, root.CustomProperties.Count);

        var plain = new Dictionary<string, CssDeclaration>(StringComparer.OrdinalIgnoreCase)
        {
            ["display"] = new CssDeclaration { Property = "display", Value = "block" }
        };
        var element = new Element("div");

        GC.KeepAlive(CssLoader.ResolveStyle(element, root, plain));

        const int iterations = 2_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        CssComputed child = null;
        for (var i = 0; i < iterations; i++)
        {
            child = CssLoader.ResolveStyle(element, root, plain);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"{iterations:N0} child resolutions under a {TokenCount:N0}-token root allocated {allocated:N0} B ({allocated / iterations:N0} B each).");

        Assert.NotNull(child);
        Assert.Equal("#0007cf", child!.CustomProperties["--token-1999"]);
        Assert.False(child.Map.ContainsKey("--token-1999"));
        // A 2,000-entry dictionary alone is ~70 KB; anything near that per element is the old O(N*P) copy.
        Assert.InRange(allocated / iterations, 0, 8_000);
    }

    [Fact]
    public void InheritedTextStyle_SharesTableAndStaysIsolatedFromLocalWrites()
    {
        var root = BuildRootWithTokens();

        var text = new CssComputed();
        text.InheritCustomProperties(root);
        Assert.Equal("#000000", text.CustomProperties["--token-0"]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var t = new CssComputed();
            t.InheritCustomProperties(root);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"1,000 inherited text styles allocated {allocated:N0} B.");
        Assert.InRange(allocated / 1_000, 0, 4_000); // one bare CssComputed is ~2.2 KB; the 2,000-entry table would be ~70 KB

        // Writing through a borrowing style must not leak into the parent or siblings.
        text.SetVariable("--token-0", "red");
        Assert.Equal("red", text.CustomProperties["--token-0"]);
        Assert.Equal("#000000", root.CustomProperties["--token-0"]);

        var sibling = new CssComputed();
        sibling.InheritCustomProperties(root);
        Assert.Equal("#000000", sibling.CustomProperties["--token-0"]);

        // ...and a later write on the parent must not reach a child that already borrowed.
        root.SetVariable("--token-1", "blue");
        Assert.Equal("blue", root.CustomProperties["--token-1"]);
        Assert.Equal("#000001", sibling.CustomProperties["--token-1"]);
    }

    [Fact]
    public void ChildOverride_WinsOverInheritedWithoutMutatingParent()
    {
        var root = BuildRootWithTokens();
        var overriding = new Dictionary<string, CssDeclaration>(StringComparer.OrdinalIgnoreCase)
        {
            ["--token-5"] = new CssDeclaration { Property = "--token-5", Value = "rebeccapurple" },
            ["color"] = new CssDeclaration { Property = "color", Value = "var(--token-5)" }
        };

        var child = CssLoader.ResolveStyle(new Element("span"), root, overriding);

        Assert.Equal("rebeccapurple", child.CustomProperties["--token-5"]);
        Assert.Equal("rebeccapurple", child.Map["color"]);
        Assert.Equal("#000005", root.CustomProperties["--token-5"]);
        Assert.Equal(TokenCount, child.CustomProperties.Count);
    }

    [Fact]
    public void Clone_IsCopyOnWriteInBothDirections()
    {
        var original = new CssComputed();
        original.SetVariable("--a", "1");

        var clone = original.Clone();
        clone.SetVariable("--a", "2");
        Assert.Equal("1", original.CustomProperties["--a"]);
        Assert.Equal("2", clone.CustomProperties["--a"]);

        original.SetVariable("--b", "3");
        Assert.False(clone.CustomProperties.ContainsKey("--b"));

        original.SetVariable("--a", string.Empty);
        Assert.False(original.CustomProperties.ContainsKey("--a"));
        Assert.Equal("2", clone.CustomProperties["--a"]);
    }
}
