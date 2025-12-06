using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles window and frame management WebDriver commands.
    /// Endpoints: Window handles, rect, maximize, minimize, fullscreen, frame switching
    /// </summary>
    public class WindowCommands : IWebDriverCommand
    {
        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // Window handle operations
            if (Regex.IsMatch(path, @"/session/[^/]+/window/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/window/handles/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/window/new/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/window/rect/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/window/maximize/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/window/minimize/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/window/fullscreen/?$")) return true;

            // Frame operations
            if (Regex.IsMatch(path, @"/session/[^/]+/frame/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/frame/parent/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');

            // GET /session/{id}/window - Get current window handle
            if (context.Method == "GET" && path.EndsWith("/window"))
            {
                return WebDriverResponse.Success(context.Session.CurrentWindowHandle);
            }

            // DELETE /session/{id}/window - Close current window
            if (context.Method == "DELETE" && path.EndsWith("/window"))
            {
                var currentHandle = context.Session.CurrentWindowHandle;
                if (!context.Session.CloseWindow(currentHandle))
                {
                    return WebDriverResponse.Error400("Cannot close the last window");
                }
                // Switch to another window if available
                if (context.Session.WindowHandles.Count > 0)
                {
                    context.Session.CurrentWindowHandle = context.Session.WindowHandles[0];
                }
                return WebDriverResponse.Success(null);
            }

            // POST /session/{id}/window - Switch to window
            if (context.Method == "POST" && path.EndsWith("/window") && !path.Contains("/new") && !path.Contains("/rect"))
            {
                if (!context.Body.TryGetProperty("handle", out var handleProp))
                    return WebDriverResponse.Error400("Missing 'handle' parameter");

                var handle = handleProp.GetString();
                if (!context.Session.SwitchToWindow(handle))
                    return WebDriverResponse.NoSuchWindow($"Window handle '{handle}' not found");

                return WebDriverResponse.Success(null);
            }

            // GET /session/{id}/window/handles - Get all window handles
            if (context.Method == "GET" && path.EndsWith("/window/handles"))
            {
                return WebDriverResponse.Success(context.Session.WindowHandles);
            }

            // POST /session/{id}/window/new - Create new window
            if (context.Method == "POST" && path.EndsWith("/window/new"))
            {
                var typeHint = "tab";
                if (context.Body.TryGetProperty("type", out var typeProp))
                    typeHint = typeProp.GetString();

                var handle = context.Session.CreateWindow();
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.CreateNewTabAsync());

                return WebDriverResponse.Success(new { handle, type = typeHint });
            }

            // GET /session/{id}/window/rect - Get window rectangle
            if (context.Method == "GET" && path.EndsWith("/window/rect"))
            {
                var rect = await Dispatcher.UIThread.InvokeAsync(() =>
                    context.Browser.GetWindowRect());
                return WebDriverResponse.Success(new
                {
                    x = rect.X,
                    y = rect.Y,
                    width = rect.Width,
                    height = rect.Height
                });
            }

            // POST /session/{id}/window/rect - Set window rectangle
            if (context.Method == "POST" && path.EndsWith("/window/rect"))
            {
                int? x = null, y = null, width = null, height = null;

                if (context.Body.TryGetProperty("x", out var xProp) && xProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                    x = xProp.GetInt32();
                if (context.Body.TryGetProperty("y", out var yProp) && yProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                    y = yProp.GetInt32();
                if (context.Body.TryGetProperty("width", out var wProp) && wProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                    width = wProp.GetInt32();
                if (context.Body.TryGetProperty("height", out var hProp) && hProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                    height = hProp.GetInt32();

                var newRect = await Dispatcher.UIThread.InvokeAsync(() =>
                    context.Browser.SetWindowRect(x, y, width, height));

                return WebDriverResponse.Success(new
                {
                    x = newRect.X,
                    y = newRect.Y,
                    width = newRect.Width,
                    height = newRect.Height
                });
            }

            // POST /session/{id}/window/maximize
            if (context.Method == "POST" && path.EndsWith("/window/maximize"))
            {
                var rect = await Dispatcher.UIThread.InvokeAsync(() =>
                    context.Browser.MaximizeWindow());
                return WebDriverResponse.Success(new
                {
                    x = rect.X,
                    y = rect.Y,
                    width = rect.Width,
                    height = rect.Height
                });
            }

            // POST /session/{id}/window/minimize
            if (context.Method == "POST" && path.EndsWith("/window/minimize"))
            {
                var rect = await Dispatcher.UIThread.InvokeAsync(() =>
                    context.Browser.MinimizeWindow());
                return WebDriverResponse.Success(new
                {
                    x = rect.X,
                    y = rect.Y,
                    width = rect.Width,
                    height = rect.Height
                });
            }

            // POST /session/{id}/window/fullscreen
            if (context.Method == "POST" && path.EndsWith("/window/fullscreen"))
            {
                var rect = await Dispatcher.UIThread.InvokeAsync(() =>
                    context.Browser.FullscreenWindow());
                return WebDriverResponse.Success(new
                {
                    x = rect.X,
                    y = rect.Y,
                    width = rect.Width,
                    height = rect.Height
                });
            }

            // POST /session/{id}/frame - Switch to frame
            if (context.Method == "POST" && path.EndsWith("/frame") && !path.Contains("/parent"))
            {
                // Frame can be null (top), number (index), or element
                object frameId = null;
                if (context.Body.TryGetProperty("id", out var idProp))
                {
                    if (idProp.ValueKind == System.Text.Json.JsonValueKind.Null)
                        frameId = null;
                    else if (idProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                        frameId = idProp.GetInt32();
                    else if (idProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // Element reference
                        if (idProp.TryGetProperty("element-6066-11e4-a52e-4f735466cecf", out var elementIdProp))
                            frameId = elementIdProp.GetString();
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.SwitchToFrameAsync(frameId));
                return WebDriverResponse.Success(null);
            }

            // POST /session/{id}/frame/parent - Switch to parent frame
            if (context.Method == "POST" && path.EndsWith("/frame/parent"))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.SwitchToParentFrameAsync());
                return WebDriverResponse.Success(null);
            }

            return WebDriverResponse.Error404("Window command not found");
        }
    }
}
