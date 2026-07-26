using System;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
public sealed class CssNavigationCacheLifetimeTests
{
    [Fact]
    public async Task DocumentCleanupPreservesImmutableParsedRulesForWarmNavigation()
    {
        CssLoader.ClearCaches();
        const string html = """
            <!doctype html>
            <html><head><style>.target { color: red; }</style></head>
            <body><div class="target">target</div></body></html>
            """;
        var baseUri = new Uri("https://cache.test/");
        var firstRoot = new HtmlParser(html, baseUri).Parse().DocumentElement;

        await CssLoader.ComputeAsync(firstRoot, baseUri, _ => Task.FromResult(string.Empty));
        var populatedCount = CssLoader.ParsedRuleCacheCount;
        Assert.True(populatedCount > 0);

        CssLoader.ClearDocumentScopedCaches(firstRoot);
        Assert.Equal(populatedCount, CssLoader.ParsedRuleCacheCount);

        var secondRoot = new HtmlParser(html, baseUri).Parse().DocumentElement;
        await CssLoader.ComputeAsync(secondRoot, baseUri, _ => Task.FromResult(string.Empty));

        Assert.Equal(populatedCount, CssLoader.ParsedRuleCacheCount);
    }

    [Fact]
    public void DocumentCleanupDropsDescriptorsButPreservesLoadedTypefaces()
    {
        FontRegistry.Clear();
        var document = new HtmlParser(
            "<!doctype html><html><body></body></html>",
            new Uri("https://fonts.test/")).Parse();

        FontRegistry.Register("ReusableTypeface", "Arial");
        FontRegistry.RegisterFontFace(new FontRegistry.FontFaceDescriptor
        {
            Family = "DocumentDescriptor",
            OwnerDocument = document
        });

        Assert.True(FontRegistry.IsRegistered("ReusableTypeface"));
        Assert.True(FontRegistry.IsRegistered("DocumentDescriptor"));

        FontRegistry.ClearDocument(document);

        Assert.True(FontRegistry.IsRegistered("ReusableTypeface"));
        Assert.False(FontRegistry.IsRegistered("DocumentDescriptor"));
    }
}
