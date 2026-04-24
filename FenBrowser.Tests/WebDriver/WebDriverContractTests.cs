using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Security;
using Xunit;

namespace FenBrowser.Tests.WebDriver
{
    public class WebDriverContractTests
    {
        [Fact]
        public void CommandRouter_NormalizesPaths_AndDecodesRouteParameters()
        {
            var router = new CommandRouter();

            var match = router.Match("get", "/session/abc/element/hello%20world/attribute/data-name/?unused=true");

            Assert.NotNull(match);
            Assert.Equal("GetElementAttribute", match.Command);
            Assert.Equal("abc", match.GetSessionId());
            Assert.Equal("hello world", match.GetElementId());
            Assert.Equal("data-name", match.Parameters["name"]);
        }

        [Fact]
        public void SessionCommands_SetTimeouts_RejectsNegativeValues()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse("""{"script":-1}""");

            var ex = Assert.Throws<WebDriverException>(() => commands.SetTimeouts(session.Id, body.RootElement.Clone()));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
        }

        [Fact]
        public void Capabilities_Merge_RejectsInvalidPageLoadStrategy()
        {
            var ex = Assert.Throws<WebDriverException>(() => Capabilities.Merge(new Capabilities
            {
                PageLoadStrategy = "fastest"
            }));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
        }

        [Fact]
        public async Task ScriptCommands_ResolveElementArguments_AndSerializeNestedElementResults()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var cachedElement = new StubElement();
            var elementId = session.RegisterElement(cachedElement);
            var handler = new CommandHandler(manager)
            {
                Browser = new ScriptStubBrowserDriver()
            };

            var router = new CommandRouter();
            var match = router.Match("POST", $"/session/{session.Id}/execute/sync");
            var body = $$"""
                {
                  "script":"return arguments;",
                  "args":[{"{{ElementReference.Identifier}}":"{{elementId}}"}]
                }
                """;

            var response = await handler.ExecuteAsync(match, body);
            using var json = JsonDocument.Parse(response.ToJson());
            var serializedElement = json.RootElement
                .GetProperty("value")[0]
                .GetProperty(ElementReference.Identifier)
                .GetString();

