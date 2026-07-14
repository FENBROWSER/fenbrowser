using System;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Logging;

/// <summary>
/// Builds compatibility log messages only when the requested category and level are enabled.
/// The handler is stack-only and owns no pooled or retained storage.
/// </summary>
[InterpolatedStringHandler]
public ref struct EngineLogInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    public EngineLogInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        LogCategory category,
        LogLevel level,
        out bool shouldAppend)
    {
        IsEnabled = shouldAppend = LogManager.IsEnabled(category, level);
        _builder = shouldAppend
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    internal bool IsEnabled { get; }

    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string format) => _builder.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string format)
        => _builder.AppendFormatted(value, alignment, format);

    public void AppendFormatted(string value) => _builder.AppendFormatted(value);

    public void AppendFormatted(string value, int alignment = 0, string format = null)
        => _builder.AppendFormatted(value, alignment, format);

    public void AppendFormatted(ReadOnlySpan<char> value) => _builder.AppendFormatted(value);

    internal string GetFormattedText() => _builder.ToStringAndClear();
}
