using SkiaSharp;
using System.Text.Json;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;

namespace FenBrowser.DevTools.Panels;

/// <summary>
/// Network panel showing HTTP requests and responses.
/// </summary>
public class NetworkPanel : DevToolsPanelBase
{
    public override string Title => "Network";
    public override string? Shortcut => "Ctrl+Shift+E";
    
    private readonly List<NetworkRequestInfo> _requests = new();
    private NetworkRequestInfo? _selectedRequest;
    private int _hoveredIndex = -1;
    private string? _requestBodyPreview;
    private string? _responseBodyPreview;
    
    // Layout
    private float _listHeight;
    private float _detailsHeight;
    private const float HEADER_HEIGHT = 24f;
    
    // Columns
    private readonly (string Name, float Width)[] _columns = new[]
    {
        ("Name", 200f),
        ("Status", 60f),
        ("Type", 80f),
        ("Size", 70f),
        ("Time", 70f)
    };

    protected override void OnHostChanging(IDevToolsHost? previousHost)
    {
        if (previousHost != null)
        {
            previousHost.ProtocolEventReceived -= OnProtocolEvent;
        }

        _requests.Clear();
        _selectedRequest = null;
        _hoveredIndex = -1;
        _requestBodyPreview = null;
        _responseBodyPreview = null;
        ScrollY = 0;
        MaxScrollY = 0;
    }
    
    protected override void OnHostChanged()
    {
        if (Host != null)
        {
            Host.ProtocolEventReceived += OnProtocolEvent;
            
            // Enable Network domain
            _ = Host.SendProtocolCommandAsync(JsonSerializer.Serialize(new ProtocolRequest<object>
            {
                Method = "Network.enable",
                Params = new { }
            }, ProtocolJson.Options));
            
            // Load existing requests (Legacy fallback)
            _requests.Clear();
            _requests.AddRange(Host.GetNetworkRequests());
        }
    }

    public override void Paint(SKCanvas canvas, SKRect bounds)
    {
        Bounds = bounds;

        canvas.Save();
        canvas.ClipRect(bounds);

        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.Background);
        canvas.DrawRect(bounds, bgPaint);

        OnPaint(canvas, bounds);

