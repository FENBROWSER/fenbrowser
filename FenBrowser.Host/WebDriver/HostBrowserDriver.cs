using System;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Commands;
using FenBrowser.Host.Tabs;
using FenBrowser.FenEngine.Scripting;
using System.Linq;

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
                 // TODO: Implement History Back
                 await Task.CompletedTask;
             });
        }

        public async Task GoForwardAsync()
        {
            await RunOnMainThread(async () =>
            {
               // TODO: Implement History Forward
               await Task.CompletedTask;
            });
        }

        public async Task RefreshAsync()
        {
            await RunOnMainThread(async () =>
            {
                 if (_tabs.ActiveTab != null)
                     await _tabs.ActiveTab.NavigateAsync(_tabs.ActiveTab.Url);
            });
        }

        public async Task<object> FindElementAsync(string strategy, string selector)
        {
            return await RunOnMainThread(async () => 
            {
                if (_tabs.ActiveTab?.Browser?.Host == null) return null;
                // BrowserHost returns a string ID
                var id = await _tabs.ActiveTab.Browser.Host.FindElementAsync(strategy, selector);
                return (object)id;
            });
        }

        public async Task<object[]> FindElementsAsync(string strategy, string selector)
        {
            return await RunOnMainThread(async () => 
            {
                if (_tabs.ActiveTab?.Browser?.Host == null) return Array.Empty<object>();
                var ids = await _tabs.ActiveTab.Browser.Host.FindElementsAsync(strategy, selector);
                return ids.Select(id => (object)id).ToArray();
            });
        }

        public async Task<string> GetElementTextAsync(object element)
        {
             return await RunOnMainThread(async () => 
            {
                if (_tabs.ActiveTab?.Browser?.Host == null) return "";
                if (element is string id)
                {
                    return await _tabs.ActiveTab.Browser.Host.GetElementTextAsync(id);
                }
                return "";
            });
        }
        
        public async Task ClickElementAsync(object element)
        {
            await RunOnMainThread(async () => 
            {
                if (_tabs.ActiveTab?.Browser?.Host == null) return;
                if (element is string id)
                {
                    await _tabs.ActiveTab.Browser.Host.ClickElementAsync(id);
                }
            });
        }

        public async Task SendKeysAsync(object element, string text)
        {
             await RunOnMainThread(async () => 
            {
                if (_tabs.ActiveTab?.Browser?.Host == null) return;
                if (element is string id)
                {
                    await _tabs.ActiveTab.Browser.Host.SendKeysToElementAsync(id, text);
                }
            });
        }

        public async Task<string> GetElementAttributeAsync(object element, string name)
        {
             return await RunOnMainThread(async () => 
            {
                if (_tabs.ActiveTab?.Browser?.Host == null) return "";
                if (element is string id)
                {
                    return await _tabs.ActiveTab.Browser.Host.GetElementAttributeAsync(id, name);
                }
                return "";
            });
        }

        public async Task<object> ExecuteScriptAsync(string script, object[] args)
        {
             return await RunOnMainThread(async () => 
             {
                 if (_tabs.ActiveTab?.Browser?.Host == null) return null;
                 return await _tabs.ActiveTab.Browser.Host.ExecuteScriptAsync(script, args);
             });
        }

        public async Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout)
        {
            // ExecuteAsyncScript delegates directly to Engine
             return await RunOnMainThread(async () => 
             {
                 if (_tabs.ActiveTab?.Browser?.Host == null) return null;
                 return await _tabs.ActiveTab.Browser.Host.ExecuteAsyncScriptAsync(script, args, timeout);
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

        public (int x, int y, int width, int height) GetWindowRect()
        {
            // Main thread?
            // WindowManager has window info
            return (0, 0, 800, 600); 
        }

        public void SetWindowRect(int? x, int? y, int? width, int? height)
        {
            // Resize window logic
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
