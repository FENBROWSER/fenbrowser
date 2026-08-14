using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Typography;
using FenBrowser.Host.ProcessIsolation.Targets;
using SkiaSharp;

namespace FenBrowser.Host.ProcessIsolation.Utility;

/// <summary>
/// IPC-based font service that communicates with the utility process.
/// Implements IFontService by forwarding requests to the sandboxed utility process.
/// </summary>
public sealed class IpcFontService : IFontService
{
    private readonly TargetProcessSession _session;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _pendingRequests = new();
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);
    private int _disposed;

    public IpcFontService(TargetProcessSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.TargetProcessCrashed += OnTargetProcessCrashed;
    }

    private void OnTargetProcessCrashed()
    {
        var error = new InvalidOperationException("Utility process crashed");
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(error);
            }
        }
    }

    public NormalizedFontMetrics GetMetrics(string fontFamily, float fontSize, int fontWeight = 400, float? cssLineHeight = null)
    {
        var payload = new FontGetMetricsPayload
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            CssLineHeight = cssLineHeight
        };

        FontGetMetricsResponsePayload response = null;
        try
        {
            response = SendRequest<FontGetMetricsResponsePayload>(TargetIpcMessageType.FontGetMetrics, payload);
        }
        catch (Exception ex)
        {
            EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] GetMetrics IPC failed: {ex.Message}");
        }

        if (response == null || !response.Success)
        {
            EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] GetMetrics failed: {response?.ErrorMessage ?? "no response"}");
            return CreateFallbackMetrics(fontSize);
        }

        return new NormalizedFontMetrics
        {
            Ascent = response.Ascent,
            Descent = response.Descent,
            LineHeight = response.LineHeight,
            XHeight = response.XHeight,
            EmSize = response.CapHeight,
            Leading = response.LineGap
        };
    }

    public float MeasureTextWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var payload = new FontMeasureWidthPayload
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight
        };

        FontMeasureWidthResponsePayload response = null;
        try
        {
            response = SendRequest<FontMeasureWidthResponsePayload>(TargetIpcMessageType.FontMeasureWidth, payload);
        }
        catch (Exception ex)
        {
            EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] MeasureTextWidth IPC failed: {ex.Message}");
        }

        if (response == null || !response.Success)
        {
            EngineLogCompat.Warn($"[IpcFontService] MeasureTextWidth failed: {response?.ErrorMessage ?? "no response"}", LogCategory.Rendering);
            return 0;
        }

        return response.Width;
    }

    public GlyphRun ShapeText(string text, string fontFamily, float fontSize, int fontWeight = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return CreateEmptyGlyphRun(text, fontSize);
        }

        var payload = new FontShapeTextPayload
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight
        };

        FontShapeTextResponsePayload response = null;
        try
        {
            response = SendRequest<FontShapeTextResponsePayload>(TargetIpcMessageType.FontShapeText, payload);
        }
        catch (Exception ex)
        {
            EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] ShapeText IPC failed: {ex.Message}");
        }

        if (response == null || !response.Success || response.Glyphs == null || response.Metrics == null)
        {
            EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] ShapeText failed: {response?.ErrorMessage ?? "invalid or missing response"}");
            return CreateEmptyGlyphRun(text, fontSize);
        }

        var glyphs = new PositionedGlyph[response.Glyphs.Length];
        for (int i = 0; i < response.Glyphs.Length; i++)
        {
            glyphs[i] = new PositionedGlyph
            {
                GlyphId = response.Glyphs[i].GlyphId,
                X = response.Glyphs[i].X,
                Y = response.Glyphs[i].Y,
                AdvanceX = response.Glyphs[i].AdvanceX
            };
        }

        var metrics = new NormalizedFontMetrics
        {
            Ascent = response.Metrics.Ascent,
            Descent = response.Metrics.Descent,
            LineHeight = response.Metrics.LineHeight,
            XHeight = response.Metrics.XHeight,
            EmSize = response.Metrics.CapHeight,
            Leading = response.Metrics.LineGap
        };

        return new GlyphRun
        {
            Glyphs = glyphs,
            Width = response.Width,
            FontSize = response.FontSize,
            Metrics = metrics,
            SourceText = response.SourceText ?? text
        };
    }

    public SKTypeface ResolveTypeface(string fontFamily, int fontWeight = 400, SKFontStyleSlant fontStyle = SKFontStyleSlant.Upright)
    {
        try
        {
            var style = new SKFontStyle((SKFontStyleWeight)fontWeight, SKFontStyleWidth.Normal, fontStyle);
            return SKFontManager.Default.MatchFamily(fontFamily, style) ?? SKTypeface.Default;
        }
        catch
        {
            return SKTypeface.Default;
        }
    }

    private TResponse SendRequest<TResponse>(TargetIpcMessageType requestType, object payload) where TResponse : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(requestId, tcs))
        {
            throw new InvalidOperationException("Failed to register request");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_pendingRequests.TryRemove(requestId, out var pending))
            {
                pending.TrySetException(new ObjectDisposedException(nameof(IpcFontService)));
            }
            throw new ObjectDisposedException(nameof(IpcFontService));
        }

        try
        {
            var envelope = new TargetIpcEnvelope
            {
                Type = requestType.ToString(),
                RequestId = requestId,
                Payload = TargetIpc.SerializePayload(payload)
            };

            _session.Send(envelope);

            var completedTask = Task.WhenAny(tcs.Task, Task.Delay(_requestTimeout)).GetAwaiter().GetResult();
            if (completedTask != tcs.Task)
            {
                EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] Request {requestType} timed out");
                return null;
            }

            return tcs.Task.GetAwaiter().GetResult() as TResponse;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    internal void HandleResponse(TargetIpcEnvelope envelope)
    {
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.RequestId))
        {
            return;
        }

        if (_pendingRequests.TryRemove(envelope.RequestId, out var tcs))
        {
            if (envelope.Type == TargetIpcMessageType.FontGetMetricsResponse.ToString())
            {
                tcs.TrySetResult(TargetIpc.DeserializePayload<FontGetMetricsResponsePayload>(envelope));
            }
            else if (envelope.Type == TargetIpcMessageType.FontMeasureWidthResponse.ToString())
            {
                tcs.TrySetResult(TargetIpc.DeserializePayload<FontMeasureWidthResponsePayload>(envelope));
            }
            else if (envelope.Type == TargetIpcMessageType.FontShapeTextResponse.ToString())
            {
                tcs.TrySetResult(TargetIpc.DeserializePayload<FontShapeTextResponsePayload>(envelope));
            }
            else if (envelope.Type == TargetIpcMessageType.FontResolveTypefaceResponse.ToString())
            {
                tcs.TrySetResult(TargetIpc.DeserializePayload<FontResolveTypefaceResponsePayload>(envelope));
            }
            else
            {
                tcs.TrySetException(new InvalidOperationException($"Unexpected response type: {envelope.Type}"));
            }
        }
    }

    private static NormalizedFontMetrics CreateFallbackMetrics(float fontSize)
    {
        return new NormalizedFontMetrics
        {
            Ascent = fontSize * 0.8f,
            Descent = fontSize * 0.2f,
            LineHeight = fontSize * 1.2f,
            XHeight = fontSize * 0.5f,
            EmSize = fontSize,
            Leading = fontSize * 0.2f
        };
    }

    private static GlyphRun CreateEmptyGlyphRun(string text, float fontSize)
    {
        return new GlyphRun
        {
            Glyphs = Array.Empty<PositionedGlyph>(),
            Width = 0,
            FontSize = fontSize,
            SourceText = text ?? string.Empty
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.TargetProcessCrashed -= OnTargetProcessCrashed;
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(IpcFontService)));
            }
        }
    }
}
