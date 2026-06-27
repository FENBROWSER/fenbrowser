using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class SkiaDomRendererDeadlineTests
{
    [Fact]
    public void ResolveLayoutDeadlineBudget_ExpandsOnlyForLargeFullDocumentLayout()
    {
        var previous = Environment.GetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS");
        try
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", null);

            Assert.Equal(3000d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(250, fullDocumentLayout: true));
            Assert.Equal(3000d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(2891, fullDocumentLayout: false));

            var budget = SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(2891, fullDocumentLayout: true);
            Assert.Equal(25000d, budget);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", previous);
        }
    }

    [Fact]
    public void ResolveLayoutDeadlineBudget_EnvironmentOverrideWins()
    {
        var previous = Environment.GetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS");
        try
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", "0");
            Assert.Equal(0d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(5000, fullDocumentLayout: true));

            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", "1234");
            Assert.Equal(1234d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(5000, fullDocumentLayout: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", previous);
        }
    }
}
