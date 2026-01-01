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
using FenBrowser.Core.Logging;
using FenBrowser.Core;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// WebDriver HTTP server implementing W3C WebDriver protocol.
    /// Uses modular command handlers via WebDriverRouter for all operations.
    /// </summary>
    public class WebDriverServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly WebDriverRouter _router;
        private bool _running;

        public WebDriverServer(IBrowser browser, int port = 4444)
        {
            _router = new WebDriverRouter(browser);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void Start()
        {
            if (_running) return;
            try
            {
                _listener.Start();
                _running = true;
                Task.Run(ListenLoop);
                FenLogger.Info($"WebDriver server started on {_listener.Prefixes.First()}", LogCategory.WebDriver);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"Failed to start WebDriver server: {ex.Message}", LogCategory.WebDriver);
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
                catch (Exception ex) { FenLogger.Error($"WebDriver listen error: {ex}", LogCategory.WebDriver); }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;
            var method = req.HttpMethod;
            var path = req.Url.AbsolutePath.TrimEnd('/');
            
            FenLogger.Debug($"[WebDriver] {method} {path}", LogCategory.WebDriver);

            try
            {
                // Read JSON body if present
                var body = default(JsonElement);
                if (req.HasEntityBody)
                {
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                    {
                        var text = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            body = JsonDocument.Parse(text).RootElement;
                        }
                    }
                }

                // Route to appropriate handler
                var response = await _router.RouteAsync(method, path, body);

                // Send response
                res.StatusCode = response.StatusCode;
                SendResponse(res, response);
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                SendResponse(res, WebDriverResponse.Error500(ex.Message));
            }
            finally
            {
                res.Close();
            }
        }

        private void SendResponse(HttpListenerResponse res, WebDriverResponse response)
        {
            object payload;
            if (response.Error != null)
            {
                payload = new { value = new { error = response.Error, message = response.Message, stacktrace = "" } };
            }
            else
            {
                payload = new { value = response.Value };
            }

            var json = JsonSerializer.Serialize(payload);
            
            // Debug: log the response for troubleshooting
            FenLogger.Debug($"[WebDriver Response] {json}", LogCategory.WebDriver);
            
            var buffer = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            res.AddHeader("Cache-Control", "no-cache");
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
        }
    }
}
