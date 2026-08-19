using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using System.Threading.Channels;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// DevTools Protocol Server.
/// Central point for protocol communication.
/// </summary>
public class DevToolsServer : IDisposable
{
    private const int JsonOutputQueueCapacity = 256;
    private readonly NodeRegistry _registry;
    private readonly MessageRouter _router;
    private JsonOutputSubscription[] _jsonOutputListeners = Array.Empty<JsonOutputSubscription>();
    private readonly object _jsonOutputLock = new();
    private int _disposed;
    
    // Domain handlers
    private DomDomain? _domDomain;
    private RuntimeDomain? _runtimeDomain;
    private NetworkDomain? _networkDomain;
    private DebuggerDomain? _debuggerDomain;
    private CSSDomain? _cssDomain;
    private LogDomain? _logDomain;
    private PageDomain? _pageDomain;
    private OverlayDomain? _overlayDomain;
    private FenBrowserDomain? _fenBrowserDomain;
    
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

    public void InitializeBrowserFrontendCompatibility(IDevToolsHost host)
    {
        _pageDomain = new PageDomain(host);
        _overlayDomain = new OverlayDomain();
        _router.RegisterHandler(_pageDomain);
        _router.RegisterHandler(_overlayDomain);
    }

    public void InitializeFenBrowser(IDevToolsHost host)
    {
        _fenBrowserDomain = new FenBrowserDomain(host);
        _router.RegisterHandler(_fenBrowserDomain);
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
        ArgumentNullException.ThrowIfNull(listener);
        lock (_jsonOutputLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var listeners = _jsonOutputListeners;
            if (Array.Exists(listeners, item => item.Listener == listener))
            {
                return;
            }

            var updated = new JsonOutputSubscription[listeners.Length + 1];
            Array.Copy(listeners, updated, listeners.Length);
            updated[^1] = new JsonOutputSubscription(listener, JsonOutputQueueCapacity);
            Volatile.Write(ref _jsonOutputListeners, updated);
        }
    }

    /// <summary>
    /// Remove a JSON output subscriber.
    /// </summary>
    public void RemoveJsonOutput(Action<string> listener)
    {
        JsonOutputSubscription? removed = null;
        lock (_jsonOutputLock)
        {
            var listeners = _jsonOutputListeners;
            var index = Array.FindIndex(listeners, item => item.Listener == listener);
            if (index < 0)
            {
                return;
            }

            removed = listeners[index];
            var updated = new JsonOutputSubscription[listeners.Length - 1];
            Array.Copy(listeners, 0, updated, 0, index);
            Array.Copy(listeners, index + 1, updated, index, listeners.Length - index - 1);
            Volatile.Write(ref _jsonOutputListeners, updated);
        }

        removed.Dispose();
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
        foreach (var listener in Volatile.Read(ref _jsonOutputListeners))
        {
            listener.TryPublish(json);
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
        _pageDomain = null;
        _overlayDomain = null;
        _fenBrowserDomain = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        JsonOutputSubscription[] listeners;
        lock (_jsonOutputLock)
        {
            listeners = _jsonOutputListeners;
            Volatile.Write(ref _jsonOutputListeners, Array.Empty<JsonOutputSubscription>());
        }

        foreach (var listener in listeners)
        {
            listener.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class JsonOutputSubscription : IDisposable
    {
        private readonly Channel<string> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private int _disposed;

        public JsonOutputSubscription(Action<string> listener, int capacity)
        {
            Listener = listener;
            _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _pump = Task.Run(PumpAsync);
        }

        public Action<string> Listener { get; }

        public void TryPublish(string json)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _queue.Writer.TryWrite(json);
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var json in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        Listener(json);
                    }
                    catch
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.Writer.TryComplete();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
