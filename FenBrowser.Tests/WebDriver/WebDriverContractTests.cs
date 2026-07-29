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
        public void Capabilities_Merge_ReturnsWdspecDefaultProxyAndUserAgent()
        {
            var capabilities = Capabilities.Merge(null);
            var json = JsonSerializer.Serialize(capabilities);

            using var document = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("proxy").ValueKind);
            Assert.False(document.RootElement.GetProperty("proxy").EnumerateObject().Any());
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("userAgent").GetString()));
        }

        [Fact]
        public void Capabilities_Merge_RejectsExplicitEmptyProxy()
        {
            var ex = Assert.Throws<WebDriverException>(() => Capabilities.Merge(new Capabilities
            {
                Proxy = new ProxyConfig
                {
                    ProxyType = null
                }
            }));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
        }

        [Theory]
        [InlineData("""{"capabilities":null}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":null}}""")]
        [InlineData("""{"capabilities":{"firstMatch":{}}}""")]
        [InlineData("""{"capabilities":{"firstMatch":[null]}}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":{"acceptInsecureCerts":"false"}}}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":{"pageLoadStrategy":"Eager"}}}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":{"timeouts":{"pageLoad":2.5}}}}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":{"timeouts":{"invalid":10}}}}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":{"proxy":{"proxyType":"SYSTEM"}}}}""")]
        [InlineData("""{"capabilities":{"alwaysMatch":{"firefoxOptions":{}}}}""")]
        public void NewSession_RejectsInvalidCapabilityPayloads(string payload)
        {
            var manager = new SessionManager();
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse(payload);

            var ex = Assert.Throws<WebDriverException>(() => commands.NewSession(body.RootElement.Clone()));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
            Assert.False(manager.HasActiveSessions);
        }

        [Fact]
        public void NewSession_AllowsExtensionCapabilitiesWithColon()
        {
            var manager = new SessionManager();
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse("""{"capabilities":{"alwaysMatch":{"test:extension":{"key":"value"}}}}""");

            var response = commands.NewSession(body.RootElement.Clone());

            Assert.IsType<NewSessionResponse>(response.Value);
        }

        [Fact]
        public void NewSession_EchoesSafeIntegerTimeouts()
        {
            var manager = new SessionManager();
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse("""{"capabilities":{"alwaysMatch":{"timeouts":{"script":0,"pageLoad":2.0,"implicit":9007199254740991}}}}""");

            var response = commands.NewSession(body.RootElement.Clone());
            var value = Assert.IsType<NewSessionResponse>(response.Value);

            Assert.Equal(0, value.Capabilities.Timeouts.Script);
            Assert.Equal(2, value.Capabilities.Timeouts.PageLoad);
            Assert.Equal(9007199254740991, value.Capabilities.Timeouts.Implicit);
        }

        [Fact]
        public void NewSession_RejectsDuplicateAlwaysAndFirstMatchCapabilities()
        {
            var manager = new SessionManager();
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse("""{"capabilities":{"alwaysMatch":{"timeouts":{"script":10}},"firstMatch":[{},{"timeouts":{"pageLoad":10}}]}}""");

            var ex = Assert.Throws<WebDriverException>(() => commands.NewSession(body.RootElement.Clone()));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
            Assert.False(manager.HasActiveSessions);
        }

        [Fact]
        public void NewSession_EchoesObjectUnhandledPromptBehavior()
        {
            var manager = new SessionManager();
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse("""{"capabilities":{"alwaysMatch":{"unhandledPromptBehavior":{"default":"accept","alert":"ignore"}}}}""");

            var response = commands.NewSession(body.RootElement.Clone());
            var value = Assert.IsType<NewSessionResponse>(response.Value);
            var json = JsonSerializer.Serialize(value.Capabilities);

            using var document = JsonDocument.Parse(json);
            var prompt = document.RootElement.GetProperty("unhandledPromptBehavior");
            Assert.Equal("accept", prompt.GetProperty("default").GetString());
            Assert.Equal("ignore", prompt.GetProperty("alert").GetString());
        }

        [Fact]
        public void NewSession_EchoesWebSocketUrlWhenRequested()
        {
            var manager = new SessionManager();
            var commands = new SessionCommands(manager);
            using var body = JsonDocument.Parse("""{"capabilities":{"alwaysMatch":{"webSocketUrl":true}}}""");

            var response = commands.NewSession(body.RootElement.Clone());
            var value = Assert.IsType<NewSessionResponse>(response.Value);

            Assert.IsType<string>(value.Capabilities.WebSocketUrl);
            Assert.Contains(value.SessionId, (string)value.Capabilities.WebSocketUrl);
        }

        [Fact]
        public async Task Status_ReportsNotReadyWhileSessionIsActive()
        {
            var manager = new SessionManager();
            var handler = new CommandHandler(manager);
            var router = new CommandRouter();

            var readyResponse = await handler.ExecuteAsync(router.Match("GET", "/status"), null);
            using var readyJson = JsonDocument.Parse(readyResponse.ToJson());
            Assert.True(readyJson.RootElement.GetProperty("value").GetProperty("ready").GetBoolean());

            var session = manager.CreateSession(new Capabilities());

            var busyResponse = await handler.ExecuteAsync(router.Match("GET", "/status"), null);
            using var busyJson = JsonDocument.Parse(busyResponse.ToJson());
            Assert.False(busyJson.RootElement.GetProperty("value").GetProperty("ready").GetBoolean());

            manager.DeleteSession(session.Id);
            var finalResponse = await handler.ExecuteAsync(router.Match("GET", "/status"), null);
            using var finalJson = JsonDocument.Parse(finalResponse.ToJson());
            Assert.True(finalJson.RootElement.GetProperty("value").GetProperty("ready").GetBoolean());
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
        public async Task CommandDeadline_EnforcesTimeoutWithoutCompletingBrowserTask()
        {
            var neverCompletes = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var started = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(
                () => WebDriverCommandDeadline.WaitAsync(neverCompletes.Task, 25));

            Assert.InRange(started.ElapsedMilliseconds, 10, 500);
            Assert.False(neverCompletes.Task.IsCompleted);
        }

        [Fact]
        public void ExecuteAsyncScript_OuterDeadlineIncludesTransportGrace()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            session.Timeouts.Script = 1000;
            var handler = new CommandHandler(manager);
            var router = new CommandRouter();

            var timeout = handler.GetProtocolCommandTimeoutMs(
                router.Match("POST", $"/session/{session.Id}/execute/async"));

            Assert.Equal(3000, timeout);
        }

        [Fact]
        public async Task UnresponsiveSession_UsesCachedStateForWptCleanup()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var initialHandle = Assert.Single(session.WindowHandles);
            session.CurrentWindowHandle = initialHandle;
            session.WindowStateInitialized = true;
            var browser = new ScriptStubBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            handler.MarkSessionUnresponsive(session.Id);

            var router = new CommandRouter();
            await handler.ExecuteAsync(
                router.Match("DELETE", $"/session/{session.Id}/actions"),
                null);
            var handles = await handler.ExecuteAsync(
                router.Match("GET", $"/session/{session.Id}/window/handles"),
                null);

            Assert.Equal(0, browser.ReleaseActionsCallCount);
            Assert.Equal(new[] { initialHandle }, Assert.IsAssignableFrom<IEnumerable<string>>(handles.Value));
        }

        [Fact]
        public async Task GetWindowHandle_IgnoresInvalidSelectedChildContext()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            var initialHandle = Assert.Single(session.WindowHandles);
            session.CurrentWindowHandle = initialHandle;
            session.WindowStateInitialized = true;
            var handler = new CommandHandler(manager)
            {
                Browser = new InvalidChildContextBrowserDriver()
            };
            var router = new CommandRouter();

            var response = await handler.ExecuteAsync(
                router.Match("GET", $"/session/{session.Id}/window"),
                null);

            Assert.Equal(initialHandle, response.Value);
        }

        [Theory]
        [InlineData("GET", "/session/{0}/window")]
        [InlineData("GET", "/session/{0}/window/handles")]
        public async Task WindowContextQueries_DoNotHandleOpenUserPrompts(string method, string pathTemplate)
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities
            {
                UnhandledPromptBehavior = "accept and notify"
            });
            var initialHandle = Assert.Single(session.WindowHandles);
            session.CurrentWindowHandle = initialHandle;
            session.WindowStateInitialized = true;

            var browser = new ScriptStubBrowserDriver();
            browser.SetAlert("cheese");
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var response = await handler.ExecuteAsync(
                router.Match(method, string.Format(pathTemplate, session.Id)),
                null);

            Assert.NotNull(response.Value);
            Assert.True(await browser.HasAlertAsync());
            Assert.Equal("cheese", await browser.GetAlertTextAsync());
            Assert.Equal(0, browser.AcceptAlertCallCount);
            Assert.Equal(0, browser.DismissAlertCallCount);
        }

        [Fact]
        public async Task NewSession_ClosesStaleTopLevelContextsAfterCreatingDedicatedContext()
        {
            var manager = new SessionManager();
            var browser = new IsolatedWindowBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var created = await handler.ExecuteAsync(
                router.Match("POST", "/session"),
                """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionId = Assert.IsType<NewSessionResponse>(created.Value).SessionId;

            Assert.Equal(new[] { "window-1" }, browser.SnapshotHandles);

            await handler.ExecuteAsync(
                router.Match("DELETE", $"/session/{sessionId}"),
                null);

            Assert.Equal(new[] { "window-1" }, browser.SnapshotHandles);

            var second = await handler.ExecuteAsync(
                router.Match("POST", "/session"),
                """{"capabilities":{"alwaysMatch":{}}}""");
            var secondSessionId = Assert.IsType<NewSessionResponse>(second.Value).SessionId;

            Assert.NotEqual(sessionId, secondSessionId);
            Assert.Equal(new[] { "window-2" }, browser.SnapshotHandles);
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

        [Theory]
        [InlineData("about:blank", ErrorCodes.InvalidCookieDomain, """{"name":"hello","value":"world"}""")]
        [InlineData("https://web-platform.test/common/blank.html", ErrorCodes.InvalidArgument, """{"name":"hello","value":"world","sameSite":"invalid"}""")]
        [InlineData("https://web-platform.test/common/blank.html", ErrorCodes.InvalidArgument, """{"name":"hello","value":"world","expiry":9007199254740992}""")]
        [InlineData("https://web-platform.test/common/blank.html", ErrorCodes.InvalidCookieDomain, """{"name":"hello","value":"world","domain":"example.com"}""")]
        public async Task AddCookie_RejectsInvalidDomainAndCookieFields(string currentUrl, string expectedError, string cookieJson)
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            session.WindowHandles.Add("window-1");
            session.CurrentWindowHandle = "window-1";
            var browser = new ScriptStubBrowserDriver();
            browser.SetCurrentUrl(currentUrl);
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();
            var match = router.Match("POST", $"/session/{session.Id}/cookie");

            var ex = await Assert.ThrowsAsync<WebDriverException>(() =>
                handler.ExecuteAsync(match, $$"""{"cookie":{{cookieJson}}}"""));

            Assert.Equal(expectedError, ex.ErrorCode);
            Assert.Empty(browser.AddedCookies);
        }

        [Fact]
        public async Task AddCookie_PreservesValidatedCookieMetadata()
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities());
            session.WindowHandles.Add("window-1");
            session.CurrentWindowHandle = "window-1";
            var browser = new ScriptStubBrowserDriver();
            browser.SetCurrentUrl("https://web-platform.test/common/blank.html");
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();
            var match = router.Match("POST", $"/session/{session.Id}/cookie");

            var response = await handler.ExecuteAsync(
                match,
                """{"cookie":{"name":"hello","value":"world","domain":"web-platform.test","path":"/","secure":true,"httpOnly":true,"expiry":1893456000,"sameSite":"Strict"}}""");

            Assert.Null(response.Value);
            var cookie = Assert.Single(browser.AddedCookies);
            Assert.Equal("hello", cookie.Name);
            Assert.Equal("world", cookie.Value);
            Assert.Equal("web-platform.test", cookie.Domain);
            Assert.Equal("/", cookie.Path);
            Assert.True(cookie.Secure);
            Assert.True(cookie.HttpOnly);
            Assert.Equal(1893456000, cookie.Expiry);
            Assert.Equal("Strict", cookie.SameSite);
        }

        [Fact]
        public async Task NewWindow_RejectsNullCommandParameters()
        {
            var manager = new SessionManager();
            var browser = new IsolatedWindowBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var sessionResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionId = ((NewSessionResponse)sessionResponse.Value).SessionId;

            var match = router.Match("POST", $"/session/{sessionId}/window/new");
            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, "null"));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
        }

        [Fact]
        public async Task NewWindow_RejectsNonStringType()
        {
            var manager = new SessionManager();
            var browser = new IsolatedWindowBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var sessionResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionId = ((NewSessionResponse)sessionResponse.Value).SessionId;

            var match = router.Match("POST", $"/session/{sessionId}/window/new");
            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(match, """{"type":true}"""));

            Assert.Equal(ErrorCodes.InvalidArgument, ex.ErrorCode);
        }

        [Fact]
        public async Task NewWindow_RejectsClosedSelectedTopLevelContext()
        {
            var manager = new SessionManager();
            var browser = new IsolatedWindowBrowserDriver();
            var handler = new CommandHandler(manager)
            {
                Browser = browser
            };
            var router = new CommandRouter();

            var sessionResponse = await handler.ExecuteAsync(router.Match("POST", "/session"), """{"capabilities":{"alwaysMatch":{}}}""");
            var sessionId = ((NewSessionResponse)sessionResponse.Value).SessionId;
            var createMatch = router.Match("POST", $"/session/{sessionId}/window/new");
            var createResponse = await handler.ExecuteAsync(createMatch, """{"type":"tab"}""");
            var createdHandle = createResponse.Value
                ?.GetType()
                .GetProperty("handle")
                ?.GetValue(createResponse.Value)
                ?.ToString();

            await handler.ExecuteAsync(
                router.Match("POST", $"/session/{sessionId}/window"),
                $$"""{"handle":"{{createdHandle}}"}""");
            await handler.ExecuteAsync(router.Match("DELETE", $"/session/{sessionId}/window"), null);

            var ex = await Assert.ThrowsAsync<WebDriverException>(() => handler.ExecuteAsync(createMatch, """{"type":null}"""));

            Assert.Equal(ErrorCodes.NoSuchWindow, ex.ErrorCode);
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

        private class ScriptStubBrowserDriver : IBrowserDriver
        {
            public object[] LastArgs { get; private set; } = Array.Empty<object>();
            public int NavigateCallCount { get; private set; }
            public int ReleaseActionsCallCount { get; private set; }
            public int AcceptAlertCallCount { get; private set; }
            public int DismissAlertCallCount { get; private set; }
            public List<WdCookie> AddedCookies { get; } = new();
            private string _currentUrl = "about:blank";
            private string _deferredCommittedUrl;
            private int _remainingReadsBeforeCommit;
            private string _alertText;

            public void SetCurrentUrl(string url)
            {
                _currentUrl = url ?? "about:blank";
                _deferredCommittedUrl = null;
                _remainingReadsBeforeCommit = 0;
            }

            public void SetAlert(string text)
            {
                _alertText = text ?? string.Empty;
            }

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
            public Task SendKeysAsync(object element, string text, bool strictFileInteractability = false) => Task.CompletedTask;
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
            public Task AddCookieAsync(WdCookie cookie)
            {
                AddedCookies.Add(cookie);
                return Task.CompletedTask;
            }
            public Task DeleteCookieAsync(string name) => Task.CompletedTask;
            public Task DeleteAllCookiesAsync()
            {
                AddedCookies.Clear();
                return Task.CompletedTask;
            }
            public Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions) => Task.CompletedTask;
            public Task ReleaseActionsAsync()
            {
                ReleaseActionsCallCount++;
                return Task.CompletedTask;
            }
            public Task<bool> HasAlertAsync() => Task.FromResult(_alertText != null);
            public Task DismissAlertAsync()
            {
                DismissAlertCallCount++;
                _alertText = null;
                return Task.CompletedTask;
            }
            public Task AcceptAlertAsync()
            {
                AcceptAlertCallCount++;
                _alertText = null;
                return Task.CompletedTask;
            }
            public Task<string> GetAlertTextAsync() => Task.FromResult(_alertText ?? string.Empty);
            public Task SendAlertTextAsync(string text)
            {
                _alertText = text ?? string.Empty;
                return Task.CompletedTask;
            }
            public virtual bool HasValidCurrentBrowsingContext() => true;
        }

        private sealed class IsolatedWindowBrowserDriver : IBrowserDriver
        {
            private readonly List<string> _handles = new();
            private string _currentHandle;

            public IReadOnlyList<string> SnapshotHandles => _handles.ToArray();

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
            public Task SendKeysAsync(object element, string text, bool strictFileInteractability = false) => Task.CompletedTask;
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

        private sealed class InvalidChildContextBrowserDriver : ScriptStubBrowserDriver
        {
            public override bool HasValidCurrentBrowsingContext() => false;
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
            public Task SendKeysAsync(object element, string text, bool strictFileInteractability = false) => Task.CompletedTask;
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
