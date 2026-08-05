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

    public IpcFontService(TargetProcessSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.TargetProcessCrashed += OnTargetProcessCrashed;
    }

    private void OnTargetProcessCrashed()
    {
        // Complete all pending requests with failure
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetException(new InvalidOperationException("Utility process crashed"));
        }
        _pendingRequests.Clear();
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

        var response = SendRequest<FontGetMetricsResponsePayload>(TargetIpcMessageType.FontGetMetrics, payload);
        if (!response.Success)
        {
            FenBrowser.Core.Logging.EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] GetMetrics failed: {response.ErrorMessage}");
            // Fallback to default metrics
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

        return new NormalizedFontMetrics
        {
            Ascent = response.Ascent,
            Descent = response.Descent,
            LineHeight = response.LineHeight,
            XHeight = response.XHeight,
            EmSize = response.CapHeight, // CapHeight maps to EmSize in our response
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

        var response = SendRequest<FontMeasureWidthResponsePayload>(TargetIpcMessageType.FontMeasureWidth, payload);
        if (!response.Success)
        {
            EngineLogCompat.Warn($"[IpcFontService] MeasureTextWidth failed: {response.ErrorMessage}", LogCategory.Rendering);
            return 0;
        }

        return response.Width;
    }

    public GlyphRun ShapeText(string text, string fontFamily, float fontSize, int fontWeight = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new GlyphRun
            {
                Glyphs = Array.Empty<PositionedGlyph>(),
                Width = 0,
                FontSize = fontSize,
                SourceText = text
            };
        }

        var payload = new FontShapeTextPayload
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight
        };

        var response = SendRequest<FontShapeTextResponsePayload>(TargetIpcMessageType.FontShapeText, payload);
        if (!response.Success)
        {
            FenBrowser.Core.Logging.EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] ShapeText failed: {response.ErrorMessage}");
            return new GlyphRun
            {
                Glyphs = Array.Empty<PositionedGlyph>(),
                Width = 0,
                FontSize = fontSize,
                SourceText = text
            };
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
            SourceText = response.SourceText
        };
    }

    public SKTypeface ResolveTypeface(string fontFamily, int fontWeight = 400, SKFontStyleSlant fontStyle = SKFontStyleSlant.Upright)
    {
        // Typeface resolution is not easily IPC-able since SKTypeface is not serializable.
        // For now, we fall back to local resolution. The utility process handles
        // the actual shaping/rendering, so this is only used for font matching.
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
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(requestId, tcs))
        {
            throw new InvalidOperationException("Failed to register request");
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

            // Wait for response with timeout
            var completedTask = Task.WhenAny(tcs.Task, Task.Delay(_requestTimeout)).GetAwaiter().GetResult();
            if (completedTask == tcs.Task)
            {
                return tcs.Task.Result as TResponse;
            }
            else
            {
                _pendingRequests.TryRemove(requestId, out _);
                FenBrowser.Core.Logging.EngineLog.Write(LogSubsystem.Font, LogSeverity.Warn, $"[IpcFontService] Request {requestType} timed out");
                return null;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    // This would be called from the IPC read loop when a response arrives
    internal void HandleResponse(TargetIpcEnvelope envelope)
    {
        if (_pendingRequests.TryRemove(envelope.RequestId, out var tcs))
        {
            if (envelope.Type == TargetIpcMessageType.FontGetMetricsResponse.ToString())
            {
                var response = TargetIpc.DeserializePayload<FontGetMetricsResponsePayload>(envelope);
                tcs.TrySetResult(response);
            }
            else if (envelope.Type == TargetIpcMessageType.FontMeasureWidthResponse.ToString())
            {
                var response = TargetIpc.DeserializePayload<FontMeasureWidthResponsePayload>(envelope);
                tcs.TrySetResult(response);
            }
            else if (envelope.Type == TargetIpcMessageType.FontShapeTextResponse.ToString())
            {
                var response = TargetIpc.DeserializePayload<FontShapeTextResponsePayload>(envelope);
                tcs.TrySetResult(response);
            }
            else if (envelope.Type == TargetIpcMessageType.FontResolveTypefaceResponse.ToString())
            {
                var response = TargetIpc.DeserializePayload<FontResolveTypefaceResponsePayload>(envelope);
                tcs.TrySetResult(response);
            }
            else
            {
                tcs.TrySetException(new InvalidOperationException($"Unexpected response type: {envelope.Type}"));
            }
        }
    }

    public void Dispose()
    {
        _session.TargetProcessCrashed -= OnTargetProcessCrashed;
        foreach (var tcs in _pendingRequests.Values)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(IpcFontService)));
        }
        _pendingRequests.Clear();
    }
}