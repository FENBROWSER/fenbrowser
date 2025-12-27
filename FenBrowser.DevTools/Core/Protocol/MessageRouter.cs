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
    private readonly List<Action<ProtocolEvent>> _eventListeners = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Register a domain handler.
    /// </summary>
    public void RegisterHandler(IProtocolHandler handler)
    {
        lock (_lock)
        {
            _handlers[handler.Domain] = handler;
        }
    }
    
    /// <summary>
    /// Subscribe to protocol events.
    /// </summary>
    public void Subscribe(Action<ProtocolEvent> listener)
    {
        lock (_lock)
        {
            _eventListeners.Add(listener);
        }
    }
    
    /// <summary>
    /// Unsubscribe from protocol events.
    /// </summary>
    public void Unsubscribe(Action<ProtocolEvent> listener)
    {
        lock (_lock)
        {
            _eventListeners.Remove(listener);
        }
    }
    
    /// <summary>
    /// Broadcast an event to all listeners.
    /// </summary>
    public void BroadcastEvent(ProtocolEvent evt)
    {
        List<Action<ProtocolEvent>> listeners;
        lock (_lock)
        {
            listeners = new List<Action<ProtocolEvent>>(_eventListeners);
        }
        
        foreach (var listener in listeners)
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
        if (string.IsNullOrEmpty(request.Method))
        {
            return ProtocolResponse.Failure(request.Id, "Method is required");
        }
        
        // Parse domain.method
        var parts = request.Method.Split('.', 2);
        if (parts.Length != 2)
        {
            return ProtocolResponse.Failure(request.Id, $"Invalid method format: {request.Method}");
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
            return ProtocolResponse.Failure(request.Id, $"Unknown domain: {domain}");
        }
        
        try
        {
            return await handler.HandleAsync(method, request);
        }
        catch (Exception ex)
        {
            return ProtocolResponse.Failure(request.Id, $"Handler error: {ex.Message}");
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
            var errorResponse = ProtocolResponse.Failure(0, "Failed to parse request JSON");
            return ProtocolJson.Serialize(errorResponse);
        }
        
        var response = await DispatchAsync(request);
        return ProtocolJson.Serialize(response);
    }
}