            Assert.False(string.IsNullOrWhiteSpace(serializedElement));
            Assert.Same(cachedElement, ((ScriptStubBrowserDriver)handler.Browser).LastArgs[0]);
            Assert.Same(cachedElement, session.GetElement(serializedElement));
        }

        [Fact]
        public async Task NavigateTo_WaitsForNavigationCommitBeforeReturning()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities
            {
                Timeouts = new Timeouts
                {
                    PageLoad = 500
                }
            });

            var browser = new ScriptStubBrowserDriver();
            browser.ConfigureDeferredNavigationCommit("https://example.com/", readsBeforeCommit: 2);

            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };

            var router = new CommandRouter();
            var navigateMatch = router.Match("POST", $"/session/{session.Id}/url");
            var navigateBody = """{"url":"https://example.com/"}""";

            await handler.ExecuteAsync(navigateMatch, navigateBody);

            Assert.Equal(1, browser.NavigateCallCount);
            Assert.Equal("https://example.com/", await browser.GetCurrentUrlAsync());
        }

        [Fact]
        public async Task NavigateTo_ThrowsTimeout_WhenNavigationNeverCommits()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities
            {
                Timeouts = new Timeouts
                {
                    PageLoad = 100
                }
            });

            var browser = new ScriptStubBrowserDriver();
            browser.ConfigureDeferredNavigationCommit("https://example.com/", readsBeforeCommit: int.MaxValue);

            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };

            var router = new CommandRouter();
            var navigateMatch = router.Match("POST", $"/session/{session.Id}/url");
            var navigateBody = """{"url":"https://example.com/"}""";

            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(navigateMatch, navigateBody));

            Assert.Equal(ErrorCodes.Timeout, ex.ErrorCode);
        }

        [Fact]
        public async Task ElementCommand_RejectsCrossSessionElementReference()
        {
            var manager = new SessionManager();
            var sessionA = manager.CreateSession(new Capabilities());
            var sessionB = manager.CreateSession(new Capabilities());
            var foreignElementId = sessionA.RegisterElement(new StubElement());
            var handler = new CommandHandler(manager)
            {
                Browser = new ScriptStubBrowserDriver()
            };

            var router = new CommandRouter();
            var match = router.Match("GET", $"/session/{sessionB.Id}/element/{foreignElementId}/text");

            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, null));

            Assert.Equal(ErrorCodes.NoSuchElement, ex.ErrorCode);
        }

        [Fact]
        public async Task ElementCommand_RejectsShadowRootReferenceAsElement()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var shadowRef = session.RegisterShadowRoot("shadow-token");
            var handler = new CommandHandler(manager)
            {
                Browser = new ScriptStubBrowserDriver()
            };

            var router = new CommandRouter();
            var match = router.Match("GET", $"/session/{session.Id}/element/{shadowRef}/text");
            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, null));

            Assert.Equal(ErrorCodes.NoSuchElement, ex.ErrorCode);
        }

        [Fact]
        public async Task ElementCommand_MapsStaleElementInvalidOperation()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var elementRef = session.RegisterElement("element-token");
            var handler = new CommandHandler(manager)
            {
                Browser = new StaleElementBrowserDriver()
            };

            var router = new CommandRouter();
            var match = router.Match("GET", $"/session/{session.Id}/element/{elementRef}/text");
            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, null));

            Assert.Equal(ErrorCodes.StaleElementReference, ex.ErrorCode);
        }

        [Fact]
        public async Task PerformActions_RejectsUnsupportedWheelSource()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var handler = new CommandHandler(manager)
            {
                Browser = new ScriptStubBrowserDriver()
            };

            var router = new CommandRouter();
            var match = router.Match("POST", $"/session/{session.Id}/actions");
            var body = """
                {
                  "actions": [
                    {
                      "type": "wheel",
                      "id": "wheel-1",
                      "actions": [{ "type": "scroll", "x": 1, "y": 1 }]
                    }
                  ]
                }
                """;

            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, body));
            Assert.Equal(ErrorCodes.UnsupportedOperation, ex.ErrorCode);
        }

        [Fact]
        public async Task NewSession_RejectsRiskyCapabilitiesWithoutExplicitOptIn()
        {
            var manager = new SessionManager();
            var handler = new CommandHandler(manager);
            var router = new CommandRouter();
            var match = router.Match("POST", "/session");
            var body = """
                {
                  "capabilities": {
                    "alwaysMatch": {
                      "fen:options": {
                        "args": ["--allow-file-access"]
                      }
                    }
                  }
                }
                """;

            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, body));
            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
            Assert.NotNull(ex.ErrorData);
        }

        [Fact]
        public async Task SwitchToWindow_RejectsHandleOwnedByAnotherSession()
        {
            var manager = new SessionManager();
            var browser = new IsolatedWindowBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var sessionAResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionBResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionAId = ((NewSessionResponse)sessionAResponse.Value).SessionId;
            var sessionBId = ((NewSessionResponse)sessionBResponse.Value).SessionId;

            var sessionA = manager.GetSession(sessionAId);
            var foreignHandle = Assert.Single(sessionA.WindowHandles);

            var match = router.Match("POST", $"/session/{sessionBId}/window");
            var body = $$"""{"handle":"{{foreignHandle}}"}""";

            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, body));
            Assert.Equal(ErrorCodes.NoSuchWindow, ex.ErrorCode);
            var errorData = Assert.IsType<SecurityFailureData>(ex.ErrorData);
            Assert.Equal(SecurityBlockReasons.SessionIsolationViolation, errorData.Reason);
            Assert.Equal(sessionBId, errorData.SessionId);
        }

        [Fact]
        public async Task CookieCommands_BlockInMultiSessionModeWithoutIsolationSupport()
        {
            var manager = new SessionManager();
            var browser = new ScriptStubBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var sessionAResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionBResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionAId = ((NewSessionResponse)sessionAResponse.Value).SessionId;
            _ = ((NewSessionResponse)sessionBResponse.Value).SessionId;

            var cookieMatch = router.Match("GET", $"/session/{sessionAId}/cookie");
            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(cookieMatch, null));

            Assert.Equal(ErrorCodes.UnsupportedOperation, ex.ErrorCode);
            var errorData = Assert.IsType<SecurityFailureData>(ex.ErrorData);
            Assert.Equal(SecurityBlockReasons.SessionIsolationViolation, errorData.Reason);
            Assert.Equal(sessionAId, errorData.SessionId);
        }

        [Fact]
        public async Task CloseWindow_DeletesOnlyOwningSession_InMultiSessionMode()
        {
            var manager = new SessionManager();
            var browser = new IsolatedWindowBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var sessionAResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionBResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionAId = ((NewSessionResponse)sessionAResponse.Value).SessionId;
            var sessionBId = ((NewSessionResponse)sessionBResponse.Value).SessionId;

            var closeMatch = router.Match("DELETE", $"/session/{sessionAId}/window");
            await handler.ExecuteAsync(closeMatch, null);

            var deletedSessionTimeoutsMatch = router.Match("GET", $"/session/{sessionAId}/timeouts");
            var deletedSessionEx = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(deletedSessionTimeoutsMatch, null));
            Assert.Equal(ErrorCodes.InvalidSessionId, deletedSessionEx.ErrorCode);

            var aliveSessionTimeoutsMatch = router.Match("GET", $"/session/{sessionBId}/timeouts");
            var aliveResponse = await handler.ExecuteAsync(aliveSessionTimeoutsMatch, null);
            Assert.NotNull(aliveResponse);
        }

        private sealed class StubElement
        {
        }

        private sealed class ScriptStubBrowserDriver : IBrowserDriver
        {
            public object[] LastArgs { get; private set; } = Array.Empty<object>();
            public int NavigateCallCount { get; private set; }
            private string _currentUrl = "about:blank";
            private string _deferredCommittedUrl;
            private int _remainingReadsBeforeCommit;

            public void ConfigureDeferredNavigationCommit(string committedUrl, int readsBeforeCommit)
            {
                _deferredCommittedUrl = committedUrl;
                _remainingReadsBeforeCommit = Math.Max(0, readsBeforeCommit);
            }

            public Task NavigateAsync(string url)
            {
                NavigateCallCount++;
                if (string.IsNullOrWhiteSpace(_deferredCommittedUrl))
                {
                    _currentUrl = url;
                }

                return Task.CompletedTask;
            }

            public Task<string> GetCurrentUrlAsync()
            {
                if (!string.IsNullOrWhiteSpace(_deferredCommittedUrl))
                {
                    if (_remainingReadsBeforeCommit <= 0)
                    {
                        _currentUrl = _deferredCommittedUrl;
                        _deferredCommittedUrl = null;
                    }
                    else
                    {
                        _remainingReadsBeforeCommit--;
                    }
                }

                return Task.FromResult(_currentUrl);
            }
            public Task<string> GetTitleAsync() => Task.FromResult(string.Empty);
            public Task<string> GetWindowHandleAsync() => Task.FromResult("window-1");
            public Task<IReadOnlyList<string>> GetWindowHandlesAsync() => Task.FromResult((IReadOnlyList<string>)new[] { "window-1" });
            public Task CloseWindowAsync() => Task.CompletedTask;
            public Task GoBackAsync() => Task.CompletedTask;
            public Task GoForwardAsync() => Task.CompletedTask;
            public Task RefreshAsync() => Task.CompletedTask;
            public Task<object> FindElementAsync(string strategy, string selector, object parentElement = null) => Task.FromResult<object>(null);
            public Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null) => Task.FromResult(Array.Empty<object>());
            public Task<object> GetActiveElementAsync() => Task.FromResult<object>(null);
            public Task<object> GetShadowRootAsync(object element) => Task.FromResult<object>(null);
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

            public Task<object> ExecuteScriptAsync(string script, object[] args)
            {
                LastArgs = args;
                return Task.FromResult<object>(args);
            }

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

        private sealed class IsolatedWindowBrowserDriver : IBrowserDriver
        {
            private readonly List<string> _handles = new();
            private string _currentHandle;

            public Task NavigateAsync(string url) => Task.CompletedTask;
            public Task<string> GetCurrentUrlAsync() => Task.FromResult("about:blank");
            public Task<string> GetTitleAsync() => Task.FromResult(string.Empty);

            public Task<string> GetWindowHandleAsync()
            {
                return Task.FromResult(_currentHandle ?? string.Empty);
            }

            public Task<IReadOnlyList<string>> GetWindowHandlesAsync()
            {
                return Task.FromResult((IReadOnlyList<string>)_handles.ToArray());
            }

            public Task CloseWindowAsync()
            {
                if (!string.IsNullOrWhiteSpace(_currentHandle))
                {
                    _handles.Remove(_currentHandle);
                    _currentHandle = _handles.FirstOrDefault();
                }

                return Task.CompletedTask;
            }

            public Task GoBackAsync() => Task.CompletedTask;
            public Task GoForwardAsync() => Task.CompletedTask;
            public Task RefreshAsync() => Task.CompletedTask;
            public Task<object> FindElementAsync(string strategy, string selector, object parentElement = null) => Task.FromResult<object>(null);
            public Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null) => Task.FromResult(Array.Empty<object>());
            public Task<object> GetActiveElementAsync() => Task.FromResult<object>(null);
            public Task<object> GetShadowRootAsync(object element) => Task.FromResult<object>(null);
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

            public Task<string> NewWindowAsync(string typeHint)
            {
                var handle = $"window-{_handles.Count + 1}";
                _handles.Add(handle);
                _currentHandle = handle;
                return Task.FromResult(handle);
            }

            public Task SwitchToWindowAsync(string windowHandle)
            {
                if (!_handles.Contains(windowHandle))
                {
                    throw new InvalidOperationException($"No such window handle in tab manager: {windowHandle}");
                }

                _currentHandle = windowHandle;
                return Task.CompletedTask;
            }

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
            public bool HasValidCurrentBrowsingContext() => !string.IsNullOrWhiteSpace(_currentHandle) && _handles.Contains(_currentHandle);
        }

        private sealed class StaleElementBrowserDriver : IBrowserDriver
        {
            public Task NavigateAsync(string url) => Task.CompletedTask;
            public Task<string> GetCurrentUrlAsync() => Task.FromResult("about:blank");
            public Task<string> GetTitleAsync() => Task.FromResult(string.Empty);
            public Task<string> GetWindowHandleAsync() => Task.FromResult("window-1");
            public Task<IReadOnlyList<string>> GetWindowHandlesAsync() => Task.FromResult((IReadOnlyList<string>)new[] { "window-1" });
            public Task CloseWindowAsync() => Task.CompletedTask;
            public Task GoBackAsync() => Task.CompletedTask;
            public Task GoForwardAsync() => Task.CompletedTask;
            public Task RefreshAsync() => Task.CompletedTask;
            public Task<object> FindElementAsync(string strategy, string selector, object parentElement = null) => Task.FromResult<object>(null);
            public Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null) => Task.FromResult(Array.Empty<object>());
            public Task<object> GetActiveElementAsync() => Task.FromResult<object>(null);
            public Task<object> GetShadowRootAsync(object element) => Task.FromResult<object>(null);
            public Task<bool> IsElementSelectedAsync(object element) => Task.FromResult(false);
            public Task<object> GetElementPropertyAsync(object element, string name) => Task.FromResult<object>(null);
            public Task<string> GetElementCssValueAsync(object element, string propertyName) => Task.FromResult(string.Empty);
            public Task<string> GetElementTextAsync(object element) => throw new InvalidOperationException("stale element reference");
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
