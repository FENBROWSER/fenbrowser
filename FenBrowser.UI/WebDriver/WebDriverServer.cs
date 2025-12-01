using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Linq;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver
{
    public class WebDriverServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly IBrowser _browser;
        private bool _running;
        private string _sessionId;

        public WebDriverServer(IBrowser browser, int port = 4444)
        {
            _browser = browser;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
        }

        public void Start()
        {
            if (_running) return;
            try
            {
                _listener.Start();
                _running = true;
                Task.Run(ListenLoop);
                System.Diagnostics.Debug.WriteLine($"WebDriver server started on {_listener.Prefixes.First()}");
                try { System.IO.File.AppendAllText("debug_log.txt", $"[WebDriver] Started on {_listener.Prefixes.First()}\r\n"); } catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start WebDriver server: {ex.Message}");
                try { System.IO.File.AppendAllText("debug_log.txt", $"[WebDriver] Failed to start: {ex.Message}\r\n"); } catch { }
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }

        public void Dispose() => Stop();

        private async Task ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleRequestAsync(context);
                }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WebDriver listen error: {ex}"); }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;
            var method = req.HttpMethod;
            var path = req.Url.AbsolutePath.TrimEnd('/');
            
            Console.WriteLine($"[WebDriver] {method} {path}");

            try
            {
                object responseData = null;

                if (method == "POST" && path.EndsWith("/session"))
                {
                    _sessionId = Guid.NewGuid().ToString();
                    responseData = new { sessionId = _sessionId, capabilities = new { browserName = "FenBrowser" } };
                }
                else if (method == "DELETE" && path.Contains("/session/"))
                {
                    _sessionId = null;
                    responseData = null;
                }
                else if (method == "POST" && path.EndsWith("/url"))
                {
                    var body = await ReadJsonBody(req);
                    if (body.TryGetProperty("url", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        await Dispatcher.UIThread.InvokeAsync(async () => await _browser.NavigateAsync(url));
                    }
                    responseData = null;
                }
                else if (method == "GET" && path.EndsWith("/title"))
                {
                    var title = await Dispatcher.UIThread.InvokeAsync(async () => await _browser.GetTitleAsync());
                    responseData = title; 
                }
                else if (method == "GET" && path.EndsWith("/screenshot"))
                {
                    var base64 = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await _browser.CaptureScreenshotAsync();
                        return ""; // or return the actual base64 string if CaptureScreenshotAsync returns it
                    });
                    responseData = base64;
                }
                else if (method == "POST" && path.EndsWith("/element"))
                {
                    var body = await ReadJsonBody(req);
                    var strategy = body.GetProperty("using").GetString();
                    var value = body.GetProperty("value").GetString();
                    var id = await Dispatcher.UIThread.InvokeAsync(async () => await _browser.FindElementAsync(strategy, value));
                    responseData = new Dictionary<string, string> { { "element-6066-11e4-a52e-4f735466cecf", id } };
                }
                else if (method == "POST" && path.Contains("/click"))
                {
                    var parts = path.Split('/');
                    // /session/{id}/element/{elementId}/click
                    // parts: "", "session", "{id}", "element", "{elementId}", "click"
                    if (parts.Length >= 6 && parts[3] == "element" && parts[5] == "click")
                    {
                        var elementId = parts[4];
                        await Dispatcher.UIThread.InvokeAsync(async () => await _browser.ClickElementAsync(elementId));
                        responseData = null;
                    }
                    else
                    {
                        throw new Exception("Invalid click path");
                    }
                }
                else if (method == "POST" && path.EndsWith("/execute/sync"))
                {
                    var body = await ReadJsonBody(req);
                    var script = body.GetProperty("script").GetString();
                    // var args = body.GetProperty("args"); // Ignore args for now
                    var result = await Dispatcher.UIThread.InvokeAsync(async () => await _browser.ExecuteScriptAsync(script));
                    responseData = result;
                }
                else
                {
                    res.StatusCode = 404;
                    responseData = new { error = "unknown command", message = $"Command {method} {path} not found" };
                }

                SendResponse(res, responseData);
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                SendResponse(res, new { error = "unknown error", message = ex.Message });
            }
            finally
            {
                res.Close();
            }
        }

        private async Task<JsonElement> ReadJsonBody(HttpListenerRequest req)
        {
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            {
                var text = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(text)) return default;
                return JsonDocument.Parse(text).RootElement;
            }
        }

        private void SendResponse(HttpListenerResponse res, object data)
        {
            var json = JsonSerializer.Serialize(new { value = data });
            var buffer = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
        }
    }
}
