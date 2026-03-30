using System;
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

    public static ScriptParsedEvent Create(
        string scriptId,
        string? url,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        string hash,
        bool hasSourceUrl,
        bool isModule,
        int length,
        int executionContextId = 1,
        bool isLiveEdit = false,
        string? sourceMapUrl = null)
    {
        int normalizedStartLine = Math.Max(0, startLine);
        int normalizedStartColumn = Math.Max(0, startColumn);
        int normalizedEndLine = Math.Max(normalizedStartLine, endLine);

        return new ScriptParsedEvent
        {
            ScriptId = NormalizeText(scriptId),
            Url = NormalizeText(url),
            StartLine = normalizedStartLine,
            StartColumn = normalizedStartColumn,
            EndLine = normalizedEndLine,
            EndColumn = Math.Max(0, endColumn),
            ExecutionContextId = executionContextId > 0 ? executionContextId : 1,
            Hash = NormalizeText(hash),
            IsLiveEdit = isLiveEdit,
            SourceMapUrl = NormalizeTextOrNull(sourceMapUrl),
            HasSourceUrl = hasSourceUrl && !string.IsNullOrWhiteSpace(url),
            IsModule = isModule,
            Length = Math.Max(0, length)
        };
    }

    private static string NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string? NormalizeTextOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// Result for Debugger.getScriptSource.
/// </summary>
public record GetScriptSourceResult
{
    [JsonPropertyName("scriptSource")]
    public required string ScriptSource { get; init; }

    public static GetScriptSourceResult Create(string? scriptSource)
    {
        return new GetScriptSourceResult
        {
            ScriptSource = scriptSource ?? string.Empty
        };
    }
}
