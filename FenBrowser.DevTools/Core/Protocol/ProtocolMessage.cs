using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenBrowser.DevTools.Core.Protocol;

/// <summary>
/// Base class for all protocol messages.
/// </summary>
public abstract record ProtocolMessageBase
{
    /// <summary>
    /// Unique message ID for request/response correlation.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

/// <summary>
/// Request message from UI to Protocol Core.
/// </summary>
public record ProtocolRequest : ProtocolMessageBase
{
    /// <summary>
    /// Method to invoke (e.g., "DOM.getDocument", "DOM.highlightNode").
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    
    /// <summary>
    /// Method parameters (JSON object).
    /// </summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>
/// Typed request message from UI to Protocol Core.
/// </summary>
public record ProtocolRequest<T> : ProtocolMessageBase
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    
    [JsonPropertyName("params")]
    public required T Params { get; init; }
}

/// <summary>
/// Response message from Protocol Core to UI.
/// </summary>
public record ProtocolResponse : ProtocolMessageBase
{
    /// <summary>
    /// Result data on success.
    /// </summary>
    [JsonPropertyName("result")]
    public object? Result { get; init; }
    
    /// <summary>
    /// Error information on failure.
    /// </summary>
    [JsonPropertyName("error")]
    public ProtocolError? Error { get; init; }
    
    public bool IsSuccess => Error == null;
    
    public static ProtocolResponse Success(int id, object? result) =>
        new() { Id = id, Result = result };
    
    public static ProtocolResponse Failure(int id, string message, int code = -1) =>
        new() { Id = id, Error = new ProtocolError(code, message) };
}

/// <summary>
/// Typed response message.
/// </summary>
public record ProtocolResponse<T> : ProtocolMessageBase
{
    [JsonPropertyName("result")]
    public T? Result { get; init; }
    
    [JsonPropertyName("error")]
    public ProtocolError? Error { get; init; }
    
    public bool IsSuccess => Error == null;
}

/// <summary>
/// Error information for failed requests.
/// </summary>
public record ProtocolError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message
);

/// <summary>
/// Event message broadcast from Protocol Core to UI.
/// Events are not in response to a specific request.
/// </summary>
public record ProtocolEvent
{
    /// <summary>
    /// Event method name (e.g., "DOM.childNodeInserted").
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    
    /// <summary>
    /// Event parameters.
    /// </summary>
    [JsonPropertyName("params")]
    public required object Params { get; init; }
}

/// <summary>
/// Typed event message.
/// </summary>
public record ProtocolEvent<T>
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    
    [JsonPropertyName("params")]
    public T? Params { get; init; }
}

/// <summary>
/// JSON serialization options for protocol messages.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
    
    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
    
    public static ProtocolRequest? ParseRequest(string json) =>
        Deserialize<ProtocolRequest>(json);
}
