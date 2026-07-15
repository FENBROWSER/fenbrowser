using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

[Collection("Engine Tests")]
public sealed class DebugSiteInteractionRunnerTests
{
    [Fact]
    public async Task LocalFormInteraction_RecordsOrdinaryBrowserOutcomeWithoutTextPayload()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            const string nonce = "fen-tooling-private-nonce";
            var result = await DebugSiteInteractionRunner.RunAsync(
                browser,
                new DebugSiteInteractionRequest("#query", nonce, "#submit", 1000),
                static () => 0);

            Assert.Equal("passed", result.Status);
            Assert.True(result.InputTargetFound);
            Assert.True(result.FocusAcquired);
            Assert.True(result.TextAccepted);
            Assert.True(result.SubmitAttempted);
            Assert.True(result.NavigationObserved);
            Assert.True(result.SubmissionOutcomeObserved);
            Assert.True(result.SubmissionOutcomeSettled);
            Assert.Equal("submitted", browser.GetDomRoot()?.QuerySelector("#result")?.TextContent);
            Assert.Contains(result.EventRecords, static record => record.StartsWith("pointerdown|", StringComparison.Ordinal));
            Assert.Contains(result.EventRecords, static record => record.StartsWith("beforeinput|", StringComparison.Ordinal));
            Assert.Contains(result.EventRecords, static record => record.StartsWith("submit|", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.GetType().GetProperties(),
                static property => string.Equals(property.Name, "Text", StringComparison.Ordinal));
            Assert.Equal(nonce.Length, result.TextLength);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public void EventExtraction_IsBoundedAndIgnoresUnmarkedConsoleData()
    {
        var messages = Enumerable.Range(0, DebugSiteInteractionRunner.MaxEventRecords + 10)
            .Select(index => index == 0
                ? "secret page console value"
                : DebugSiteInteractionRunner.EventMarker + "input|capture|INPUT|query|q|false|" + new string('x', 400))
            .ToArray();

        var records = DebugSiteInteractionRunner.ExtractEventRecords(messages);

        Assert.Equal(DebugSiteInteractionRunner.MaxEventRecords, records.Count);
        Assert.All(records, record => Assert.True(record.Length <= DebugSiteInteractionRunner.MaxEventRecordLength));
        Assert.DoesNotContain(records, record => record.Contains("secret", StringComparison.Ordinal));
    }

    private static string GetFixtureUri()
    {
        var fixturePath = Path.Combine(
            FindRepositoryRoot(),
            "FenBrowser.Tests",
            "Fixtures",
            "Interaction",
            "form_acceptance.html");
        return new Uri(fixturePath).AbsoluteUri;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 10; depth++)
        {
            if (File.Exists(Path.Combine(current, "FenBrowser.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not locate the FenBrowser repository root.");
    }
}
