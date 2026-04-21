using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// DevTools Protocol Server.
/// Central point for protocol communication.
/// </summary>
public class DevToolsServer
{
    private readonly NodeRegistry _registry;
    private readonly MessageRouter _router;
    private readonly List<Action<string>> _jsonOutputListeners = new();
    private readonly object _jsonOutputLock = new();
    
    // Domain handlers
    private DomDomain? _domDomain;
    private RuntimeDomain? _runtimeDomain;
    private NetworkDomain? _networkDomain;
    private DebuggerDomain? _debuggerDomain;
    private CSSDomain? _cssDomain;
    private LogDomain? _logDomain;
    
    public NodeRegistry Registry => _registry;
    public MessageRouter Router => _router;
    
    public DevToolsServer()
    {
        _registry = new NodeRegistry();
        _router = new MessageRouter();
        
        // Subscribe to events and forward as JSON
        _router.Subscribe(evt =>
        {
            var json = ProtocolJson.Serialize(evt);
            BroadcastJson(json);
        });
    }
    
    /// <summary>
    /// Initialize the DOM domain with a root node provider.
    /// </summary>
    public void InitializeDom(
        Func<Node?> getRootNode,
        Action<int?>? onHighlight = null,
        Func<Func<ProtocolResponse>, Task<ProtocolResponse>>? dispatchAsync = null)
    {
        _domDomain = new DomDomain(_registry, getRootNode, onHighlight, dispatchAsync);
        _router.RegisterHandler(_domDomain);
    }
    
    public void InitializeRuntime(IDevToolsHost host)
    {
        _runtimeDomain = new RuntimeDomain(host);
        _router.RegisterHandler(_runtimeDomain);
    }
    
    public void InitializeNetwork(IDevToolsHost host)
    {
        _networkDomain = new NetworkDomain(host);
        _router.RegisterHandler(_networkDomain);
    }

    public void InitializeDebugger(IDevToolsHost host)
    {
        _debuggerDomain = new DebuggerDomain(host, BroadcastEvent);
        _router.RegisterHandler(_debuggerDomain);
    }

    public void InitializeLog()
    {
        _logDomain = new LogDomain(BroadcastEvent);
        _router.RegisterHandler(_logDomain);
    }
    
    public void InitializeCss(
        Func<Node, CssComputed?> getComputedStyle, 
        Func<Node, List<CssLoader.MatchedRule>>? getMatchedRules = null,
        Action<Node, string, string>? setInlineStyle = null,
        Action? triggerRepaint = null,
        Func<Func<ProtocolResponse>, Task<ProtocolResponse>>? dispatchAsync = null)
    {
        _cssDomain = new CSSDomain(_registry, getComputedStyle, getMatchedRules, setInlineStyle, triggerRepaint, dispatchAsync);
        _router.RegisterHandler(_cssDomain);
    }
    
    /// <summary>
    /// Subscribe to JSON output (for debugging or forwarding to UI).
    /// </summary>
    public void OnJsonOutput(Action<string> listener)
    {
        lock (_jsonOutputLock)
        {
            _jsonOutputListeners.Add(listener);
        }
    }

    /// <summary>
    /// Remove a JSON output subscriber.
    /// </summary>
    public void RemoveJsonOutput(Action<string> listener)
    {
        lock (_jsonOutputLock)
        {
            _jsonOutputListeners.Remove(listener);
        }
    }
    
    /// <summary>
    /// Process a JSON request and return JSON response.
    /// </summary>
    public async Task<string> ProcessRequestAsync(string requestJson)
    {
        var responseJson = await _router.DispatchJsonAsync(requestJson);
        return responseJson;
    }
    
    /// <summary>
    /// Broadcast JSON to all listeners.
    /// </summary>
    private void BroadcastJson(string json)
    {
        Action<string>[] listeners;
        lock (_jsonOutputLock)
        {
            listeners = _jsonOutputListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(json);
            }
            catch
            {
                // Ignore listener errors
            }
        }
    }
    
    /// <summary>
    /// Broadcast a DOM event.
    /// </summary>
    public void BroadcastDomEvent(string method, object eventParams)
    {
        BroadcastEvent("DOM." + method, eventParams);
    }
    
    /// <summary>
    /// Broadcast a general protocol event.
    /// </summary>
    public void BroadcastEvent(string fullMethod, object eventParams)
    {
        var evt = new ProtocolEvent
        {
            Method = fullMethod,
            Params = eventParams
        };
        _router.BroadcastEvent(evt);
    }
    
    /// <summary>
    /// Clear all state (on page navigation).
    /// </summary>
    public void Reset()
    {
        _registry.Clear();
        _router.ClearHandlers();
        _domDomain = null;
        _runtimeDomain = null;
        _networkDomain = null;
        _debuggerDomain = null;
        _cssDomain = null;
        _logDomain = null;
    }
}
