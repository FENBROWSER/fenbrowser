using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Integration layer between WebDriver and MainWindow.
    /// Provides real window operations, screenshot capture, and tab management.
    /// </summary>
    public static class WebDriverIntegration
    {
        private static Window _mainWindow;
        private static Control _browserContainer;
        private static Func<Task<string>> _createTabCallback;
        private static Func<string, bool> _closeTabCallback;
        private static Func<string, bool> _switchTabCallback;
        private static Func<IEnumerable<string>> _getTabHandlesCallback;
        private static Func<string> _getCurrentTabCallback;

        /// <summary>
        /// Registers the main window for WebDriver operations.
        /// Call this from MainWindow constructor.
        /// </summary>
        public static void RegisterMainWindow(
            Window window,
            Control browserContainer,
            Func<Task<string>> createTab = null,
            Func<string, bool> closeTab = null,
            Func<string, bool> switchTab = null,
            Func<IEnumerable<string>> getTabHandles = null,
            Func<string> getCurrentTab = null)
        {
            _mainWindow = window;
            _browserContainer = browserContainer;
            _createTabCallback = createTab;
            _closeTabCallback = closeTab;
            _switchTabCallback = switchTab;
            _getTabHandlesCallback = getTabHandles;
            _getCurrentTabCallback = getCurrentTab;
        }

        /// <summary>
        /// Updates the browser container reference (for screenshot capture).
        /// </summary>
        public static void UpdateBrowserContainer(Control container)
        {
            _browserContainer = container;
        }

        #region Window Operations

        public static WindowRect GetWindowRect()
        {
            if (_mainWindow == null)
                return new WindowRect { X = 0, Y = 0, Width = 1100, Height = 700 };

            return Dispatcher.UIThread.Invoke(() =>
            {
                var pos = _mainWindow.Position;
                var bounds = _mainWindow.Bounds;
                return new WindowRect
                {
                    X = pos.X,
                    Y = pos.Y,
                    Width = (int)bounds.Width,
                    Height = (int)bounds.Height
                };
            });
        }

        public static WindowRect SetWindowRect(int? x, int? y, int? width, int? height)
        {
            if (_mainWindow == null)
                return GetWindowRect();

            return Dispatcher.UIThread.Invoke(() =>
            {
                var currentPos = _mainWindow.Position;
                var currentBounds = _mainWindow.Bounds;

                // Set position
                if (x.HasValue || y.HasValue)
                {
                    _mainWindow.Position = new PixelPoint(
                        x ?? currentPos.X,
                        y ?? currentPos.Y);
                }

                // Set size
                if (width.HasValue)
                    _mainWindow.Width = width.Value;
                if (height.HasValue)
                    _mainWindow.Height = height.Value;

                return GetWindowRect();
            });
        }

        public static WindowRect MaximizeWindow()
        {
            if (_mainWindow == null)
                return GetWindowRect();

            Dispatcher.UIThread.Invoke(() =>
            {
                _mainWindow.WindowState = WindowState.Maximized;
            });

            return GetWindowRect();
        }

        public static WindowRect MinimizeWindow()
        {
            if (_mainWindow == null)
                return GetWindowRect();

            Dispatcher.UIThread.Invoke(() =>
            {
                _mainWindow.WindowState = WindowState.Minimized;
            });

            return GetWindowRect();
        }

        public static WindowRect FullscreenWindow()
        {
            if (_mainWindow == null)
                return GetWindowRect();

            Dispatcher.UIThread.Invoke(() =>
            {
                _mainWindow.WindowState = WindowState.FullScreen;
            });

            return GetWindowRect();
        }

        #endregion

        #region Tab Operations

        public static async Task<string> CreateNewTabAsync()
        {
            if (_createTabCallback == null)
                return Guid.NewGuid().ToString();

            return await _createTabCallback();
        }

        public static bool CloseTab(string handle)
        {
            return _closeTabCallback?.Invoke(handle) ?? false;
        }

        public static bool SwitchToTab(string handle)
        {
            return _switchTabCallback?.Invoke(handle) ?? false;
        }

        public static IEnumerable<string> GetTabHandles()
        {
            return _getTabHandlesCallback?.Invoke() ?? new List<string>();
        }

        public static string GetCurrentTabHandle()
        {
            return _getCurrentTabCallback?.Invoke() ?? "default";
        }

        #endregion

        #region Screenshot Operations

        /// <summary>
        /// Captures a screenshot of the browser container and returns base64 PNG.
        /// </summary>
        public static async Task<string> CaptureScreenshotAsync()
        {
            if (_browserContainer == null)
                return "";

            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    var bounds = _browserContainer.Bounds;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        return "";

                    var pixelSize = new PixelSize((int)bounds.Width, (int)bounds.Height);
                    var dpi = new Vector(96, 96);

                    using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
                    bitmap.Render(_browserContainer);

                    using var stream = new MemoryStream();
                    bitmap.Save(stream);
                    stream.Position = 0;

                    return Convert.ToBase64String(stream.ToArray());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Screenshot failed: {ex.Message}");
                    return "";
                }
            });
        }

        /// <summary>
        /// Captures a screenshot of a specific element by its visual.
        /// </summary>
        public static async Task<string> CaptureElementScreenshotAsync(Control elementVisual)
        {
            if (elementVisual == null)
                return "";

            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    var bounds = elementVisual.Bounds;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        return "";

                    var pixelSize = new PixelSize((int)bounds.Width, (int)bounds.Height);
                    var dpi = new Vector(96, 96);

                    using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
                    bitmap.Render(elementVisual);

                    using var stream = new MemoryStream();
                    bitmap.Save(stream);
                    stream.Position = 0;

                    return Convert.ToBase64String(stream.ToArray());
                }
                catch
                {
                    return "";
                }
            });
        }

        #endregion
    }
}
