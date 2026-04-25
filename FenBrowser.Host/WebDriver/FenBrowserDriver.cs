// =============================================================================
// FenBrowserDriver.cs
// Implementation of IBrowserDriver for FenBrowser.Host
// =============================================================================

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
    /// <summary>
    /// Bridges the WebDriver server and BrowserIntegration.
    /// </summary>
    public class FenBrowserDriver : IBrowserDriver
    {
        private readonly BrowserIntegration _integration;

        public FenBrowserDriver(BrowserIntegration integration)
        {
            _integration = integration;
        }

        private BrowserHost Host => _integration?.Host;

        public async Task NavigateAsync(string url)
        {
            await _integration.NavigateProgrammaticAsync(url);
        }

        public async Task<string> GetCurrentUrlAsync()
        {
            if (Host != null)
            {
                if (!Host.HasValidCurrentBrowsingContext())
                {
                    throw new InvalidOperationException("Current browsing context is no longer open");
                }

                var currentUrl = await Host.GetCurrentUrlAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(currentUrl) ? "about:blank" : currentUrl;
            }

            return _integration.CurrentUrl;
        }
        public Task<string> GetTitleAsync() => Host?.GetTitleAsync() ?? Task.FromResult("FenBrowser");
        public Task<string> GetWindowHandleAsync()
        {
            return Task.FromResult(TabManager.Instance.ActiveTab?.Id.ToString() ?? string.Empty);
        }

        public Task<IReadOnlyList<string>> GetWindowHandlesAsync()
        {
            return Task.FromResult((IReadOnlyList<string>)TabManager.Instance.Tabs.Select(tab => tab.Id.ToString()).ToList());
        }
        public Task CloseWindowAsync()
        {
            var tabs = TabManager.Instance;
            if (tabs.ActiveTab != null)
            {
                tabs.CloseActiveTab();
            }

            return Task.CompletedTask;
        }
        public Task GoBackAsync() => _integration.GoBackAsync();
        public Task GoForwardAsync() => _integration.GoForwardAsync();
        public Task RefreshAsync() => _integration.RefreshAsync();

        public async Task<object> FindElementAsync(string strategy, string selector, object parentElement = null)
        {
            if (Host == null) return null;
            var parentId = parentElement as string;
            var id = await Host.FindElementAsync(strategy, selector, parentId);
            return id;
        }

        public async Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null)
        {
            if (Host == null) return Array.Empty<object>();
            var parentId = parentElement as string;
            var ids = await Host.FindElementsAsync(strategy, selector, parentId);
            return ids.Select(id => (object)id).ToArray();
        }

        public async Task<object> GetActiveElementAsync()
        {
            if (Host == null) return null;
            return await Host.GetActiveElementAsync();
        }

        public async Task<object> GetShadowRootAsync(object element)
        {
            if (Host == null || element is not string id) return null;
            return await Host.GetShadowRootAsync(id);
        }

        public async Task<bool> IsElementSelectedAsync(object element)
        {
            if (Host == null || element is not string id) return false;
            return await Host.IsElementSelectedAsync(id);
        }

        public async Task<object> GetElementPropertyAsync(object element, string name)
        {
            if (Host == null || element is not string id) return null;
            return await Host.GetElementPropertyAsync(id, name);
        }

        public async Task<string> GetElementCssValueAsync(object element, string propertyName)
        {
            if (Host == null || element is not string id) return "";
            return await Host.GetElementCssValueAsync(id, propertyName);
        }

        public async Task<string> GetElementTextAsync(object element)
        {
            if (Host == null || element is not string id) return "";
            return await Host.GetElementTextAsync(id);
        }

        public async Task<string> GetElementTagNameAsync(object element)
        {
            if (Host == null || element is not string id) return "";
            return await Host.GetElementTagNameAsync(id);
        }

        public async Task<WdElementRect> GetElementRectAsync(object element)
        {
            if (Host == null || element is not string id) return new WdElementRect();
            var rect = await Host.GetElementRectAsync(id);
            return new WdElementRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
        }

        public async Task<bool> IsElementEnabledAsync(object element)
        {
            if (Host == null || element is not string id) return false;
            return await Host.IsElementEnabledAsync(id);
        }

        public async Task<string> GetElementComputedRoleAsync(object element)
        {
            if (Host == null || element is not string id) return "";
            return await Host.GetElementComputedRoleAsync(id);
        }

        public async Task<string> GetElementComputedLabelAsync(object element)
        {
            if (Host == null || element is not string id) return "";
            return await Host.GetElementComputedLabelAsync(id);
        }

        public async Task ClickElementAsync(object element)
        {
            if (Host == null || element is not string id) return;
            await Host.ClickElementAsync(id);
        }

        public async Task ClearElementAsync(object element)
        {
            if (Host == null || element is not string id) return;
            await Host.ClearElementAsync(id);
        }

        public async Task SendKeysAsync(object element, string text, bool strictFileInteractability = false)
        {
            if (Host == null || element is not string id) return;
            await Host.SendKeysToElementAsync(id, text ?? string.Empty, strictFileInteractability);
        }

        public async Task<string> GetElementAttributeAsync(object element, string name)
        {
            if (Host == null || element is not string id) return "";
            return await Host.GetElementAttributeAsync(id, name);
        }

        public Task<string> GetPageSourceAsync()
        {
            if (Host == null) return Task.FromResult("<html></html>");
            return Host.GetPageSourceAsync();
        }

        public Task<object> ExecuteScriptAsync(string script, object[] args)
        {
            if (Host == null) return Task.FromResult<object>(null);
            return Host.ExecuteScriptAsync(script, args);
        }

        public Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout)
        {
            if (Host == null) return Task.FromResult<object>(null);
            return Host.ExecuteAsyncScriptAsync(script, args, timeout);
        }

        public Task<string> TakeScreenshotAsync()
        {
            return _integration.CaptureScreenshotAsync();
        }

        public async Task<string> TakeElementScreenshotAsync(object element)
        {
            if (Host == null || element is not string id) return "";
            return await Host.CaptureElementScreenshotAsync(id);
        }

        public async Task<string> PrintPageAsync(WdPrintOptions options)
        {
            if (Host == null) return "";
            var page = options?.Page ?? new WdPrintPageOptions();
            var landscape = string.Equals(options?.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
            var scale = options?.Scale ?? 1.0;
            return await Host.PrintToPdfAsync(page.Width, page.Height, landscape, scale);
        }

        public (int x, int y, int width, int height) GetWindowRect()
        {
            if (Host == null) return (0, 0, 1024, 768);
            var rect = Host.GetWindowRect();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public void SetWindowRect(int? x, int? y, int? width, int? height)
        {
            Host?.SetWindowRect(x, y, width, height);
        }

        public (int x, int y, int width, int height) MaximizeWindow()
        {
            if (Host == null) return (0, 0, 1024, 768);
            var rect = Host.MaximizeWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) MinimizeWindow()
        {
            if (Host == null) return (0, 0, 1024, 768);
            var rect = Host.MinimizeWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) FullscreenWindow()
        {
            if (Host == null) return (0, 0, 1024, 768);
            var rect = Host.FullscreenWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public async Task<string> NewWindowAsync(string typeHint)
        {
            var tabs = TabManager.Instance;
            var beforeActiveTabId = tabs.ActiveTab?.Id;
            var beforeCount = tabs.Tabs.Count;
            tabs.CreateTab();

            // Guarantee a new top-level browsing context and handle.
            if (tabs.Tabs.Count <= beforeCount || tabs.ActiveTab?.Id == beforeActiveTabId)
            {
                tabs.CreateTab();
            }

            var activeTab = tabs.ActiveTab;
            if (activeTab != null)
            {
                return activeTab.Id.ToString();
            }

            return beforeActiveTabId?.ToString() ?? Guid.NewGuid().ToString("N");
        }

        public Task SwitchToWindowAsync(string windowHandle)
        {
            if (!int.TryParse(windowHandle, out var tabId))
            {
                throw new InvalidOperationException($"Invalid window handle format: {windowHandle}");
            }

            var tabs = TabManager.Instance;
            var switched = false;
            for (int i = 0; i < tabs.Tabs.Count; i++)
            {
                if (tabs.Tabs[i].Id == tabId)
                {
                    tabs.SwitchToTab(i);
                    switched = true;
                    break;
                }
            }

            if (!switched)
            {
                throw new InvalidOperationException($"No such window handle in tab manager: {windowHandle}");
            }

            return Task.CompletedTask;
        }

        public Task SwitchToFrameAsync(object frameReference)
        {
            if (Host == null) return Task.CompletedTask;
            return Host.SwitchToFrameAsync(frameReference);
        }

        public Task SwitchToParentFrameAsync()
        {
            if (Host == null) return Task.CompletedTask;
            return Host.SwitchToParentFrameAsync();
        }

        public async Task<IReadOnlyList<WdCookie>> GetAllCookiesAsync()
        {
            if (Host == null) return Array.Empty<WdCookie>();
            var cookies = await Host.GetAllCookiesAsync();
            return cookies.Select(ToWdCookie).ToList();
        }

        public async Task<WdCookie> GetNamedCookieAsync(string name)
        {
            if (Host == null) return null;
            var cookie = await Host.GetCookieAsync(name);
            return cookie == null ? null : ToWdCookie(cookie);
        }

        public Task AddCookieAsync(WdCookie cookie)
        {
            if (Host == null) return Task.CompletedTask;
            return Host.AddCookieAsync(ToEngineCookie(cookie));
        }

        public Task DeleteCookieAsync(string name)
        {
            if (Host == null) return Task.CompletedTask;
            return Host.DeleteCookieAsync(name);
        }

        public Task DeleteAllCookiesAsync()
        {
            if (Host == null) return Task.CompletedTask;
            return Host.DeleteAllCookiesAsync();
        }

        public Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions)
        {
            if (Host == null) return Task.CompletedTask;
            var mapped = (actions ?? Array.Empty<WdActionSequence>()).Select(ToEngineActionChain).ToList();
            return Host.PerformActionsAsync(mapped);
        }

        public Task ReleaseActionsAsync()
        {
            if (Host == null) return Task.CompletedTask;
            return Host.ReleaseActionsAsync();
        }

        public Task<bool> HasAlertAsync()
        {
            if (Host == null) return Task.FromResult(false);
            return Host.HasAlertAsync();
        }

        public Task DismissAlertAsync()
        {
            if (Host == null) return Task.CompletedTask;
            return Host.DismissAlertAsync();
        }

        public Task AcceptAlertAsync()
        {
            if (Host == null) return Task.CompletedTask;
            return Host.AcceptAlertAsync();
        }

        public Task<string> GetAlertTextAsync()
        {
            if (Host == null) return Task.FromResult(string.Empty);
            return Host.GetAlertTextAsync();
        }

        public Task SendAlertTextAsync(string text)
        {
            if (Host == null) return Task.CompletedTask;
            return Host.SendAlertTextAsync(text ?? string.Empty);
        }

        public bool HasValidCurrentBrowsingContext()
        {
            return Host == null || Host.HasValidCurrentBrowsingContext();
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
    }
}
