namespace FenBrowser.DevTools.Core.Protocol;

/// <summary>
/// Interface for domain handlers (DOM, Layout, Network, etc.).
/// Each domain handles a set of related methods.
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// Domain name (e.g., "DOM", "Layout", "Network").
    /// </summary>
    string Domain { get; }
    
    /// <summary>
    /// Handle a request for this domain.
    /// </summary>
    /// <param name="method">Method name without domain prefix (e.g., "getDocument")</param>
    /// <param name="request">The full request</param>
    /// <returns>Response with result or error</returns>
    Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request);
}

/// <summary>
/// Central router that dispatches protocol messages to domain handlers.
/// </summary>
public class MessageRouter
{
    private readonly Dictionary<string, IProtocolHandler> _handlers = new();
    private Action<ProtocolEvent>[] _eventListeners = Array.Empty<Action<ProtocolEvent>>();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    
    /// <summary>
    /// Register a domain handler.
    /// </summary>
    public void RegisterHandler(IProtocolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(handler.Domain))
        {
            throw new ArgumentException("Protocol handlers must declare a domain.", nameof(handler));
        }

        lock (_lock)
        {
            if (_handlers.ContainsKey(handler.Domain))
            {
                throw new InvalidOperationException($"Protocol handler already registered for domain '{handler.Domain}'.");
            }

            _handlers[handler.Domain] = handler;
        }
    }

    /// <summary>
    /// Remove all registered domain handlers while preserving event subscriptions.
    /// </summary>
    public void ClearHandlers()
    {
        lock (_lock)
        {
            _handlers.Clear();
        }
    }
    
    /// <summary>
    /// Subscribe to protocol events.
    /// </summary>
    public void Subscribe(Action<ProtocolEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_lock)
        {
            var listeners = _eventListeners;
            if (!Array.Exists(listeners, candidate => candidate == listener))
            {
                var updated = new Action<ProtocolEvent>[listeners.Length + 1];
                Array.Copy(listeners, updated, listeners.Length);
                updated[^1] = listener;
                Volatile.Write(ref _eventListeners, updated);
            }
        }
    }
    
    /// <summary>
    /// Unsubscribe from protocol events.
    /// </summary>
    public void Unsubscribe(Action<ProtocolEvent> listener)
    {
        lock (_lock)
        {
            var listeners = _eventListeners;
            var index = Array.IndexOf(listeners, listener);
            if (index < 0)
            {
                return;
            }

            var updated = new Action<ProtocolEvent>[listeners.Length - 1];
            Array.Copy(listeners, 0, updated, 0, index);
            Array.Copy(listeners, index + 1, updated, index, listeners.Length - index - 1);
            Volatile.Write(ref _eventListeners, updated);
        }
    }
    
    /// <summary>
    /// Broadcast an event to all listeners.
    /// </summary>
    public void BroadcastEvent(ProtocolEvent evt)
    {
        foreach (var listener in Volatile.Read(ref _eventListeners))
        {
            try
            {
                listener(evt);
            }
            catch (Exception ex)
            {
                // Log but don't crash
                System.Diagnostics.Debug.WriteLine($"[DevTools] Event listener error: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Dispatch a request to the appropriate handler.
    /// </summary>
    public async Task<ProtocolResponse> DispatchAsync(ProtocolRequest request)
    {
        if (request == null)
        {
            return ProtocolResponse.Failure(0, "Request is required", -32600);
        }

        if (string.IsNullOrEmpty(request.Method))
        {
            return ProtocolResponse.Failure(request.Id, "Method is required", -32600);
        }
        
        // Parse domain.method
        var parts = request.Method.Split('.', 2);
        if (parts.Length != 2)
        {
            return ProtocolResponse.Failure(request.Id, $"Invalid method format: {request.Method}", -32601);
        }
        
        var domain = parts[0];
        var method = parts[1];
        
        IProtocolHandler? handler;
        lock (_lock)
        {
            _handlers.TryGetValue(domain, out handler);
        }
        
        if (handler == null)
        {
            return ProtocolResponse.Failure(request.Id, $"Unknown domain: {domain}", -32601);
        }
        
        await _dispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await handler.HandleAsync(method, request);
        }
        catch (Exception ex)
        {
            return ProtocolResponse.Failure(request.Id, $"Handler error: {ex.Message}", -32603);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }
    
    /// <summary>
    /// Dispatch a request from JSON string.
    /// </summary>
    public async Task<string> DispatchJsonAsync(string requestJson)
    {
        var request = ProtocolJson.ParseRequest(requestJson);
        if (request == null)
        {
            var errorResponse = ProtocolResponse.Failure(0, "Failed to parse request JSON", -32700);
            return ProtocolJson.Serialize(errorResponse);
        }
        
        var response = await DispatchAsync(request);
        return ProtocolJson.Serialize(response);
    }
}
