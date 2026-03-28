using System.Text.Json.Serialization;

namespace FenBrowser.DevTools.Domains.DTOs;

/// <summary>
/// Event payload for Debugger.scriptParsed.
/// </summary>
public record ScriptParsedEvent
{
    [JsonPropertyName("scriptId")]
    public required string ScriptId { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("startLine")]
    public int StartLine { get; init; }

    [JsonPropertyName("startColumn")]
    public int StartColumn { get; init; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; init; }

    [JsonPropertyName("endColumn")]
    public int EndColumn { get; init; }

    [JsonPropertyName("executionContextId")]
    public int ExecutionContextId { get; init; } = 1;

    [JsonPropertyName("hash")]
    public required string Hash { get; init; }

    [JsonPropertyName("isLiveEdit")]
    public bool IsLiveEdit { get; init; }

    [JsonPropertyName("sourceMapURL")]
    public string? SourceMapUrl { get; init; }

    [JsonPropertyName("hasSourceURL")]
    public bool HasSourceUrl { get; init; }

    [JsonPropertyName("isModule")]
    public bool IsModule { get; init; }

    [JsonPropertyName("length")]
    public int Length { get; init; }
}

/// <summary>
/// Result for Debugger.getScriptSource.
/// </summary>
public record GetScriptSourceResult
{
    [JsonPropertyName("scriptSource")]
    public required string ScriptSource { get; init; }
}
