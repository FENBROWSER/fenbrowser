using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host.Tabs;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.Host.WebDriver
{
    public class HostBrowserDriver : IBrowserDriver
    {
        private const int ElementLookupTimeoutMs = 0;
        private static readonly TimeSpan ElementLookupPollInterval = TimeSpan.FromMilliseconds(50);
        private TabManager _tabs => TabManager.Instance;

        public async Task NavigateAsync(string url)
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab == null)
                {
                    _tabs.CreateTab();
                }

                var activeTab = _tabs.ActiveTab ?? throw new InvalidOperationException("Current browsing context is no longer open");
                await activeTab.NavigateProgrammaticAsync(url);
            });
        }

        private BrowserTab GetActiveTabOrThrow()
        {
            return _tabs.ActiveTab ?? throw new InvalidOperationException("Current browsing context is no longer open");
        }

        private BrowserHost GetActiveHostOrThrow(bool requireValidContext = true)
        {
            var host = GetActiveTabOrThrow().Browser?.Host;
            if (host == null)
            {
                throw new InvalidOperationException("Current browsing context is no longer open");
            }

            if (requireValidContext && !host.HasValidCurrentBrowsingContext())
            {
                throw new InvalidOperationException("Current browsing context is no longer open");
            }

            return host;
        }

        public async Task<string> GetCurrentUrlAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                var currentUrl = await host.GetCurrentUrlAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(currentUrl) ? "about:blank" : currentUrl;
            });
        }

        public async Task<string> GetTitleAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var activeTab = GetActiveTabOrThrow();
                var host = GetActiveHostOrThrow(requireValidContext: false);
                var domTitle = await host.GetTitleAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(domTitle))
                {
                    return domTitle;
                }

                return activeTab?.Title ?? string.Empty;
            });
        }

        public async Task<string> GetWindowHandleAsync()
        {
            return await RunOnMainThread(() =>
            {
                return GetActiveTabOrThrow().Id.ToString();
            });
        }

        public async Task<IReadOnlyList<string>> GetWindowHandlesAsync()
        {
            return await RunOnMainThread(() =>
            {
                var handles = _tabs.Tabs.Select(tab => tab.Id.ToString()).ToList();
                return (IReadOnlyList<string>)handles;
            });
        }

        public async Task CloseWindowAsync()
        {
            await RunOnMainThread(() =>
            {
                _ = GetActiveTabOrThrow();
                _tabs.CloseActiveTab();
            });
        }

        public async Task GoBackAsync()
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab?.Browser != null)
                {
                    await _tabs.ActiveTab.Browser.GoBackAsync();
                }
            });
        }

        public async Task GoForwardAsync()
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab?.Browser != null)
                {
                    await _tabs.ActiveTab.Browser.GoForwardAsync();
                }
            });
        }

        public async Task RefreshAsync()
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab != null)
                {
                    await _tabs.ActiveTab.NavigateProgrammaticAsync(_tabs.ActiveTab.Url);
                }
            });
        }

        public async Task<object> FindElementAsync(string strategy, string selector, object parentElement = null)
        {
            var parentId = parentElement as string;
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ElementLookupTimeoutMs);

            while (true)
            {
                var (id, isLoading) = await RunOnMainThread(async () =>
                {
                    var activeTab = _tabs.ActiveTab;
                    var host = activeTab?.Browser?.Host;
                    if (host == null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    var elementId = await host.FindElementAsync(strategy, selector, parentId);
                    return (Id: elementId, IsLoading: activeTab.IsLoading);
                });

                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return null;
                }

                // During navigation and immediate post-load settling, polling avoids flaky no-such-element races.
                await Task.Delay(isLoading ? ElementLookupPollInterval : TimeSpan.FromMilliseconds(25));
            }
        }

        public async Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null)
        {
            var parentId = parentElement as string;
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ElementLookupTimeoutMs);

            while (true)
            {
                var (ids, isLoading) = await RunOnMainThread(async () =>
                {
                    var activeTab = _tabs.ActiveTab;
                    var host = activeTab?.Browser?.Host;
                    if (host == null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    var elementIds = await host.FindElementsAsync(strategy, selector, parentId);
                    return (Ids: elementIds ?? Array.Empty<string>(), IsLoading: activeTab.IsLoading);
                });

                if (ids.Length > 0)
                {
                    return ids.Select(id => (object)id).ToArray();
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return Array.Empty<object>();
                }

                await Task.Delay(isLoading ? ElementLookupPollInterval : TimeSpan.FromMilliseconds(25));
            }
        }

        public async Task<object> GetActiveElementAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return null;
                var id = await host.GetActiveElementAsync();
                return (object)id;
            });
        }

        public async Task<object> GetShadowRootAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ElementLookupTimeoutMs);
            while (true)
            {
                var (shadowId, isLoading) = await RunOnMainThread(async () =>
                {
                    var activeTab = _tabs.ActiveTab;
                    var host = activeTab?.Browser?.Host;
                    if (host == null)
                    {
                        throw new InvalidOperationException("Current browsing context is no longer open");
                    }

                    var resolvedShadowId = await host.GetShadowRootAsync(id);
                    return (ShadowId: resolvedShadowId, IsLoading: activeTab.IsLoading);
                });

                if (!string.IsNullOrWhiteSpace(shadowId))
                {
                    return shadowId;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return null;
                }

                await Task.Delay(isLoading ? ElementLookupPollInterval : TimeSpan.FromMilliseconds(25));
            }
        }

        public async Task<bool> IsElementSelectedAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.IsElementSelectedAsync(id);
            });
        }

        public async Task<object> GetElementPropertyAsync(object element, string name)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementPropertyAsync(id, name);
            });
        }

        public async Task<string> GetElementCssValueAsync(object element, string propertyName)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementCssValueAsync(id, propertyName);
            });
        }

        public async Task<string> GetElementTextAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementTextAsync(id);
            });
        }

        public async Task<string> GetElementTagNameAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementTagNameAsync(id);
            });
        }

        public async Task<WdElementRect> GetElementRectAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                var rect = await host.GetElementRectAsync(id);
                return new WdElementRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
            });
        }

        public async Task<bool> IsElementEnabledAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.IsElementEnabledAsync(id);
            });
        }

        public async Task<string> GetElementComputedRoleAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementComputedRoleAsync(id);
            });
        }

        public async Task<string> GetElementComputedLabelAsync(object element)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementComputedLabelAsync(id);
            });
        }

        public async Task ClickElementAsync(object element)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                if (element is not string id)
                {
                    throw new InvalidOperationException("Element reference is invalid for current browsing context");
                }

                await host.ClickElementAsync(id);
            });
        }

        public async Task ClearElementAsync(object element)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                if (element is not string id)
                {
                    throw new InvalidOperationException("Element reference is invalid for current browsing context");
                }

                await host.ClearElementAsync(id);
            });
        }

        public async Task SendKeysAsync(object element, string text, bool strictFileInteractability = false)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                if (element is not string id)
                {
                    throw new InvalidOperationException("Element reference is invalid for current browsing context");
                }

                await host.SendKeysToElementAsync(id, text ?? string.Empty, strictFileInteractability);
            });
        }

        public async Task<string> GetElementAttributeAsync(object element, string name)
        {
            if (element is not string id)
            {
                throw new InvalidOperationException("no such element: invalid element reference");
            }

            return await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                return await host.GetElementAttributeAsync(id, name);
            });
        }

        public async Task<string> GetPageSourceAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return "<html></html>";
                return await host.GetPageSourceAsync();
            });
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null)
                {
                    throw new InvalidOperationException("Current browsing context is no longer open");
                }
                return await host.ExecuteScriptAsync(script, args);
            });
        }

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null)
                {
                    throw new InvalidOperationException("Current browsing context is no longer open");
                }
                return await host.ExecuteAsyncScriptAsync(script, args, timeout);
            });
        }

        public async Task<string> TakeScreenshotAsync()
        {
            return await RunOnMainThread(() =>
            {
                var bitmap = WindowManager.Instance.CaptureScreenshot();
                if (bitmap != null)
                {
                    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    return Convert.ToBase64String(data.ToArray());
                }

                return "";
            });
        }

        public async Task<string> TakeElementScreenshotAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.CaptureElementScreenshotAsync(id);
            });
        }

        public async Task<string> PrintPageAsync(WdPrintOptions options)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return "";
                var page = options?.Page ?? new WdPrintPageOptions();
                var landscape = string.Equals(options?.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
                var scale = options?.Scale ?? 1.0;
                return await host.PrintToPdfAsync(page.Width, page.Height, landscape, scale);
            });
        }

        public (int x, int y, int width, int height) GetWindowRect()
        {
            var host = GetActiveHostOrThrow();
            var rect = host.GetWindowRect();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public void SetWindowRect(int? x, int? y, int? width, int? height)
        {
            var host = GetActiveHostOrThrow();
            host.SetWindowRect(x, y, width, height);
        }

        public (int x, int y, int width, int height) MaximizeWindow()
        {
            var host = GetActiveHostOrThrow();
            var rect = host.MaximizeWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) MinimizeWindow()
        {
            var host = GetActiveHostOrThrow();
            var rect = host.MinimizeWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) FullscreenWindow()
        {
            var host = GetActiveHostOrThrow();
            var rect = host.FullscreenWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public async Task<string> NewWindowAsync(string typeHint)
        {
            return await RunOnMainThread(async () =>
            {
                var beforeActiveTabId = _tabs.ActiveTab?.Id;
                var beforeCount = _tabs.Tabs.Count;
                _tabs.CreateTab();

                // Fallback guard if tab creation was ignored by host state.
                if (_tabs.Tabs.Count <= beforeCount || _tabs.ActiveTab?.Id == beforeActiveTabId)
                {
                    _tabs.CreateTab();
                }

                var activeTab = _tabs.ActiveTab;
                if (activeTab != null)
                {
                    return activeTab.Id.ToString();
                }

                return beforeActiveTabId?.ToString() ?? Guid.NewGuid().ToString("N");
            });
        }

        public async Task SwitchToWindowAsync(string windowHandle)
        {
            var switched = await RunOnMainThread(() =>
            {
                // Correlate WebDriver window handle to tab ID for physical tab switching
                if (int.TryParse(windowHandle, out var tabId))
                {
                    var tabs = _tabs.Tabs;
                    var didSwitch = false;
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        if (tabs[i].Id == tabId)
                        {
                            if (i == _tabs.ActiveIndex)
                            {
                                return false;
                            }

                            _tabs.SwitchToTab(i);
                            didSwitch = true;
                            break;
                        }
                    }
                    if (!didSwitch)
                    {
                        throw new InvalidOperationException($"No such window handle in tab manager: {windowHandle}");
                    }
                    return true;
                }

                throw new InvalidOperationException($"Invalid window handle format: {windowHandle}");
            });

            if (!switched)
            {
                return;
            }

            await RunOnMainThread(async () =>
            {
                // Switching top-level browsing context resets frame focus to top-level.
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.SwitchToFrameAsync(null);
                }
            });
        }

        public async Task SwitchToFrameAsync(object frameReference)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.SwitchToFrameAsync(frameReference);
                }
            });
        }

        public async Task SwitchToParentFrameAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.SwitchToParentFrameAsync();
                }
            });
        }

        public async Task<IReadOnlyList<WdCookie>> GetAllCookiesAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return (IReadOnlyList<WdCookie>)Array.Empty<WdCookie>();
                var cookies = await host.GetAllCookiesAsync();
                return cookies.Select(ToWdCookie).ToList();
            });
        }

        public async Task<WdCookie> GetNamedCookieAsync(string name)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return null;
                var cookie = await host.GetCookieAsync(name);
                return cookie == null ? null : ToWdCookie(cookie);
            });
        }

        public async Task AddCookieAsync(WdCookie cookie)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || cookie == null) return;
                await host.AddCookieAsync(ToEngineCookie(cookie));
            });
        }

        public async Task DeleteCookieAsync(string name)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.DeleteCookieAsync(name);
            });
        }

        public async Task DeleteAllCookiesAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.DeleteAllCookiesAsync();
            });
        }

        public async Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions)
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                var mapped = (actions ?? Array.Empty<WdActionSequence>()).Select(ToEngineActionChain).ToList();
                await host.PerformActionsAsync(mapped);
            });
        }

        public async Task ReleaseActionsAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = GetActiveHostOrThrow();
                await host.ReleaseActionsAsync();
            });
        }

        public async Task<bool> HasAlertAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return false;
                return await host.HasAlertAsync();
            });
        }

        public async Task DismissAlertAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.DismissAlertAsync();
            });
        }

        public async Task AcceptAlertAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.AcceptAlertAsync();
            });
        }

        public async Task<string> GetAlertTextAsync()
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return string.Empty;
                return await host.GetAlertTextAsync();
            });
        }

        public async Task SendAlertTextAsync(string text)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                await host.SendAlertTextAsync(text ?? string.Empty);
            });
        }

        public void SetUnhandledPromptBehavior(string behavior)
        {
            RunOnMainThread(() =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                host?.SetUnhandledPromptBehavior(behavior);
            }).GetAwaiter().GetResult();
        }

        public bool HasValidCurrentBrowsingContext()
        {
            var host = _tabs.ActiveTab?.Browser?.Host;
            return host != null && host.HasValidCurrentBrowsingContext();
        }

        private static WdCookie ToWdCookie(FenBrowser.FenEngine.Rendering.WebDriverCookie cookie)
        {
            return new WdCookie
            {
                Name = cookie?.Name ?? string.Empty,
                Value = cookie?.Value ?? string.Empty,
                Path = cookie?.Path ?? "/",
                Domain = cookie?.Domain ?? string.Empty,
                Secure = cookie?.Secure ?? false,
                HttpOnly = cookie?.HttpOnly ?? false,
                Expiry = cookie?.Expiry,
                SameSite = cookie?.SameSite ?? "Lax"
            };
        }

        private static FenBrowser.FenEngine.Rendering.WebDriverCookie ToEngineCookie(WdCookie cookie)
        {
            return new FenBrowser.FenEngine.Rendering.WebDriverCookie
            {
                Name = cookie?.Name ?? string.Empty,
                Value = cookie?.Value ?? string.Empty,
                Path = cookie?.Path ?? "/",
                Domain = cookie?.Domain ?? string.Empty,
                Secure = cookie?.Secure ?? false,
                HttpOnly = cookie?.HttpOnly ?? false,
                Expiry = cookie?.Expiry,
                SameSite = cookie?.SameSite ?? "Lax"
            };
        }

        private static ActionChain ToEngineActionChain(WdActionSequence sequence)
        {
            var chain = new ActionChain
            {
                Type = sequence?.Type ?? string.Empty,
                Id = sequence?.Id ?? string.Empty
            };

            if (sequence?.Actions != null)
            {
                foreach (var action in sequence.Actions)
                {
                    chain.Actions.Add(new InputAction
                    {
                        Type = action?.Type ?? string.Empty,
                        Duration = action?.Duration ?? 0,
                        X = action?.X ?? 0,
                        Y = action?.Y ?? 0,
                        Button = action?.Button ?? 0,
                        Value = action?.Value ?? string.Empty,
                        Origin = action?.Origin ?? string.Empty
                    });
                }
            }

            return chain;
        }

        private Task<T> RunOnMainThread<T>(Func<T> func) => WindowManager.Instance.RunOnMainThread(func);
        private Task RunOnMainThread(Action action) => WindowManager.Instance.RunOnMainThread(action);
        private async Task RunOnMainThread(Func<Task> func)
        {
            var task = await WindowManager.Instance.RunOnMainThread(func).ConfigureAwait(false);
            if (task != null)
            {
                await task.ConfigureAwait(false);
            }
        }

        private async Task<T> RunOnMainThread<T>(Func<Task<T>> func)
        {
            var task = await WindowManager.Instance.RunOnMainThread(func).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
