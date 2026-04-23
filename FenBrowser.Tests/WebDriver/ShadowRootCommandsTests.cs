using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Protocol;
using Xunit;

namespace FenBrowser.Tests.WebDriver
{
    public class ShadowRootCommandsTests
    {
        [Fact]
        public async Task GetShadowRoot_Route_ReturnsShadowRootReference()
        {
            var sessionManager = new SessionManager();
            var handler = new CommandHandler(sessionManager)
            {
                Browser = new StubBrowserDriver
                {
                    ShadowRootMap = { ["host-token"] = "shadow-token" }
                }
            };

            var session = sessionManager.CreateSession(new Capabilities());
            var sessionElementId = session.RegisterElement("host-token");
            var router = new CommandRouter();

            var match = router.Match("GET", $"/session/{session.Id}/element/{sessionElementId}/shadow");

            Assert.NotNull(match);

            var response = await handler.ExecuteAsync(match!, null);
            var json = JsonDocument.Parse(response.ToJson());
            var returnedShadowId = json.RootElement.GetProperty("value").GetProperty(ShadowRootReference.Identifier).GetString();

            Assert.False(string.IsNullOrWhiteSpace(returnedShadowId));
            Assert.Equal("shadow-token", session.GetElement(returnedShadowId!));
        }

        [Fact]
        public async Task FindElementFromShadowRoot_Route_UsesShadowRootAsParentContext()
        {
            var sessionManager = new SessionManager();
            var handler = new CommandHandler(sessionManager)
            {
                Browser = new StubBrowserDriver
                {
                    ShadowRootMap = { ["host-token"] = "shadow-token" },
                    FindElementResults = { [("css selector", "#inside", "shadow-token")] = "child-token" }
                }
            };

            var session = sessionManager.CreateSession(new Capabilities());
            var shadowSessionId = session.RegisterElement("shadow-token");
            var router = new CommandRouter();
            var match = router.Match("POST", $"/session/{session.Id}/shadow/{shadowSessionId}/element");

            Assert.NotNull(match);

            using var body = JsonDocument.Parse("{\"using\":\"css selector\",\"value\":\"#inside\"}");
            var response = await handler.ExecuteAsync(match!, body.RootElement.GetRawText());
            var json = JsonDocument.Parse(response.ToJson());
            var returnedElementId = json.RootElement.GetProperty("value").GetProperty(ElementReference.Identifier).GetString();

            Assert.False(string.IsNullOrWhiteSpace(returnedElementId));
            Assert.Equal("child-token", session.GetElement(returnedElementId!));
        }

        private sealed class StubBrowserDriver : IBrowserDriver
        {
            public Dictionary<string, string> ShadowRootMap { get; } = new(StringComparer.Ordinal);
            public Dictionary<(string Strategy, string Selector, string Parent), string> FindElementResults { get; } = new();

            public Task NavigateAsync(string url) => Task.CompletedTask;
            public Task<string> GetCurrentUrlAsync() => Task.FromResult("about:blank");
            public Task<string> GetTitleAsync() => Task.FromResult(string.Empty);
            public Task<string> GetWindowHandleAsync() => Task.FromResult("window-1");
            public Task<IReadOnlyList<string>> GetWindowHandlesAsync() => Task.FromResult((IReadOnlyList<string>)new[] { "window-1" });
            public Task CloseWindowAsync() => Task.CompletedTask;
            public Task GoBackAsync() => Task.CompletedTask;
            public Task GoForwardAsync() => Task.CompletedTask;
            public Task RefreshAsync() => Task.CompletedTask;

            public Task<object> FindElementAsync(string strategy, string selector, object parentElement = null)
            {
                var parent = parentElement as string;
                return Task.FromResult<object>(FindElementResults.TryGetValue((strategy, selector, parent), out var result) ? result : null);
            }

            public Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null)
            {
                var result = FindElementResults.TryGetValue((strategy, selector, parentElement as string), out var element)
                    ? new object[] { element }
                    : Array.Empty<object>();
                return Task.FromResult(result);
            }

