using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;

namespace FenBrowser.Host;

/// <summary>
/// DevTools host adapter for FenBrowser.Host.
/// Bridges the browser engine with DevTools.
/// </summary>
public class DevToolsHostAdapter : IDevToolsHost
{
    private readonly BrowserIntegration _browser;
    private readonly DevToolsServer _server;
    private readonly List<ConsoleMessageInfo> _consoleMessages = new();
    private readonly List<NetworkRequestInfo> _networkRequests = new();
    
    public event Action DomChanged;
    public event Action<ConsoleMessageInfo> ConsoleMessageAdded;
    public event Action<NetworkRequestInfo> NetworkRequestUpdated;
    public event Action<string>? ProtocolEventReceived;
    public event Action<CursorType>? CursorChanged;
    
    public string CurrentUrl => _browser.CurrentUrl;
    
    public DevToolsHostAdapter(BrowserIntegration browser, DevToolsServer server)
    {
        _browser = browser;
        _server = server;
        
        // Wire up events
        _browser.NeedsRepaint += () => Program.RunOnMainThread(() => DomChanged?.Invoke());
        
        // Wire up protocol events - Assuming OnJsonOutput can be handled on any thread or is already safe?
        // RemoteDebugServer handles it. Internal DevTools might need safe dispatch. 
        // Let's safe dispatch it.
        _server.OnJsonOutput(json => Program.RunOnMainThread(() => ProtocolEventReceived?.Invoke(json)));
        
        // Initialize domains
        _server.InitializeRuntime(this);
        _server.InitializeNetwork(this);
        
        // Wire up network events from legacy DevToolsCore (correlate with protocol)
        FenBrowser.FenEngine.DevTools.DevToolsCore.Instance.OnNetworkRequest += (req) =>
        {
            Program.RunOnMainThread(() => BroadcastNetworkEvent(req));
        };
        
        // Wire up console messages from engine
        _browser.ConsoleMessage += (msg) =>
        {
            // Do parsing on BG thread to save UI time? Or just marshal all?
            // Parsing is cheap. Marshal safely.
            Program.RunOnMainThread(() => {
                var level = ConsoleLevel.Log;
                var cleanMsg = msg;
                
                if (msg.StartsWith("[Error] ")) { level = ConsoleLevel.Error; cleanMsg = msg.Substring(8); }
                else if (msg.StartsWith("[Warn] ")) { level = ConsoleLevel.Warn; cleanMsg = msg.Substring(7); }
                else if (msg.StartsWith("[Info] ")) { level = ConsoleLevel.Info; cleanMsg = msg.Substring(7); }
                else if (msg.StartsWith("[Debug] ")) { level = ConsoleLevel.Debug; cleanMsg = msg.Substring(8); }
                else if (msg.StartsWith("[Alert] ")) { level = ConsoleLevel.Info; cleanMsg = "alert: " + msg.Substring(8); }
                
                AddConsoleMessage(cleanMsg, level);
            });
        };
    }
    
    public async Task<string> SendProtocolCommandAsync(string json)
    {
        return await _server.ProcessRequestAsync(json);
    }
    
    public object EvaluateScript(string script)
    {
        return _browser.EvaluateScript(script);
    }
    
    public void HighlightElement(Element? element)
    {
        Program.RunOnMainThread(() => _browser.HighlightElement(element));
    }

    public IEnumerable<NetworkRequestInfo> GetNetworkRequests() => _networkRequests;
    public IEnumerable<ConsoleMessageInfo> GetConsoleMessages() => _consoleMessages;
    
    public void ScrollToElement(Element element)
    {
        // TODO
    }

    public void RequestCursorChange(CursorType cursor)
    {
        Program.RunOnMainThread(() => CursorChanged?.Invoke(cursor));
    }
    
    public IEnumerable<ScriptSourceInfo> GetScriptSources()
    {
        return Enumerable.Empty<ScriptSourceInfo>();
    }
    
    /// <summary>
    /// Add a console message.
    /// </summary>
    public void AddConsoleMessage(string message, ConsoleLevel level = ConsoleLevel.Log)
    {
        // Assumes called on MainThread now (wrapper above)
        var msg = new ConsoleMessageInfo(
            message,
            level,
            DateTime.Now,
            null,
            null,
            null
        );
        _consoleMessages.Add(msg);
        ConsoleMessageAdded?.Invoke(msg);
        
        // Broadcast protocol event
        var evt = new ConsoleAPICalledEvent
        {
            Type = level.ToString().ToLower(),
            Timestamp = (DateTime.Now - DateTime.UnixEpoch).TotalSeconds,
            Args = new[] { 
                new RemoteObject { 
                    Type = "string", 
                    Value = message, 
                    Description = message 
                } 
            }
        };
        _server.BroadcastEvent("Runtime.consoleAPICalled", evt);
    }
    
    private void BroadcastNetworkEvent(FenBrowser.FenEngine.DevTools.NetworkRequest req)
    {
        // Assumes called on MainThread now
        // Translate legacy NetworkRequest to protocol events
        if (req.Status == "pending")
        {
            var evt = new RequestWillBeSentEvent
            {
                RequestId = req.Id,
                Timestamp = (req.StartTime - DateTime.UnixEpoch).TotalSeconds,
                WallTime = (req.StartTime - DateTime.UnixEpoch).TotalSeconds,
                Dto = new NetworkRequest
                {
                    Url = req.Url,
                    Method = req.Method,
                    Headers = req.RequestHeaders
                }
            };
            _server.BroadcastEvent("Network.requestWillBeSent", evt);
        }
        else if (req.Status == "success" || req.Status == "error" || req.Status == "redirect")
        {
            var respEvt = new ResponseReceivedEvent
            {
                RequestId = req.Id,
                Timestamp = (req.EndTime - DateTime.UnixEpoch).TotalSeconds,
                Dto = new NetworkResponse
                {
                    Url = req.Url,
                    Status = req.StatusCode,
                    StatusText = req.Status, // req.Status is "success"/"error", not full status text, but ok for now
                    Headers = req.ResponseHeaders,
                    MimeType = req.MimeType ?? ""
                }
            };
            _server.BroadcastEvent("Network.responseReceived", respEvt);
            
            var finishEvt = new LoadingFinishedEvent
            {
                RequestId = req.Id,
                Timestamp = (req.EndTime - DateTime.UnixEpoch).TotalSeconds,
                EncodedDataLength = req.Size
            };
            _server.BroadcastEvent("Network.loadingFinished", finishEvt);
        }

        // Update local list for initial state
        var existing = _networkRequests.FirstOrDefault(n => n.Id == req.Id);
        var info = new NetworkRequestInfo(
            req.Id,
            req.Url,
            req.Method,
            req.StatusCode,
            req.Status,
            req.MimeType ?? "",
            req.Size,
            (req.EndTime - req.StartTime).TotalMilliseconds,
            req.StartTime,
            req.RequestHeaders ?? new(),
            req.ResponseHeaders ?? new(),
            null,
            req.Status != "pending"
        );

        if (existing != null)
        {
            _networkRequests.Remove(existing);
        }
        _networkRequests.Add(info);
    }
}
