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
        
        using var headerPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeSmall);
        
        foreach (var (name, width) in _columns)
        {
            canvas.DrawText(name, x + DevToolsTheme.PaddingSmall, y + HEADER_HEIGHT - 6, headerPaint);
            x += width;
        }
        
        // Draw border
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(bounds.Left, y + HEADER_HEIGHT, bounds.Right, y + HEADER_HEIGHT, borderPaint);
        
        // Draw requests
        y = bounds.Top + HEADER_HEIGHT + ScrollY;
        
        for (int i = 0; i < _requests.Count; i++)
        {
            float itemY = y + i * DevToolsTheme.ItemHeight;
            
            // Skip if outside visible area
            if (itemY + DevToolsTheme.ItemHeight < bounds.Top + HEADER_HEIGHT - ScrollY) continue;
            if (itemY > bounds.Bottom - ScrollY) break;
            
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
            
            using var namePaint = DevToolsTheme.CreateTextPaint();
            canvas.DrawText(name, x + DevToolsTheme.PaddingSmall, textY, namePaint);
            x += _columns[0].Width;
            
            // Status
            SKColor statusColor = request.StatusCode switch
            {
                >= 200 and < 300 => DevToolsTheme.NetworkSuccess,
                >= 300 and < 400 => DevToolsTheme.NetworkRedirect,
                >= 400 => DevToolsTheme.NetworkError,
                _ => DevToolsTheme.NetworkPending
            };
            
            using var statusPaint = DevToolsTheme.CreateTextPaint(statusColor);
            string status = request.IsComplete ? request.StatusCode.ToString() : "pending";
            canvas.DrawText(status, x + DevToolsTheme.PaddingSmall, textY, statusPaint);
            x += _columns[1].Width;
            
            // Type
            using var typePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);
            string type = request.ContentType?.Split('/').LastOrDefault()?.Split(';').FirstOrDefault() ?? "-";
            if (type.Length > 10) type = type.Substring(0, 7) + "...";
            canvas.DrawText(type, x + DevToolsTheme.PaddingSmall, textY, typePaint);
            x += _columns[2].Width;
            
            // Size
            string size = request.Size > 0 ? FormatSize(request.Size) : "-";
            canvas.DrawText(size, x + DevToolsTheme.PaddingSmall, textY, typePaint);
            x += _columns[3].Width;
            
            // Time
            string time = request.IsComplete ? $"{request.DurationMs:F0}ms" : "-";
            canvas.DrawText(time, x + DevToolsTheme.PaddingSmall, textY, typePaint);
        }
        
        // Empty state
        if (_requests.Count == 0)
        {
            using var hintPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("No network requests", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + HEADER_HEIGHT + 30, hintPaint);
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
        using var labelPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary);
        using var valuePaint = DevToolsTheme.CreateTextPaint();
        
        canvas.DrawText("URL: ", x, y + 12, labelPaint);
        
        string url = _selectedRequest.Url;
        if (url.Length > 80) url = url.Substring(0, 77) + "...";
        canvas.DrawText(url, x + 35, y + 12, valuePaint);
        y += DevToolsTheme.ItemHeight;
        
        // Method + Status
        canvas.DrawText($"Method: {_selectedRequest.Method}", x, y + 12, valuePaint);
        canvas.DrawText($"Status: {_selectedRequest.StatusCode} {_selectedRequest.StatusText}", x + 120, y + 12, valuePaint);
        y += DevToolsTheme.ItemHeight;
        
        // Headers section
        canvas.DrawLine(bounds.Left, y, bounds.Right, y, borderPaint);
        y += DevToolsTheme.PaddingNormal;
        
        using var sectionPaint = DevToolsTheme.CreateUITextPaint(DevToolsTheme.TextPrimary, DevToolsTheme.FontSizeMedium);
        canvas.DrawText("Response Headers", x, y + 12, sectionPaint);
        y += DevToolsTheme.ItemHeight;
        
        using var headerKeyPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.SyntaxProperty, DevToolsTheme.FontSizeSmall);
        using var headerValuePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextSecondary, DevToolsTheme.FontSizeSmall);
        
        foreach (var header in _selectedRequest.ResponseHeaders.Take(8))
        {
            if (y + 16 > bounds.Bottom) break;
            
            canvas.DrawText(header.Key + ": ", x, y + 10, headerKeyPaint);
            
            string val = header.Value;
            if (val.Length > 50) val = val.Substring(0, 47) + "...";
            canvas.DrawText(val, x + 150, y + 10, headerValuePaint);
            y += 16;
        }
    }
    
    public override void OnMouseMove(float x, float y)
    {
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
                Invalidate();
                return true;
            }
        }
        
        return false;
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
}
