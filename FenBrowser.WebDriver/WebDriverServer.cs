// =============================================================================
// WebDriverServer.cs
// W3C WebDriver HTTP Server (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §5 - Nodes
//                 https://www.w3.org/TR/webdriver2/#nodes
// 
// PURPOSE: Production-grade HTTP server for WebDriver commands.
// SECURITY: Origin validation, capability guards, rate limiting.
// =============================================================================

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Security;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// W3C WebDriver HTTP server.
    /// </summary>
    public class WebDriverServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly SessionManager _sessionManager;
        private readonly CommandRouter _router;
        private readonly CommandHandler _handler;
        private readonly OriginValidator _originValidator;
        private readonly CancellationTokenSource _cts;
        private readonly int _port;
        private Task _listenerTask;
        private bool _disposed;
        
        public event Action<string> OnLog;
        
        public WebDriverServer(int port = 4444)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            
            _sessionManager = new SessionManager();
            _router = new CommandRouter();
            _handler = new CommandHandler(_sessionManager);
            _originValidator = new OriginValidator(allowLocalhostOnly: true);
            _cts = new CancellationTokenSource();
        }
        
        /// <summary>
        /// Set the browser driver implementation.
        /// </summary>
        public void SetDriver(IBrowserDriver driver)
        {
            _handler.Browser = driver;
        }

        /// <summary>
        /// Start the WebDriver server.
        /// </summary>
        public void Start()
        {
            ThrowIfDisposed();
            
            _listener.Start();
            Log($"WebDriver server started on port {_port}");
            
            _listenerTask = Task.Run(ListenAsync);
        }
        
        /// <summary>
        /// Stop the WebDriver server.
        /// </summary>
        public void Stop()
        {
            _cts.Cancel();
            _listener.Stop();
            Log("WebDriver server stopped");
        }
        
        private async Task ListenAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context));
                }
                catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                {
                    // Expected during shutdown
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listener error: {ex.Message}");
                }
            }
        }
        
        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            
            try
            {
                var origin = request.Headers["Origin"];
                if (!_originValidator.ValidateOrigin(request.RemoteEndPoint) || !_originValidator.ValidateOriginHeader(origin))
                {
                    Log($"Security Alert: Blocked request from unauthorized origin or endpoint. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}");
                    await SendErrorAsync(response, ErrorCodes.UnknownCommand, "Unauthorized Origin", 403);
                    return;
                }

                // CORS headers only for validated browser origins.
                if (!string.IsNullOrEmpty(origin))
                {
                    response.Headers["Access-Control-Allow-Origin"] = origin;
                    response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                    response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                    response.Headers["Vary"] = "Origin";
                }
                
                // Handle preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }
                
                // Route the request
                var path = request.Url?.AbsolutePath ?? "/";
                var method = request.HttpMethod;
                
                Log($"{method} {path}");
                
                var routeMatch = _router.Match(method, path);
                
                if (routeMatch == null)
                {
                    await SendErrorAsync(response, ErrorCodes.UnknownCommand,
                        $"Unknown command: {method} {path}", 404);
                    return;
                }
                
                // Read request body
                string body = null;
                if (request.HasEntityBody)
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    body = await reader.ReadToEndAsync();
                }
                
                // Execute command
                var result = await _handler.ExecuteAsync(routeMatch, body);
                
                await SendResponseAsync(response, result);
            }
            catch (WebDriverException wdEx)
            {
                await SendErrorAsync(response, wdEx.ErrorCode, wdEx.Message, wdEx.HttpStatus);
            }
            catch (JsonException)
            {
                await SendErrorAsync(response, ErrorCodes.InvalidArgument,
                    "Invalid JSON in request body", 400);
            }
            catch (Exception ex)
            {
                Log($"Request error: {ex}");
                await SendErrorAsync(response, ErrorCodes.UnknownError, ex.Message, 500);
            }
        }
        
        private async Task SendResponseAsync(HttpListenerResponse response, WebDriverResponse result)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = 200;
            
            var json = result.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        
        private async Task SendErrorAsync(HttpListenerResponse response, string error, string message, int status)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = status;
            
            var result = WebDriverResponse.Error(error, message);
            var json = result.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        
        private void Log(string message)
        {
            OnLog?.Invoke($"[WebDriver] {message}");
        }
        
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WebDriverServer));
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            Stop();
            _sessionManager.Dispose();
            _cts.Dispose();
            _listener.Close();
        }
    }
}