            public Task<object> GetActiveElementAsync() => Task.FromResult<object>(null);
            public Task<object> GetShadowRootAsync(object element)
            {
                var hostToken = element as string;
                return Task.FromResult<object>(hostToken != null && ShadowRootMap.TryGetValue(hostToken, out var shadowToken) ? shadowToken : null);
            }

            public Task<bool> IsElementSelectedAsync(object element) => Task.FromResult(false);
            public Task<object> GetElementPropertyAsync(object element, string name) => Task.FromResult<object>(null);
            public Task<string> GetElementCssValueAsync(object element, string propertyName) => Task.FromResult(string.Empty);
            public Task<string> GetElementTextAsync(object element) => Task.FromResult(string.Empty);
            public Task<string> GetElementTagNameAsync(object element) => Task.FromResult(string.Empty);
            public Task<WdElementRect> GetElementRectAsync(object element) => Task.FromResult(new WdElementRect());
            public Task<bool> IsElementEnabledAsync(object element) => Task.FromResult(true);
            public Task<string> GetElementComputedRoleAsync(object element) => Task.FromResult(string.Empty);
            public Task<string> GetElementComputedLabelAsync(object element) => Task.FromResult(string.Empty);
            public Task ClickElementAsync(object element) => Task.CompletedTask;
            public Task ClearElementAsync(object element) => Task.CompletedTask;
            public Task SendKeysAsync(object element, string text) => Task.CompletedTask;
            public Task<string> GetElementAttributeAsync(object element, string name) => Task.FromResult(string.Empty);
            public Task<string> GetPageSourceAsync() => Task.FromResult("<html></html>");
            public Task<object> ExecuteScriptAsync(string script, object[] args) => Task.FromResult<object>(null);
            public Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout) => Task.FromResult<object>(null);
            public Task<string> TakeScreenshotAsync() => Task.FromResult(string.Empty);
            public Task<string> TakeElementScreenshotAsync(object element) => Task.FromResult(string.Empty);
            public Task<string> PrintPageAsync(WdPrintOptions options) => Task.FromResult(string.Empty);
            public (int x, int y, int width, int height) GetWindowRect() => (0, 0, 1024, 768);
            public void SetWindowRect(int? x, int? y, int? width, int? height) { }
            public (int x, int y, int width, int height) MaximizeWindow() => (0, 0, 1024, 768);
            public (int x, int y, int width, int height) MinimizeWindow() => (0, 0, 1024, 768);
            public (int x, int y, int width, int height) FullscreenWindow() => (0, 0, 1024, 768);
            public Task<string> NewWindowAsync(string typeHint) => Task.FromResult("window-2");
            public Task SwitchToWindowAsync(string windowHandle) => Task.CompletedTask;
            public Task SwitchToFrameAsync(object frameReference) => Task.CompletedTask;
            public Task SwitchToParentFrameAsync() => Task.CompletedTask;
            public Task<IReadOnlyList<WdCookie>> GetAllCookiesAsync() => Task.FromResult((IReadOnlyList<WdCookie>)Array.Empty<WdCookie>());
            public Task<WdCookie> GetNamedCookieAsync(string name) => Task.FromResult<WdCookie>(null);
            public Task AddCookieAsync(WdCookie cookie) => Task.CompletedTask;
            public Task DeleteCookieAsync(string name) => Task.CompletedTask;
            public Task DeleteAllCookiesAsync() => Task.CompletedTask;
            public Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions) => Task.CompletedTask;
            public Task ReleaseActionsAsync() => Task.CompletedTask;
            public Task<bool> HasAlertAsync() => Task.FromResult(false);
            public Task DismissAlertAsync() => Task.CompletedTask;
            public Task AcceptAlertAsync() => Task.CompletedTask;
            public Task<string> GetAlertTextAsync() => Task.FromResult(string.Empty);
            public Task SendAlertTextAsync(string text) => Task.CompletedTask;
            public bool HasValidCurrentBrowsingContext() => true;
        }
    }
}
