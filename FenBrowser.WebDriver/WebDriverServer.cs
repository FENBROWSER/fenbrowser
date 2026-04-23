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
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.WebDriver.BiDi;
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
        private readonly IBiDiTransportBootstrap _biDiBootstrap;
        private readonly int _port;
        private static readonly string[] AllowedCorsMethods = { "GET", "POST", "DELETE", "OPTIONS" };
        private static readonly string[] AllowedCorsHeaders = { "content-type" };
        private Task _listenerTask;
        private bool _disposed;
        
        public event Action<string> OnLog;
        
        public WebDriverServer(int port = 4444, IBiDiTransportBootstrap biDiBootstrap = null)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            
            _sessionManager = new SessionManager();
            _router = new CommandRouter();
            _handler = new CommandHandler(_sessionManager);
            _originValidator = new OriginValidator(allowLocalhostOnly: true);
            _cts = new CancellationTokenSource();
            _biDiBootstrap = biDiBootstrap ?? new NoOpBiDiTransportBootstrap();
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
            ValidateCommandCoverage();
            
            _listener.Start();
            Log($"WebDriver server started on port {_port}");
            _biDiBootstrap.Register(new BiDiBootstrapContext(_port));
            
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
            using var logScope = EngineLogCompat.BeginScope(
                component: "WebDriver",
                data: new System.Collections.Generic.Dictionary<string, object>
                {
                    ["remoteEndPoint"] = request.RemoteEndPoint?.ToString() ?? string.Empty,
                    ["method"] = request.HttpMethod ?? string.Empty,
                    ["path"] = request.Url?.AbsolutePath ?? string.Empty
                });
            
            try
            {
                var origin = request.Headers["Origin"];
                if (!_originValidator.ValidateOrigin(request.RemoteEndPoint) || !_originValidator.ValidateOriginHeader(origin))
                {
                    const string reasonCode = SecurityBlockReasons.OriginNotAllowed;
                    const string detail = "Request endpoint or Origin header is not authorized";
                    SecurityAudit.LogBlocked(reasonCode, detail);
                    EngineLogCompat.Warn(
                        $"[WebDriver] Unauthorized request blocked. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}",
                        LogCategory.Security);
                    Log($"Security Alert: Blocked request from unauthorized origin or endpoint. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}");
                    await SendErrorAsync(
                        response,
                        ErrorCodes.UnknownCommand,
                        SecurityAudit.BuildBlockedMessage(reasonCode),
                        403,
                        SecurityAudit.CreateFailureData(reasonCode, detail));
                    return;
                }

                // CORS headers only for validated browser origins.
                if (!string.IsNullOrEmpty(origin))
                {
                    response.Headers["Access-Control-Allow-Origin"] = origin;
                    response.Headers["Access-Control-Allow-Methods"] = string.Join(", ", AllowedCorsMethods);
                    response.Headers["Vary"] = "Origin";
                }
                
                // Handle preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    if (!ValidatePreflightRequest(request, response))
                    {
                        const string reasonCode = SecurityBlockReasons.PreflightRejected;
                        const string detail = "Preflight method or headers are not allowed";
                        SecurityAudit.LogBlocked(reasonCode, detail);
                        EngineLogCompat.Warn(
                            $"[WebDriver] Invalid preflight blocked. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}",
                            LogCategory.Security);
                        Log($"Security Alert: Blocked invalid WebDriver preflight. Origin={origin ?? "(none)"} Remote={request.RemoteEndPoint}");
                        await SendErrorAsync(
                            response,
                            ErrorCodes.UnknownCommand,
                            SecurityAudit.BuildBlockedMessage(reasonCode),
                            403,
                            SecurityAudit.CreateFailureData(reasonCode, detail));
                        return;
                    }

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
                await SendErrorAsync(response, wdEx.ErrorCode, wdEx.Message, wdEx.HttpStatus, wdEx.ErrorData);
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
            response.Headers["Cache-Control"] = "no-cache";
            
            var json = result.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        
        private async Task SendErrorAsync(HttpListenerResponse response, string error, string message, int status, object data = null)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = status;
            response.Headers["Cache-Control"] = "no-cache";
            
            var result = WebDriverResponse.Error(error, message, data: data);
            var json = result.ToJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        
        private void Log(string message)
        {
            EngineLogCompat.Info(message, LogCategory.WebDriver);
            OnLog?.Invoke($"[WebDriver] {message}");
        }

        private static bool ValidatePreflightRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            var requestedMethod = request.Headers["Access-Control-Request-Method"];
            if (string.IsNullOrWhiteSpace(requestedMethod) ||
                !AllowedCorsMethods.Contains(requestedMethod, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var requestedHeaderValue = request.Headers["Access-Control-Request-Headers"];
            if (string.IsNullOrWhiteSpace(requestedHeaderValue))
            {
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                return true;
            }

            var requestedHeaders = requestedHeaderValue
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(header => header.Trim())
                .Where(header => !string.IsNullOrEmpty(header))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (requestedHeaders.Length == 0)
            {
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                return true;
            }

            if (requestedHeaders.Any(header => !AllowedCorsHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            response.Headers["Access-Control-Allow-Headers"] = string.Join(", ", requestedHeaders);
            return true;
        }

        private void ValidateCommandCoverage()
        {
            var registered = _router.GetRegisteredCommands()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var implemented = CommandHandler.GetImplementedCommands()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var missing = registered.Except(implemented, StringComparer.Ordinal).ToArray();
            var extra = implemented.Except(registered, StringComparer.Ordinal).ToArray();

            Log($"Command coverage: routes={_router.GetRegisteredRouteCount()}, uniqueCommands={registered.Length}, implemented={implemented.Length}, missing={missing.Length}, extra={extra.Length}");
            if (missing.Length > 0)
            {
                Log($"Missing command handlers: {string.Join(", ", missing)}");
            }
            if (extra.Length > 0)
            {
                Log($"Implemented-but-unrouted commands: {string.Join(", ", extra)}");
            }

            var strictRaw = Environment.GetEnvironmentVariable("FEN_WEBDRIVER_STRICT_COMMAND_COVERAGE");
            var strict = string.Equals(strictRaw, "1", StringComparison.Ordinal) ||
                         string.Equals(strictRaw, "true", StringComparison.OrdinalIgnoreCase);
            if (strict && (missing.Length > 0 || extra.Length > 0))
            {
                throw new InvalidOperationException("WebDriver command coverage strict mode failed. Set FEN_WEBDRIVER_STRICT_COMMAND_COVERAGE=0 to allow partial coverage.");
            }
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