        if (MaxScrollY > 0)
        {
            DrawPaneScrollbar(canvas, new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + _listHeight), ScrollY, MaxScrollY);
        }

        canvas.Restore();
    }

    private void OnProtocolEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("method", out var methodProp)) return;
            var method = methodProp.GetString();
            
            if (method == "Network.requestWillBeSent")
            {
                var evt = JsonSerializer.Deserialize<ProtocolEvent<RequestWillBeSentEvent>>(json, ProtocolJson.Options);
                if (evt?.Params != null)
                {
                    var info = new NetworkRequestInfo(
                        evt.Params.RequestId,
                        evt.Params.Dto.Url,
                        evt.Params.Dto.Method,
                        0, "pending", "", 0, 0,
                        DateTime.UnixEpoch.AddSeconds(evt.Params.Timestamp),
                        evt.Params.Dto.Headers,
                        new Dictionary<string, string>(),
                        null,
                        null, false
                    );
                    UpdateRequest(info);
                }
            }
            else if (method == "Network.responseReceived")
            {
                var evt = JsonSerializer.Deserialize<ProtocolEvent<ResponseReceivedEvent>>(json, ProtocolJson.Options);
                if (evt?.Params != null)
                {
                    int index = _requests.FindIndex(r => r.Id == evt.Params.RequestId);
                    if (index >= 0)
                    {
                        var old = _requests[index];
                        var info = old with {
                            StatusCode = evt.Params.Dto.Status,
                            StatusText = evt.Params.Dto.StatusText,
                            ContentType = evt.Params.Dto.MimeType,
                            ResponseHeaders = evt.Params.Dto.Headers
                        };
                        UpdateRequest(info);
                    }
                }
            }
            else if (method == "Network.loadingFinished")
            {
                var evt = JsonSerializer.Deserialize<ProtocolEvent<LoadingFinishedEvent>>(json, ProtocolJson.Options);
                if (evt?.Params != null)
                {
                    int index = _requests.FindIndex(r => r.Id == evt.Params.RequestId);
                    if (index >= 0)
                    {
                        var old = _requests[index];
                        var info = old with {
                            Size = evt.Params.EncodedDataLength,
                            IsComplete = true,
                            DurationMs = (DateTime.UnixEpoch.AddSeconds(evt.Params.Timestamp) - old.StartTime).TotalMilliseconds
                        };
                        UpdateRequest(info);
                    }
                }
            }
        }
        catch { }
    }

    private void UpdateRequest(NetworkRequestInfo info)
    {
        int index = _requests.FindIndex(r => r.Id == info.Id);
        if (index >= 0) _requests[index] = info;
        else _requests.Add(info);
        
        MaxScrollY = Math.Max(0, _requests.Count * DevToolsTheme.ItemHeight - _listHeight + HEADER_HEIGHT);
        Invalidate();
    }
    
    protected override void OnPaint(SKCanvas canvas, SKRect bounds)
    {
        // Split view: list on top, details on bottom
        _listHeight = _selectedRequest == null ? bounds.Height : bounds.Height * 0.5f;
        _detailsHeight = bounds.Height - _listHeight;
        
        var listBounds = new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + _listHeight);
        var detailsBounds = new SKRect(bounds.Left, listBounds.Bottom, bounds.Right, bounds.Bottom);
        
        // Draw list
        DrawRequestList(canvas, listBounds);
        
        // Draw details if selected
        if (_selectedRequest != null)
        {
            DrawRequestDetails(canvas, detailsBounds);
        }
    }
    
    private void DrawRequestList(SKCanvas canvas, SKRect bounds)
    {
        // Draw header
        float x = bounds.Left;
        float y = bounds.Top;
        
        using var headerBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        canvas.DrawRect(new SKRect(bounds.Left, y, bounds.Right, y + HEADER_HEIGHT), headerBgPaint);

        using var headerFont = DevToolsTheme.CreateUIFont(DevToolsTheme.FontSizeSmall);
        using var headerColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextSecondary);

        foreach (var (name, width) in _columns)
        {
            canvas.DrawText(name, x + DevToolsTheme.PaddingSmall, y + HEADER_HEIGHT - 6, headerFont, headerColorPaint);
            x += width;
        }
        
        // Draw border
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(bounds.Left, y + HEADER_HEIGHT, bounds.Right, y + HEADER_HEIGHT, borderPaint);
        
        // Draw requests
        y = bounds.Top + HEADER_HEIGHT - ScrollY;
        
        for (int i = 0; i < _requests.Count; i++)
        {
            float itemY = y + i * DevToolsTheme.ItemHeight;
            
            // Skip if outside visible area
            if (itemY + DevToolsTheme.ItemHeight < bounds.Top + HEADER_HEIGHT) continue;
            if (itemY > bounds.Bottom) break;
            
            var request = _requests[i];
            
            // Selection / hover background
            if (request == _selectedRequest)
            {
                using var selectPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundSelected);
                canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), selectPaint);
            }
            else if (i == _hoveredIndex)
            {
                using var hoverPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundHover);
                canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), hoverPaint);
            }
            
            x = bounds.Left;
            float textY = itemY + DevToolsTheme.ItemHeight / 2 + 4;
            
            // Name (URL)
            string name = System.IO.Path.GetFileName(new Uri(request.Url).AbsolutePath);
            if (string.IsNullOrEmpty(name)) name = "/";
            if (name.Length > 30) name = name.Substring(0, 27) + "...";

            using var nameFont = DevToolsTheme.CreateTextFont();
            using var nameColorPaint = DevToolsTheme.CreateTextColorPaint();
            canvas.DrawText(name, x + DevToolsTheme.PaddingSmall, textY, nameFont, nameColorPaint);
            x += _columns[0].Width;

            // Status
            SKColor statusColor = request.StatusCode switch
            {
                >= 200 and < 300 => DevToolsTheme.NetworkSuccess,
                >= 300 and < 400 => DevToolsTheme.NetworkRedirect,
                >= 400 => DevToolsTheme.NetworkError,
                _ => DevToolsTheme.NetworkPending
            };

            using var statusFont = DevToolsTheme.CreateTextFont();
            using var statusColorPaint = DevToolsTheme.CreateTextColorPaint(statusColor);
            string status = request.IsComplete ? request.StatusCode.ToString() : "pending";
            canvas.DrawText(status, x + DevToolsTheme.PaddingSmall, textY, statusFont, statusColorPaint);
            x += _columns[1].Width;

            // Type
            using var typeFont = DevToolsTheme.CreateTextFont();
            using var typeColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextSecondary);
            string type = request.ContentType?.Split('/').LastOrDefault()?.Split(';').FirstOrDefault() ?? "-";
            if (type.Length > 10) type = type.Substring(0, 7) + "...";
            canvas.DrawText(type, x + DevToolsTheme.PaddingSmall, textY, typeFont, typeColorPaint);
            x += _columns[2].Width;

            // Size
            string size = request.Size > 0 ? FormatSize(request.Size) : "-";
            canvas.DrawText(size, x + DevToolsTheme.PaddingSmall, textY, typeFont, typeColorPaint);
            x += _columns[3].Width;

            // Time
            string time = request.IsComplete ? $"{request.DurationMs:F0}ms" : "-";
            canvas.DrawText(time, x + DevToolsTheme.PaddingSmall, textY, typeFont, typeColorPaint);
        }

        // Empty state
        if (_requests.Count == 0)
        {
            using var hintFont = DevToolsTheme.CreateTextFont();
            using var hintColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("No network requests", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + HEADER_HEIGHT + 30, hintFont, hintColorPaint);
        }
    }
    
    private void DrawRequestDetails(SKCanvas canvas, SKRect bounds)
    {
        if (_selectedRequest == null) return;
        
        // Top border
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(bounds.Left, bounds.Top, bounds.Right, bounds.Top, borderPaint);
        
        float y = bounds.Top + DevToolsTheme.PaddingNormal;
        float x = bounds.Left + DevToolsTheme.PaddingNormal;
        
        // URL
        using var labelFont = DevToolsTheme.CreateTextFont();
        using var labelColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextSecondary);
        using var valueFont = DevToolsTheme.CreateTextFont();
        using var valueColorPaint = DevToolsTheme.CreateTextColorPaint();

        canvas.DrawText("URL: ", x, y + 12, labelFont, labelColorPaint);

        string url = _selectedRequest.Url;
        if (url.Length > 80) url = url.Substring(0, 77) + "...";
        canvas.DrawText(url, x + 35, y + 12, valueFont, valueColorPaint);
        y += DevToolsTheme.ItemHeight;

        // Method + Status
        canvas.DrawText($"Method: {_selectedRequest.Method}", x, y + 12, valueFont, valueColorPaint);
        canvas.DrawText($"Status: {_selectedRequest.StatusCode} {_selectedRequest.StatusText}", x + 120, y + 12, valueFont, valueColorPaint);
        y += DevToolsTheme.ItemHeight;

        // Headers section
        canvas.DrawLine(bounds.Left, y, bounds.Right, y, borderPaint);
        y += DevToolsTheme.PaddingNormal;

        using var sectionFont = DevToolsTheme.CreateUIFont(DevToolsTheme.FontSizeMedium);
        using var sectionColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextPrimary);
        canvas.DrawText("Response Headers", x, y + 12, sectionFont, sectionColorPaint);
        y += DevToolsTheme.ItemHeight;

        using var headerKeyFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
        using var headerKeyColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.SyntaxProperty);
        using var headerValueFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
        using var headerValueColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextSecondary);

        foreach (var header in _selectedRequest.ResponseHeaders.Take(8))
        {
            if (y + 16 > bounds.Bottom) break;

            canvas.DrawText(header.Key + ": ", x, y + 10, headerKeyFont, headerKeyColorPaint);

            string val = header.Value;
            if (val.Length > 50) val = val.Substring(0, 47) + "...";
            canvas.DrawText(val, x + 150, y + 10, headerValueFont, headerValueColorPaint);
            y += 16;
        }

        y += DevToolsTheme.PaddingNormal;
        canvas.DrawLine(bounds.Left, y, bounds.Right, y, borderPaint);
        y += DevToolsTheme.PaddingNormal;

        if (!string.IsNullOrWhiteSpace(_requestBodyPreview))
        {
            canvas.DrawText("Request Body", x, y + 12, sectionFont, sectionColorPaint);
            y += DevToolsTheme.ItemHeight;

            foreach (var line in TruncatePreview(_requestBodyPreview!, 3).Split('\n'))
            {
                if (y + 16 > bounds.Bottom) break;
                canvas.DrawText(line, x, y + 10, headerValueFont, headerValueColorPaint);
                y += 16;
            }
        }

        if (!string.IsNullOrWhiteSpace(_responseBodyPreview) && y + 16 <= bounds.Bottom)
        {
            if (!string.IsNullOrWhiteSpace(_requestBodyPreview))
            {
                y += DevToolsTheme.PaddingSmall;
            }

            canvas.DrawText("Response Preview", x, y + 12, sectionFont, sectionColorPaint);
            y += DevToolsTheme.ItemHeight;

            foreach (var line in TruncatePreview(_responseBodyPreview!, 5).Split('\n'))
            {
                if (y + 16 > bounds.Bottom) break;
                canvas.DrawText(line, x, y + 10, headerValueFont, headerValueColorPaint);
                y += 16;
            }
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
        Host?.RequestCursorChange(CursorType.Default);
        
        if (y < Bounds.Top + _listHeight)
        {
            int index = GetRequestIndexAt(y);
            if (index != _hoveredIndex)
            {
                _hoveredIndex = index;
                Invalidate();
            }
        }
        else
        {
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                Invalidate();
            }
        }
    }
    
    public override bool OnMouseDown(float x, float y, bool isRightButton)
    {
        if (y < Bounds.Top + _listHeight)
        {
            int index = GetRequestIndexAt(y);
            if (index >= 0 && index < _requests.Count)
            {
                _selectedRequest = _requests[index];
                _requestBodyPreview = _selectedRequest.RequestBody;
                _responseBodyPreview = _selectedRequest.ResponseBody;
                _ = LoadSelectedRequestBodiesAsync(_selectedRequest.Id);
                Invalidate();
                return true;
            }
        }
        
        return false;
    }

    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        if (y >= Bounds.Top + _listHeight || MaxScrollY <= 0)
        {
            return;
        }

        ScrollY = Math.Clamp(ScrollY - deltaY * 40, 0, MaxScrollY);
        Invalidate();
    }
    
    private int GetRequestIndexAt(float y)
    {
        float relativeY = y - Bounds.Top - HEADER_HEIGHT + ScrollY;
        int index = (int)(relativeY / DevToolsTheme.ItemHeight);
        return index >= 0 && index < _requests.Count ? index : -1;
    }
    
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / (1024.0 * 1024.0):F1}MB";
    }

    private async Task LoadSelectedRequestBodiesAsync(string requestId)
    {
        if (Host == null) return;

        string? requestBodyPreview = _selectedRequest?.RequestBody;
        string? responseBodyPreview = _selectedRequest?.ResponseBody;

        try
        {
            var bodyRequest = new ProtocolRequest<object>
            {
                Id = 3001,
                Method = "Network.getResponseBody",
                Params = new { requestId }
            };

            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(bodyRequest, ProtocolJson.Options));
            var bodyResponse = ProtocolJson.Deserialize<ProtocolResponse<GetResponseBodyResult>>(responseJson);
            if (bodyResponse?.Result?.Body != null)
            {
                responseBodyPreview = bodyResponse.Result.Body;
            }
        }
        catch
        {
            // Ignore response-body fetch failures for requests without buffered payloads.
        }

        try
        {
            var requestBodyRequest = new ProtocolRequest<object>
            {
                Id = 3002,
                Method = "Network.getRequestPostData",
                Params = new { requestId }
            };

            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(requestBodyRequest, ProtocolJson.Options));
            var bodyResponse = ProtocolJson.Deserialize<ProtocolResponse<GetRequestPostDataResult>>(responseJson);
            if (bodyResponse?.Result?.PostData != null)
            {
                requestBodyPreview = bodyResponse.Result.PostData;
            }
        }
        catch
        {
            // Ignore request-body fetch failures for GET/HEAD and similar requests.
        }

        if (_selectedRequest?.Id != requestId)
        {
            return;
        }

        _requestBodyPreview = requestBodyPreview;
        _responseBodyPreview = responseBodyPreview;

        Invalidate();
    }

    private static string TruncatePreview(string body, int maxLines)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var normalized = body.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var limited = lines.Length > maxLines ? string.Join('\n', lines.Take(maxLines)) + "\n..." : normalized;
        return limited.Length > 320 ? limited.Substring(0, 317) + "..." : limited;
    }

    private static void DrawPaneScrollbar(SKCanvas canvas, SKRect bounds, float scrollY, float maxScrollY)
    {
        float scrollbarWidth = 8f;
        float trackHeight = bounds.Height;
        float thumbHeight = Math.Max(20, trackHeight * (trackHeight / (trackHeight + maxScrollY)));
        float thumbY = bounds.Top + (scrollY / maxScrollY) * (trackHeight - thumbHeight);
        var thumbRect = new SKRect(bounds.Right - scrollbarWidth - 2, thumbY, bounds.Right - 2, thumbY + thumbHeight);

        using var paint = DevToolsTheme.CreateFillPaint(DevToolsTheme.Scrollbar);
        canvas.DrawRoundRect(thumbRect, 4, 4, paint);
    }
}
