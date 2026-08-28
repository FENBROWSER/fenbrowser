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
    // WD-004: pageLoadStrategy eager/normal must wait for the document readiness
    // stage reported by the browser seam, not return at URL commit.
    public class PageLoadStrategyNavigationTests
    {
        [Fact]
        public async Task NormalStrategy_DoesNotReturnBeforeDocumentCompletes()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start");
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            var navigationTask = navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));

            // The URL commits instantly, but the fake's document never fires load
            // until the gate is released: a success here means the command returned
            // on URL commit alone.
            var returnedEarly = await Task.WhenAny(navigationTask, Task.Delay(500)) == navigationTask;
            Assert.False(returnedEarly, "normal strategy returned before document completion");

            driver.ReadinessGate.TrySetResult(WdReadinessWaitStatus.Reached);
            var response = await navigationTask;
            Assert.IsNotType<WebDriverError>(response.Value);
            Assert.True(driver.ReadinessWaitCalled);
            Assert.Equal(WdDocumentReadinessStage.Complete, driver.RequestedStage);
            Assert.True(driver.RequestedTimeoutMs <= 2000);
        }

        [Fact]
        public async Task EagerStrategy_WaitsForInteractiveStage()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start");
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "eager", pageLoadTimeoutMs: 2000);

            var navigationTask = navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));
            var returnedEarly = await Task.WhenAny(navigationTask, Task.Delay(500)) == navigationTask;
            Assert.False(returnedEarly, "eager strategy returned before document interactivity");

            driver.ReadinessGate.TrySetResult(WdReadinessWaitStatus.Reached);
            var response = await navigationTask;

            Assert.IsNotType<WebDriverError>(response.Value);
            Assert.Equal(WdDocumentReadinessStage.Interactive, driver.RequestedStage);
        }

        [Fact]
        public async Task NoneStrategy_ReturnsWithoutReadinessWait()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start");
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "none", pageLoadTimeoutMs: 2000);

            var response = await navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));

            Assert.IsNotType<WebDriverError>(response.Value);
            Assert.False(driver.ReadinessWaitCalled);
        }

        [Fact]
        public async Task NormalStrategy_ReadinessTimeoutThrowsTimeoutError()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.TimedOut)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            var ex = await Assert.ThrowsAsync<WebDriverException>(
                () => navigation.NavigateToAsync(session.Id, Body("https://example.test/a")));

            Assert.Equal(ErrorCodes.Timeout, ex.ErrorCode);
        }

        [Fact]
        public async Task NormalStrategy_NavigationFailureThrowsUnknownError()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.NavigationAborted)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            var ex = await Assert.ThrowsAsync<WebDriverException>(
                () => navigation.NavigateToAsync(session.Id, Body("https://example.test/a")));

            Assert.Equal(ErrorCodes.UnknownError, ex.ErrorCode);
        }

        [Fact]
        public async Task NormalStrategy_ClosedContextDuringWaitThrowsNoSuchWindow()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                OnReadinessWait = () => throw new InvalidOperationException("Current browsing context is no longer open")
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            var ex = await Assert.ThrowsAsync<WebDriverException>(
                () => navigation.NavigateToAsync(session.Id, Body("https://example.test/a")));

            Assert.Equal(ErrorCodes.NoSuchWindow, ex.ErrorCode);
        }

        [Fact]
        public async Task NormalStrategy_SameUrlNavigationStillWaitsForReadiness()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/a");
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            // Requested URL equals the current URL: commit detection returns
            // immediately, so only the readiness wait can serialize completion.
            var navigationTask = navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));
            var returnedEarly = await Task.WhenAny(navigationTask, Task.Delay(500)) == navigationTask;
            Assert.False(returnedEarly, "same-URL navigation returned before document completion");

            driver.ReadinessGate.TrySetResult(WdReadinessWaitStatus.Reached);
            var response = await navigationTask;

            Assert.IsNotType<WebDriverError>(response.Value);
            Assert.True(driver.ReadinessWaitCalled);
        }

        [Fact]
        public async Task NormalStrategy_RedirectCommitProceedsToReadinessWait()
        {
            // The browser lands on a redirect target that differs from both the
            // previous URL and the requested URL; commit detection accepts it and
            // the readiness stage still gates completion.
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                RedirectTarget = "https://example.test/redirected"
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            var navigationTask = navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));
            var returnedEarly = await Task.WhenAny(navigationTask, Task.Delay(500)) == navigationTask;
            Assert.False(returnedEarly, "redirected navigation returned before document completion");

            driver.ReadinessGate.TrySetResult(WdReadinessWaitStatus.Reached);
            var response = await navigationTask;

            Assert.IsNotType<WebDriverError>(response.Value);
            Assert.True(driver.ReadinessWaitCalled);
        }

        // One page-load budget: navigate + commit + readiness must share the
        // session's timeouts.pageLoad, each wait receiving only the remainder.
        [Fact]
        public async Task TimeoutBudget_NavigateTimeIsDeductedFromReadinessWait()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                NavigateDelayMs = 300,
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.Reached)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 1500);

            var response = await navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));

            Assert.IsNotType<WebDriverError>(response.Value);
            // The readiness wait must see roughly 1200ms, not the full 1500ms.
            Assert.InRange(driver.RequestedTimeoutMs, 1000, 1300);
        }

        [Fact]
        public async Task TimeoutBudget_CommitWaitReceivesRemainingNotFullBudget()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                NavigateDelayMs = 300,
                CommitDelayMs = 900
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 800);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<WebDriverException>(
                () => navigation.NavigateToAsync(session.Id, Body("https://example.test/a")));
            clock.Stop();

            // Commit never happens (900ms after a 300ms navigate against an 800ms
            // budget): the command must give up at the budget (~800ms), not after a
            // full extra commit wait (which would push the total past 1100ms).
            Assert.True(clock.ElapsedMilliseconds < 1000,
                $"command took {clock.ElapsedMilliseconds}ms, exceeding the 800ms page-load budget");
        }

        [Fact]
        public async Task TimeoutBudget_NearlyExhaustedBudgetLeavesAlmostNoReadinessTime()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                NavigateDelayMs = 60,
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.TimedOut)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 250);

            var ex = await Assert.ThrowsAsync<WebDriverException>(
                () => navigation.NavigateToAsync(session.Id, Body("https://example.test/a")));

            Assert.Equal(ErrorCodes.Timeout, ex.ErrorCode);
            // Initiation consumed part of the budget, so the readiness wait
            // receives roughly 190ms, never the full 250ms.
            Assert.InRange(driver.RequestedTimeoutMs, 140, 200);
        }

        // Correlation: the readiness wait must receive the navigation identifier
        // captured when the navigation began.
        [Fact]
        public async Task TrackedNavigate_DefaultSeamPassesUntrackedId()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.Reached)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            await navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));

            // The default NavigateTrackedAsync delegates to NavigateAsync and
            // reports the navigation as untracked (0).
            Assert.Equal(0, driver.RequestedNavigationId);
        }

        [Fact]
        public async Task TrackedNavigate_PassesNavigationIdToReadinessWait()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                OnNavigateTracked = _ => Task.FromResult(4242L),
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.Reached)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 2000);

            await navigation.NavigateToAsync(session.Id, Body("https://example.test/a"));

            Assert.Equal(4242, driver.RequestedNavigationId);
        }

        // The page-load budget bounds navigation initiation itself: a driver that
        // takes longer than the remaining budget to initiate surfaces a timeout
        // instead of stalling the command for the whole initiation.
        [Fact]
        public async Task TimeoutBudget_BoundsNavigationInitiation()
        {
            var driver = new ReadinessFakeDriver(startingUrl: "https://example.test/start")
            {
                OnNavigateTracked = async _ =>
                {
                    await Task.Delay(1500);
                    return 7L;
                },
                OnReadinessWait = () => Task.FromResult(WdReadinessWaitStatus.Reached)
            };
            var (handler, navigation, session) = CreateHarness(driver, pageLoadStrategy: "normal", pageLoadTimeoutMs: 300);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<WebDriverException>(
                () => navigation.NavigateToAsync(session.Id, Body("https://example.test/a")));
            clock.Stop();

            Assert.Equal(ErrorCodes.Timeout, ex.ErrorCode);
            Assert.True(clock.ElapsedMilliseconds < 1000,
                $"initiation deadline not enforced: command took {clock.ElapsedMilliseconds}ms against a 300ms budget");
        }

        private static (CommandHandler Handler, NavigationCommands Navigation, Session Session) CreateHarness(
            ReadinessFakeDriver driver,
            string pageLoadStrategy,
            int pageLoadTimeoutMs)
        {
            var manager = new SessionManager();
            var session = manager.CreateSession(new Capabilities
            {
                PageLoadStrategy = pageLoadStrategy,
                Timeouts = new Timeouts { PageLoad = pageLoadTimeoutMs }
            });
            var handler = new CommandHandler(manager) { Browser = driver };
            return (handler, new NavigationCommands(handler), session);
        }

        private static JsonElement Body(string url)
        {
            return JsonDocument.Parse($"{{\"url\":\"{url}\"}}").RootElement.Clone();
        }

        private sealed class ReadinessFakeDriver : IBrowserDriver
        {
            private readonly string _startingUrl;
            private long _navigateStartTicks = -1;
            private string _pendingUrl;

            public ReadinessFakeDriver(string startingUrl)
            {
                _startingUrl = startingUrl;
            }

            public string RedirectTarget { get; init; }

            /// <summary>Time NavigateAsync spends before the navigation is initiated.</summary>
            public int NavigateDelayMs { get; init; }

            /// <summary>Time after initiation before the new URL is reported committed.</summary>
            public int CommitDelayMs { get; init; }

            public bool ReadinessWaitCalled { get; private set; }

            public WdDocumentReadinessStage? RequestedStage { get; private set; }

            public int RequestedTimeoutMs { get; private set; } = -1;

            public TaskCompletionSource<WdReadinessWaitStatus> ReadinessGate { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Func<Task<WdReadinessWaitStatus>>? OnReadinessWait { get; init; }

            public long RequestedNavigationId { get; private set; } = -1;

            public Func<string, Task<long>>? OnNavigateTracked { get; init; }

            public async Task<WdReadinessWaitStatus> WaitForDocumentReadinessAsync(WdDocumentReadinessStage stage, int timeoutMs, long navigationId)
            {
                ReadinessWaitCalled = true;
                RequestedStage = stage;
                RequestedTimeoutMs = timeoutMs;
                RequestedNavigationId = navigationId;
                if (OnReadinessWait != null)
                {
                    return await OnReadinessWait();
                }

                return await ReadinessGate.Task;
            }

            public async Task<long> NavigateTrackedAsync(string url)
            {
                var id = OnNavigateTracked != null ? await OnNavigateTracked(url) : 0;
                await NavigateAsync(url);
                return id;
            }

            public async Task NavigateAsync(string url)
            {
                _pendingUrl = RedirectTarget ?? url;
                if (NavigateDelayMs > 0)
                {
                    await Task.Delay(NavigateDelayMs);
                }

                _navigateStartTicks = Environment.TickCount64;
            }

            public Task<string> GetCurrentUrlAsync()
            {
                if (_pendingUrl == null)
                {
                    return Task.FromResult(_startingUrl);
                }

                var elapsedMs = _navigateStartTicks >= 0
                    ? Environment.TickCount64 - _navigateStartTicks
                    : long.MaxValue;
                return Task.FromResult(elapsedMs >= CommitDelayMs ? _pendingUrl : _startingUrl);
            }

            private static NotSupportedException Unsupported(string member) =>
                new($"{member} is not used by this stub.");

            public Task<string> GetTitleAsync() => throw Unsupported(nameof(GetTitleAsync));
            public Task<string> GetWindowHandleAsync() => Task.FromResult("win-1");
            public Task<IReadOnlyList<string>> GetWindowHandlesAsync() =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "win-1" });
            public Task CloseWindowAsync() => throw Unsupported(nameof(CloseWindowAsync));
            public Task GoBackAsync() => throw Unsupported(nameof(GoBackAsync));
            public Task GoForwardAsync() => throw Unsupported(nameof(GoForwardAsync));
            public Task RefreshAsync() => throw Unsupported(nameof(RefreshAsync));
            public Task<object> FindElementAsync(string strategy, string selector, object parentElement = null) => throw Unsupported(nameof(FindElementAsync));
            public Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null) => throw Unsupported(nameof(FindElementsAsync));
            public Task<object> GetActiveElementAsync() => throw Unsupported(nameof(GetActiveElementAsync));
            public Task<object> GetShadowRootAsync(object element) => throw Unsupported(nameof(GetShadowRootAsync));
            public Task<bool> IsElementSelectedAsync(object element) => throw Unsupported(nameof(IsElementSelectedAsync));
            public Task<object> GetElementPropertyAsync(object element, string name) => throw Unsupported(nameof(GetElementPropertyAsync));
            public Task<string> GetElementCssValueAsync(object element, string propertyName) => throw Unsupported(nameof(GetElementCssValueAsync));
            public Task<string> GetElementTextAsync(object element) => throw Unsupported(nameof(GetElementTextAsync));
            public Task<string> GetElementTagNameAsync(object element) => throw Unsupported(nameof(GetElementTagNameAsync));
            public Task<WdElementRect> GetElementRectAsync(object element) => throw Unsupported(nameof(GetElementRectAsync));
            public Task<bool> IsElementEnabledAsync(object element) => throw Unsupported(nameof(IsElementEnabledAsync));
            public Task<string> GetElementComputedRoleAsync(object element) => throw Unsupported(nameof(GetElementComputedRoleAsync));
            public Task<string> GetElementComputedLabelAsync(object element) => throw Unsupported(nameof(GetElementComputedLabelAsync));
            public Task ClickElementAsync(object element) => throw Unsupported(nameof(ClickElementAsync));
            public Task ClearElementAsync(object element) => throw Unsupported(nameof(ClearElementAsync));
            public Task SendKeysAsync(object element, string text, bool strictFileInteractability = false) => throw Unsupported(nameof(SendKeysAsync));
            public Task<string> GetElementAttributeAsync(object element, string name) => throw Unsupported(nameof(GetElementAttributeAsync));
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
