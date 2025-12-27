using System.Text.Json.Serialization;

namespace FenBrowser.DevTools.Domains.DTOs;

/// <summary>
/// Event: Console API called (e.g., console.log).
/// </summary>
public record ConsoleAPICalledEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "log";

    [JsonPropertyName("args")]
    public required RemoteObject[] Args { get; init; }

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; init; }

    [JsonPropertyName("stackTrace")]
    public StackTraceResponse? StackTrace { get; init; }
}

/// <summary>
/// DTO representing a remote object (evaluation result or log argument).
/// </summary>
public record RemoteObject
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "undefined";

    [JsonPropertyName("subtype")]
    public string? Subtype { get; init; }

    [JsonPropertyName("className")]
    public string? ClassName { get; init; }

    [JsonPropertyName("value")]
    public object? Value { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("objectId")]
    public string? ObjectId { get; init; }
}

/// <summary>
/// Stack trace DTO.
/// </summary>
public record StackTraceResponse
{
    [JsonPropertyName("callFrames")]
    public required CallFrame[] CallFrames { get; init; }
}

/// <summary>
/// Single frame in a stack trace.
/// </summary>
public record CallFrame
{
    [JsonPropertyName("functionName")]
    public string FunctionName { get; init; } = "";

    [JsonPropertyName("scriptId")]
    public string ScriptId { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; init; }

    [JsonPropertyName("columnNumber")]
    public int ColumnNumber { get; init; }
}

/// <summary>
/// Result for Runtime.evaluate.
/// </summary>
public record EvaluateResult
{
    [JsonPropertyName("result")]
    public required RemoteObject Result { get; init; }

    [JsonPropertyName("exceptionDetails")]
    public ExceptionDetails? ExceptionDetails { get; init; }
}

/// <summary>
/// Detailed information about an exception.
/// </summary>
public record ExceptionDetails
{
    [JsonPropertyName("exceptionId")]
    public int ExceptionId { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; init; }

    [JsonPropertyName("columnNumber")]
    public int ColumnNumber { get; init; }

    [JsonPropertyName("stackTrace")]
    public StackTraceResponse? StackTrace { get; init; }

    [JsonPropertyName("exception")]
    public RemoteObject? Exception { get; init; }
}
