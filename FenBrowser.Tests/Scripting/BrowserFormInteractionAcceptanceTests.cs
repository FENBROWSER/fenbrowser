using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class BrowserFormInteractionAcceptanceTests
{
    [Fact]
    public async Task CheckableInputCheckedProperty_UsesLiveCheckedState()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            Assert.Equal(
                "true|true",
                (await browser.ExecuteScriptAsync(
                    "var input=document.getElementById('include');" +
                    "[String(input.checked),String(input.hasAttribute('checked'))].join('|');"))?.ToString());

            Assert.Equal(
                "false|true",
                (await browser.ExecuteScriptAsync(
                    "var input=document.getElementById('include');input.checked=false;" +
                    "[String(input.checked),String(input.hasAttribute('checked'))].join('|');"))?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task PreventedBeforeInput_DoesNotMutateOrDispatchInput()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            var queryId = await browser.FindElementAsync("css selector", "#query");
            await browser.ClickElementAsync(queryId);
            await browser.ExecuteScriptAsync("globalThis.__cancelBeforeInput=true;globalThis.__events=[];");
            await browser.SendKeysToElementAsync(queryId, "x");

            Assert.Equal(
                string.Empty,
                (await browser.ExecuteScriptAsync("String(document.getElementById('query').value);"))?.ToString());
            Assert.Equal(
                "keydown:query|keypress:query|beforeinput:query|keyup:query",
                (await browser.ExecuteScriptAsync("globalThis.__events.join('|');"))?.ToString());
            Assert.Equal(
                "x|120|x|insertText",
                (await browser.ExecuteScriptAsync(
                    "[globalThis.__lastKey,globalThis.__lastBeforeInput].join('|');"))?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task WebDriverCheckboxClick_TogglesLiveStateWithoutChangingContentAttribute()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            var includeId = await browser.FindElementAsync("css selector", "#include");
            await browser.ExecuteScriptAsync("globalThis.__events=[];");
            await browser.ClickElementAsync(includeId);
            Assert.Equal(
                "false|true",
                (await browser.ExecuteScriptAsync(
                    "var input=document.getElementById('include');" +
                    "[String(input.checked),String(input.hasAttribute('checked'))].join('|');"))?.ToString());

            await browser.ClickElementAsync(includeId);
            Assert.Equal(
                "true|true|input:include|change:include|input:include|change:include",
                (await browser.ExecuteScriptAsync(
                    "var input=document.getElementById('include');" +
                    "[String(input.checked),String(input.hasAttribute('checked'))," +
                    "globalThis.__events.filter(function(x){return x==='input:include'||x==='change:include';}).join('|')].join('|');"))?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task WebDriverClickWithoutInteractablePoint_Throws()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            var hiddenId = await browser.FindElementAsync("css selector", "#hidden-submit");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => browser.ClickElementAsync(hiddenId));
            Assert.Contains("element not interactable", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task WebDriverFindElement_UsesFullCssSelectorSemantics()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            Assert.NotNull(browser.GetDomRoot()?.QuerySelector("#search-form button"));
            Assert.NotNull(browser.GetDomRoot()?.QuerySelector("button[name=submitter]"));
            Assert.NotNull(browser.GetDomRoot()?.QuerySelector("#search-form button[name=submitter]"));

            var submitId = await browser.FindElementAsync(
                "css selector",
                "#search-form button[name=submitter]");

            Assert.NotNull(submitId);
            Assert.Equal(
                "submit",
                (await browser.GetElementAttributeAsync(submitId, "id"))?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task WebDriverClick_NestedFlexTextAreaUsesItsRenderedInViewCenter()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetNestedFlexFixtureUri()));
            await Task.Delay(250);

            var queryId = await browser.FindElementAsync("css selector", "#query");
            var rect = await browser.GetElementRectAsync(queryId);

            Assert.True(rect.Width > 0 && rect.Height > 0);
            Assert.InRange(rect.X, 314, 316);
            await browser.ClickElementAsync(queryId);
            Assert.Equal(queryId, await browser.GetActiveElementAsync());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task WebDriverClickTypeAndPreventedSubmit_UsesOrdinaryInputPipeline()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            var fixtureUri = GetFixtureUri();

            Assert.True(await browser.NavigateAsync(fixtureUri));

            var queryId = await browser.FindElementAsync("css selector", "#query");
            var submitId = await browser.FindElementAsync("css selector", "#submit");
            var queryRect = await browser.GetElementRectAsync(queryId);
            Assert.True(queryRect.Width > 0 && queryRect.Height > 0);

            await browser.ClickElementAsync(queryId);
            Assert.Equal(queryId, await browser.GetActiveElementAsync());

            const string nonce = "fen42";
            await browser.SendKeysToElementAsync(queryId, nonce);
            Assert.Equal(nonce, (await browser.GetElementPropertyAsync(queryId, "value"))?.ToString());

            await browser.ExecuteScriptAsync("globalThis.__preventSubmit=true;");
            await browser.ClickElementAsync(submitId);

            Assert.Equal(fixtureUri, browser.CurrentUri.AbsoluteUri);
            Assert.Equal(
                $"submitted:{nonce}",
                (await browser.ExecuteScriptAsync("document.getElementById('status').textContent;"))?.ToString());
            Assert.Equal(
                $"{nonce}|local-fixture|yes|false|go",
                (await browser.ExecuteScriptAsync(
                    "[__submission.query,__submission.source,__submission.include," +
                    "String(__submission.disabledIncluded),__submission.submitter].join('|');"))?.ToString());

            var events = ((await browser.ExecuteScriptAsync("globalThis.__events.join('|');"))?.ToString() ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries);

            AssertOrdered(
                events,
                "pointerdown:query",
                "mousedown:query",
                "focus:query",
                "pointerup:query",
                "mouseup:query",
                "click:query",
                "keydown:query",
                "beforeinput:query",
                "input:query",
                "keyup:query",
                "change:query",
                "click:submit",
                "submit:search-form");
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    [Fact]
    public async Task WebDriverSubmitWithoutCancellation_NavigatesWithSuccessfulControls()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(GetFixtureUri()));

            var queryId = await browser.FindElementAsync("css selector", "#query");
            var submitId = await browser.FindElementAsync("css selector", "#submit");
            await browser.ClickElementAsync(queryId);
            await browser.SendKeysToElementAsync(queryId, "fen-submit");
            await browser.ExecuteScriptAsync("globalThis.__preventSubmit=false;");
            await browser.ClickElementAsync(submitId);

            Assert.Equal("result.html", Path.GetFileName(browser.CurrentUri.LocalPath));
            Assert.Equal(
                "?q=fen-submit&source=local-fixture&include=yes&submitter=go",
                browser.CurrentUri.Query);
            Assert.DoesNotContain("ignored=", browser.CurrentUri.Query, StringComparison.Ordinal);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
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

    private static string GetNestedFlexFixtureUri()
    {
        var fixturePath = Path.Combine(
            FindRepositoryRoot(),
            "FenBrowser.Tests",
            "Fixtures",
            "Interaction",
            "nested_flex_click.html");
        return new Uri(fixturePath).AbsoluteUri;
    }

    private static void AssertOrdered(IReadOnlyList<string> actual, params string[] expected)
    {
        var searchFrom = 0;
        foreach (var expectedEvent in expected)
        {
            var foundAt = -1;
            for (var index = searchFrom; index < actual.Count; index++)
            {
                if (string.Equals(actual[index], expectedEvent, StringComparison.Ordinal))
                {
                    foundAt = index;
                    break;
                }
            }

            Assert.True(
                foundAt >= 0,
                $"Missing ordered event '{expectedEvent}'. Actual: {string.Join("|", actual)}");
            searchFrom = foundAt + 1;
        }
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
