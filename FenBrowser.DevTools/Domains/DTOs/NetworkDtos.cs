using System.Text.Json.Serialization;

namespace FenBrowser.DevTools.Domains.DTOs;

/// <summary>
/// Event: Request will be sent.
/// </summary>
public record RequestWillBeSentEvent
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("loaderId")]
    public string? LoaderId { get; init; }

    [JsonPropertyName("documentURL")]
    public string? DocumentURL { get; init; }

    [JsonPropertyName("request")]
    public required NetworkRequest Dto { get; init; }

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; init; }

    [JsonPropertyName("wallTime")]
    public double WallTime { get; init; }
}

/// <summary>
/// Event: Response received.
/// </summary>
public record ResponseReceivedEvent
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("loaderId")]
    public string? LoaderId { get; init; }

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Other";

    [JsonPropertyName("response")]
    public required NetworkResponse Dto { get; init; }
}

/// <summary>
/// Event: Loading finished.
/// </summary>
public record LoadingFinishedEvent
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; init; }

    [JsonPropertyName("encodedDataLength")]
    public long EncodedDataLength { get; init; }
}

/// <summary>
/// Event: Loading failed.
/// </summary>
public record LoadingFailedEvent
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "Other";

    [JsonPropertyName("errorText")]
    public string ErrorText { get; init; } = "";

    [JsonPropertyName("canceled")]
    public bool Canceled { get; init; }
}

/// <summary>
/// Network request information in protocol.
/// </summary>
public record NetworkRequest
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new();

    [JsonPropertyName("initialPriority")]
    public string InitialPriority { get; init; } = "Medium";
}

/// <summary>
/// Network response information in protocol.
/// </summary>
public record NetworkResponse
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("statusText")]
    public string StatusText { get; init; } = "";

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new();

    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = "";
}
