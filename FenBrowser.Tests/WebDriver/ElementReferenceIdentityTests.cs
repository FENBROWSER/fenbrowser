using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Protocol;
using Xunit;

namespace FenBrowser.Tests.WebDriver
{
    /// <summary>
    /// Regression tests for WD-001: distinct elements must never be aliased onto
    /// the same WebDriver element reference by fingerprint equivalence.
    /// </summary>
    public class ElementReferenceIdentityTests
    {
        [Fact]
        public async Task IdenticalSiblings_GetDistinctElementReferences()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var driver = new IdentityStubDriver();
            driver.QueueFind("native-A");
            driver.QueueFind("native-B");

            var handler = new CommandHandler(manager) { Browser = driver };
            var router = new CommandRouter();

            var first = await FindElementAsync(handler, router, session.Id);
            var second = await FindElementAsync(handler, router, session.Id);

            Assert.NotEqual(first, second);
            Assert.Same("native-A", session.GetElement(first));
            Assert.Same("native-B", session.GetElement(second));
        }

        [Fact]
        public async Task SameNativeElement_Reused_AcrossConsecutiveFinds()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var driver = new IdentityStubDriver();
            driver.QueueFind("native-A");
            driver.QueueFind("native-A");

            var handler = new CommandHandler(manager) { Browser = driver };
            var router = new CommandRouter();

            var first = await FindElementAsync(handler, router, session.Id);
            var second = await FindElementAsync(handler, router, session.Id);

