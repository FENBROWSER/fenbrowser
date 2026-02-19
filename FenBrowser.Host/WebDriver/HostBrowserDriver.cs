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
        private TabManager _tabs => TabManager.Instance;

        public async Task NavigateAsync(string url)
        {
            await RunOnMainThread(async () =>
            {
                if (_tabs.ActiveTab == null)
                {
                    _tabs.CreateTab(url);
                }
                else
                {
                    await _tabs.ActiveTab.NavigateAsync(url);
                }
            });
        }

        public async Task<string> GetCurrentUrlAsync()
        {
            return await RunOnMainThread(() => _tabs.ActiveTab?.Url ?? "about:blank");
        }

        public async Task<string> GetTitleAsync()
        {
            return await RunOnMainThread(() => _tabs.ActiveTab?.Title ?? "");
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
                    await _tabs.ActiveTab.NavigateAsync(_tabs.ActiveTab.Url);
                }
            });
        }

        public async Task<object> FindElementAsync(string strategy, string selector, object parentElement = null)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return null;
                var parentId = parentElement as string;
                var id = await host.FindElementAsync(strategy, selector, parentId);
                return (object)id;
            });
        }

        public async Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return Array.Empty<object>();
                var parentId = parentElement as string;
                var ids = await host.FindElementsAsync(strategy, selector, parentId);
                return ids.Select(id => (object)id).ToArray();
            });
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

        public async Task<bool> IsElementSelectedAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return false;
                return await host.IsElementSelectedAsync(id);
            });
        }

        public async Task<object> GetElementPropertyAsync(object element, string name)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return null;
                return await host.GetElementPropertyAsync(id, name);
            });
        }

        public async Task<string> GetElementCssValueAsync(object element, string propertyName)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.GetElementCssValueAsync(id, propertyName);
            });
        }

        public async Task<string> GetElementTextAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.GetElementTextAsync(id);
            });
        }

        public async Task<string> GetElementTagNameAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.GetElementTagNameAsync(id);
            });
        }

        public async Task<WdElementRect> GetElementRectAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return new WdElementRect();
                var rect = await host.GetElementRectAsync(id);
                return new WdElementRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
            });
        }

        public async Task<bool> IsElementEnabledAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return false;
                return await host.IsElementEnabledAsync(id);
            });
        }

        public async Task<string> GetElementComputedRoleAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.GetElementComputedRoleAsync(id);
            });
        }

        public async Task<string> GetElementComputedLabelAsync(object element)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
                return await host.GetElementComputedLabelAsync(id);
            });
        }

        public async Task ClickElementAsync(object element)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return;
                await host.ClickElementAsync(id);
            });
        }

        public async Task ClearElementAsync(object element)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return;
                await host.ClearElementAsync(id);
            });
        }

        public async Task SendKeysAsync(object element, string text)
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return;
                await host.SendKeysToElementAsync(id, text ?? string.Empty);
            });
        }

        public async Task<string> GetElementAttributeAsync(object element, string name)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null || element is not string id) return "";
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
                if (host == null) return null;
                return await host.ExecuteScriptAsync(script, args);
            });
        }

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return null;
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
            var host = _tabs.ActiveTab?.Browser?.Host;
            if (host == null) return (0, 0, 800, 600);
            var rect = host.GetWindowRect();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public void SetWindowRect(int? x, int? y, int? width, int? height)
        {
            var host = _tabs.ActiveTab?.Browser?.Host;
            host?.SetWindowRect(x, y, width, height);
        }

        public (int x, int y, int width, int height) MaximizeWindow()
        {
            var host = _tabs.ActiveTab?.Browser?.Host;
            if (host == null) return (0, 0, 800, 600);
            var rect = host.MaximizeWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) MinimizeWindow()
        {
            var host = _tabs.ActiveTab?.Browser?.Host;
            if (host == null) return (0, 0, 800, 600);
            var rect = host.MinimizeWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public (int x, int y, int width, int height) FullscreenWindow()
        {
            var host = _tabs.ActiveTab?.Browser?.Host;
            if (host == null) return (0, 0, 800, 600);
            var rect = host.FullscreenWindow();
            return (rect.X, rect.Y, rect.Width, rect.Height);
        }

        public async Task<string> NewWindowAsync(string typeHint)
        {
            return await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host != null)
                {
                    await host.CreateNewTabAsync();
                }

                // Host uses tab manager as source-of-truth; WebDriver session tracks handles independently.
                return Guid.NewGuid().ToString("N");
            });
        }

        public async Task SwitchToWindowAsync(string windowHandle)
        {
            await RunOnMainThread(() =>
            {
                // Session-level handle switching is managed by WebDriver session state.
                return Task.CompletedTask;
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
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
                var mapped = (actions ?? Array.Empty<WdActionSequence>()).Select(ToEngineActionChain).ToList();
                await host.PerformActionsAsync(mapped);
            });
        }

        public async Task ReleaseActionsAsync()
        {
            await RunOnMainThread(async () =>
            {
                var host = _tabs.ActiveTab?.Browser?.Host;
                if (host == null) return;
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
        private Task RunOnMainThread(Func<Task> func) => WindowManager.Instance.RunOnMainThread(func);
        private async Task<T> RunOnMainThread<T>(Func<Task<T>> func)
        {
            var task = await WindowManager.Instance.RunOnMainThread(func);
            return await task;
        }
    }
}