            Assert.Equal(first, second);
            Assert.Same("native-A", session.GetElement(first));
        }

        private static async Task<string> FindElementAsync(
            CommandHandler handler,
            CommandRouter router,
            string sessionId)
        {
            var match = router.Match("POST", $"/session/{sessionId}/element");
            const string body = """{"using":"css selector","value":"button"}""";

            var response = await handler.ExecuteAsync(match, body);
            using var json = JsonDocument.Parse(response.ToJson());

            var value = json.RootElement.GetProperty("value");
            Assert.True(value.TryGetProperty(ElementReference.Identifier, out var elementId),
                $"Expected element reference in response: {response.ToJson()}");
            return elementId.GetString();
        }

        private sealed class IdentityStubDriver : IBrowserDriver
        {
            private readonly Queue<object> _findResults = new();

            public void QueueFind(string nativeId)
            {
                _findResults.Enqueue(nativeId);
            }

            public Task<object> FindElementAsync(string strategy, string selector, object parentElement = null)
            {
                return Task.FromResult(_findResults.Count > 0 ? _findResults.Dequeue() : null);
            }

            public Task<string> GetElementTextAsync(object element) => Task.FromResult("OK");

            public Task<string> GetElementTagNameAsync(object element) => Task.FromResult("button");

            public Task<string> GetElementAttributeAsync(object element, string name) => Task.FromResult(string.Empty);

            private static NotSupportedException Unsupported(string member) =>
                new NotSupportedException($"{member} is not used by this stub.");

            public Task NavigateAsync(string url) => throw Unsupported(nameof(NavigateAsync));
            public Task<string> GetCurrentUrlAsync() => throw Unsupported(nameof(GetCurrentUrlAsync));
            public Task<string> GetTitleAsync() => throw Unsupported(nameof(GetTitleAsync));
            public Task<string> GetWindowHandleAsync() => Task.FromResult("win-1");
            public Task<IReadOnlyList<string>> GetWindowHandlesAsync() =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "win-1" });
            public Task CloseWindowAsync() => throw Unsupported(nameof(CloseWindowAsync));
            public Task GoBackAsync() => throw Unsupported(nameof(GoBackAsync));
            public Task GoForwardAsync() => throw Unsupported(nameof(GoForwardAsync));
            public Task RefreshAsync() => throw Unsupported(nameof(RefreshAsync));
            public Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null) => throw Unsupported(nameof(FindElementsAsync));
            public Task<object> GetActiveElementAsync() => throw Unsupported(nameof(GetActiveElementAsync));
            public Task<object> GetShadowRootAsync(object element) => throw Unsupported(nameof(GetShadowRootAsync));
            public Task<bool> IsElementSelectedAsync(object element) => throw Unsupported(nameof(IsElementSelectedAsync));
            public Task<object> GetElementPropertyAsync(object element, string name) => throw Unsupported(nameof(GetElementPropertyAsync));
            public Task<string> GetElementCssValueAsync(object element, string propertyName) => throw Unsupported(nameof(GetElementCssValueAsync));
            public Task<WdElementRect> GetElementRectAsync(object element) => throw Unsupported(nameof(GetElementRectAsync));
            public Task<bool> IsElementEnabledAsync(object element) => throw Unsupported(nameof(IsElementEnabledAsync));
            public Task<string> GetElementComputedRoleAsync(object element) => throw Unsupported(nameof(GetElementComputedRoleAsync));
            public Task<string> GetElementComputedLabelAsync(object element) => throw Unsupported(nameof(GetElementComputedLabelAsync));
            public Task ClickElementAsync(object element) => throw Unsupported(nameof(ClickElementAsync));
            public Task ClearElementAsync(object element) => throw Unsupported(nameof(ClearElementAsync));
            public Task SendKeysAsync(object element, string text, bool strictFileInteractability = false) => throw Unsupported(nameof(SendKeysAsync));
            public Task<string> GetPageSourceAsync() => throw Unsupported(nameof(GetPageSourceAsync));
            public Task<object> ExecuteScriptAsync(string script, object[] args) => throw Unsupported(nameof(ExecuteScriptAsync));
            public Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout) => throw Unsupported(nameof(ExecuteAsyncScriptAsync));
            public Task<string> TakeScreenshotAsync() => throw Unsupported(nameof(TakeScreenshotAsync));
            public Task<string> TakeElementScreenshotAsync(object element) => throw Unsupported(nameof(TakeElementScreenshotAsync));
            public Task<string> PrintPageAsync(WdPrintOptions options) => throw Unsupported(nameof(PrintPageAsync));
            public (int x, int y, int width, int height) GetWindowRect() => throw Unsupported(nameof(GetWindowRect));
            public void SetWindowRect(int? x, int? y, int? width, int? height) => throw Unsupported(nameof(SetWindowRect));
            public (int x, int y, int width, int height) MaximizeWindow() => throw Unsupported(nameof(MaximizeWindow));
            public (int x, int y, int width, int height) MinimizeWindow() => throw Unsupported(nameof(MinimizeWindow));
            public (int x, int y, int width, int height) FullscreenWindow() => throw Unsupported(nameof(FullscreenWindow));
            public Task<string> NewWindowAsync(string typeHint) => throw Unsupported(nameof(NewWindowAsync));
            public Task SwitchToWindowAsync(string windowHandle) => throw Unsupported(nameof(SwitchToWindowAsync));
            public Task SwitchToFrameAsync(object frameReference) => throw Unsupported(nameof(SwitchToFrameAsync));
            public Task SwitchToParentFrameAsync() => throw Unsupported(nameof(SwitchToParentFrameAsync));
            public Task<IReadOnlyList<WdCookie>> GetAllCookiesAsync() => throw Unsupported(nameof(GetAllCookiesAsync));
            public Task<WdCookie> GetNamedCookieAsync(string name) => throw Unsupported(nameof(GetNamedCookieAsync));
            public Task AddCookieAsync(WdCookie cookie) => throw Unsupported(nameof(AddCookieAsync));
            public Task DeleteCookieAsync(string name) => throw Unsupported(nameof(DeleteCookieAsync));
            public Task DeleteAllCookiesAsync() => throw Unsupported(nameof(DeleteAllCookiesAsync));
            public Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions) => throw Unsupported(nameof(PerformActionsAsync));
            public Task ReleaseActionsAsync() => throw Unsupported(nameof(ReleaseActionsAsync));
            public Task<bool> HasAlertAsync() => Task.FromResult(false);
            public Task DismissAlertAsync() => throw Unsupported(nameof(DismissAlertAsync));
            public Task AcceptAlertAsync() => throw Unsupported(nameof(AcceptAlertAsync));
            public Task<string> GetAlertTextAsync() => throw Unsupported(nameof(GetAlertTextAsync));
            public Task SendAlertTextAsync(string text) => throw Unsupported(nameof(SendAlertTextAsync));
            public bool HasValidCurrentBrowsingContext() => true;
        }
    }
}
