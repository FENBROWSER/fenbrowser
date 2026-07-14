using System.Text.Json;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;
using SkiaSharp;

namespace FenBrowser.DevTools.Panels;

/// <summary>
/// Sources panel showing loaded script sources and source text.
/// </summary>
public class SourcesPanel : DevToolsPanelBase
{
    public override string Title => "Sources";
    public override string? Shortcut => "Ctrl+3";

    private readonly List<ScriptListItem> _scripts = new();
    private ScriptListItem? _selectedScript;
    private int _hoveredIndex = -1;
    private string _sourceText = string.Empty;
    private float _sourceScrollY;
    private float _sourceMaxScrollY;
    private const float HEADER_HEIGHT = 24f;
    private const float LIST_WIDTH = 260f;
    private const float SOURCE_LINE_HEIGHT = 16f;

    protected override void OnHostChanging(IDevToolsHost? previousHost)
    {
        if (previousHost != null)
        {
            previousHost.ProtocolEventReceived -= OnProtocolEvent;
        }

        _scripts.Clear();
        _selectedScript = null;
        _hoveredIndex = -1;
        _sourceText = string.Empty;
        _sourceScrollY = 0;
        _sourceMaxScrollY = 0;
        ScrollY = 0;
        MaxScrollY = 0;
    }

    protected override void OnHostChanged()
    {
        if (Host == null)
        {
            return;
        }

        _scripts.Clear();
        _selectedScript = null;
        _hoveredIndex = -1;
        _sourceText = string.Empty;
        _sourceScrollY = 0;
        ScrollY = 0;
        MaxScrollY = 0;

        Host.ProtocolEventReceived += OnProtocolEvent;

        _ = Host.SendProtocolCommandAsync(JsonSerializer.Serialize(new ProtocolRequest<object>
        {
            Method = "Debugger.enable",
            Params = new { }
        }, ProtocolJson.Options));

        _scripts.Clear();
        foreach (var script in Host.GetScriptSources())
        {
            UpsertScript(new ScriptListItem(script.ScriptId, script.Url, script.IsInline, script.Content.Length));
        }

        if (_selectedScript == null && _scripts.Count > 0)
        {
            SelectScript(_scripts[0]);
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
            DrawPaneScrollbar(canvas, new SKRect(bounds.Left, bounds.Top, Math.Min(bounds.Right, bounds.Left + LIST_WIDTH), bounds.Bottom), ScrollY, MaxScrollY);
        }

        if (_sourceMaxScrollY > 0)
        {
            DrawPaneScrollbar(canvas, new SKRect(Math.Min(bounds.Right, bounds.Left + LIST_WIDTH) + 1, bounds.Top, bounds.Right, bounds.Bottom), _sourceScrollY, _sourceMaxScrollY);
        }

        canvas.Restore();
    }

    private void OnProtocolEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("method", out var methodProp))
            {
                return;
            }

            if (methodProp.GetString() != "Debugger.scriptParsed")
            {
                return;
            }

            var evt = JsonSerializer.Deserialize<ProtocolEvent<ScriptParsedEvent>>(json, ProtocolJson.Options);
            if (evt?.Params == null || string.IsNullOrWhiteSpace(evt.Params.ScriptId))
            {
                return;
            }

            UpsertScript(new ScriptListItem(
                evt.Params.ScriptId,
                evt.Params.Url,
                string.IsNullOrWhiteSpace(evt.Params.Url),
                evt.Params.Length));

            if (_selectedScript == null && _scripts.Count > 0)
            {
                SelectScript(_scripts[0]);
            }

            Invalidate();
        }
        catch
        {
        }
    }

    protected override void OnPaint(SKCanvas canvas, SKRect bounds)
    {
        var listBounds = new SKRect(bounds.Left, bounds.Top, Math.Min(bounds.Right, bounds.Left + LIST_WIDTH), bounds.Bottom);
        var sourceBounds = new SKRect(listBounds.Right + 1, bounds.Top, bounds.Right, bounds.Bottom);

        DrawScriptList(canvas, listBounds);
        DrawSourceView(canvas, sourceBounds);
    }

    private void DrawScriptList(SKCanvas canvas, SKRect bounds)
    {
        using var headerBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        using var headerFont = DevToolsTheme.CreateUIFont(DevToolsTheme.FontSizeSmall);
        using var headerColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextSecondary);
        using var textFont = DevToolsTheme.CreateTextFont();
        using var textColorPaint = DevToolsTheme.CreateTextColorPaint();
        using var mutedFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
        using var mutedColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);

        canvas.DrawRect(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + HEADER_HEIGHT), headerBgPaint);
        canvas.DrawText("Scripts", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + HEADER_HEIGHT - 6, headerFont, headerColorPaint);
        canvas.DrawLine(bounds.Right, bounds.Top, bounds.Right, bounds.Bottom, borderPaint);
        canvas.DrawLine(bounds.Left, bounds.Top + HEADER_HEIGHT, bounds.Right, bounds.Top + HEADER_HEIGHT, borderPaint);

        float y = bounds.Top + HEADER_HEIGHT - ScrollY;
        for (int i = 0; i < _scripts.Count; i++)
        {
            float itemY = y + i * DevToolsTheme.ItemHeight;
            if (itemY + DevToolsTheme.ItemHeight < bounds.Top + HEADER_HEIGHT) continue;
            if (itemY > bounds.Bottom) break;

            var script = _scripts[i];
            if (script == _selectedScript)
            {
                using var selectPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundSelected);
                canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), selectPaint);
            }
            else if (i == _hoveredIndex)
            {
                using var hoverPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundHover);
                canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), hoverPaint);
            }

            var title = script.DisplayName.Length > 28 ? script.DisplayName[..25] + "..." : script.DisplayName;
            canvas.DrawText(title, bounds.Left + DevToolsTheme.PaddingNormal, itemY + 14, textFont, textColorPaint);

            var metadata = script.IsInline ? $"{script.OriginGroup} inline" : $"{script.OriginGroup} - {Math.Max(1, script.Length)} chars";
            canvas.DrawText(metadata, bounds.Left + DevToolsTheme.PaddingNormal, itemY + DevToolsTheme.ItemHeight - 5, mutedFont, mutedColorPaint);
        }

        if (_scripts.Count == 0)
        {
            using var hintFont = DevToolsTheme.CreateTextFont();
            using var hintColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("No scripts loaded", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + HEADER_HEIGHT + 30, hintFont, hintColorPaint);
        }

        MaxScrollY = Math.Max(0, _scripts.Count * DevToolsTheme.ItemHeight - (bounds.Height - HEADER_HEIGHT));
    }

    private void DrawSourceView(SKCanvas canvas, SKRect bounds)
    {
        using var headerBgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        using var headerFont = DevToolsTheme.CreateUIFont(DevToolsTheme.FontSizeSmall);
        using var headerColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextSecondary);
        using var lineNumberFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
        using var lineNumberColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);
        using var sourceFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
        using var sourceColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextPrimary);

        canvas.DrawRect(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + HEADER_HEIGHT), headerBgPaint);
        canvas.DrawText(_selectedScript?.Url ?? "Source", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + HEADER_HEIGHT - 6, headerFont, headerColorPaint);
        canvas.DrawLine(bounds.Left, bounds.Top + HEADER_HEIGHT, bounds.Right, bounds.Top + HEADER_HEIGHT, borderPaint);

        if (string.IsNullOrEmpty(_sourceText))
        {
            using var hintFont = DevToolsTheme.CreateTextFont();
            using var hintColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("Select a script to inspect its source.", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + HEADER_HEIGHT + 24, hintFont, hintColorPaint);
            return;
        }

        var lines = _sourceText.Replace("\r\n", "\n").Split('\n');
        _sourceMaxScrollY = Math.Max(0, lines.Length * SOURCE_LINE_HEIGHT - (bounds.Height - HEADER_HEIGHT - DevToolsTheme.PaddingNormal));

        float y = bounds.Top + HEADER_HEIGHT + DevToolsTheme.PaddingNormal - _sourceScrollY;
        for (int i = 0; i < lines.Length; i++)
        {
            if (y + SOURCE_LINE_HEIGHT < bounds.Top + HEADER_HEIGHT) { y += SOURCE_LINE_HEIGHT; continue; }
            if (y > bounds.Bottom) break;

            canvas.DrawText((i + 1).ToString(), bounds.Left + DevToolsTheme.PaddingNormal, y + 11, lineNumberFont, lineNumberColorPaint);
            canvas.DrawText(lines[i], bounds.Left + 52, y + 11, sourceFont, sourceColorPaint);
            y += SOURCE_LINE_HEIGHT;
        }
    }

    public override void OnMouseMove(float x, float y)
    {
        Host?.RequestCursorChange(CursorType.Default);

        if (x <= Bounds.Left + LIST_WIDTH)
        {
            int index = GetScriptIndexAt(y);
            if (_hoveredIndex != index)
            {
                _hoveredIndex = index;
                Invalidate();
            }
        }
        else if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            Invalidate();
        }
    }

    public override bool OnMouseDown(float x, float y, bool isRightButton)
    {
        if (x > Bounds.Left + LIST_WIDTH)
        {
            return false;
        }

        int index = GetScriptIndexAt(y);
        if (index < 0 || index >= _scripts.Count)
        {
            return false;
        }

        SelectScript(_scripts[index]);
        return true;
    }

    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        if (x <= Bounds.Left + LIST_WIDTH)
        {
            base.OnMouseWheel(x, y, deltaX, deltaY);
            return;
        }

        _sourceScrollY = Math.Clamp(_sourceScrollY - deltaY * 40, 0, _sourceMaxScrollY);
        Invalidate();
    }

    private int GetScriptIndexAt(float y)
    {
        float relativeY = y - Bounds.Top - HEADER_HEIGHT + ScrollY;
        int index = (int)(relativeY / DevToolsTheme.ItemHeight);
        return index >= 0 && index < _scripts.Count ? index : -1;
    }

    private void UpsertScript(ScriptListItem item)
    {
        int index = _scripts.FindIndex(existing => string.Equals(existing.ScriptId, item.ScriptId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _scripts[index] = item;
            if (_selectedScript?.ScriptId == item.ScriptId)
            {
                _selectedScript = item;
            }
        }
        else
        {
            _scripts.Add(item);
        }

        _scripts.Sort((left, right) =>
        {
            var originCompare = string.Compare(left.OriginGroup, right.OriginGroup, StringComparison.OrdinalIgnoreCase);
            return originCompare != 0
                ? originCompare
                : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void SelectScript(ScriptListItem item)
    {
        _selectedScript = item;
        _sourceText = string.Empty;
        _sourceScrollY = 0;
        _ = LoadSelectedScriptAsync(item.ScriptId);
        Invalidate();
    }

    private async Task LoadSelectedScriptAsync(string scriptId)
    {
        if (Host == null)
        {
            return;
        }

        try
        {
            var request = new ProtocolRequest<object>
            {
                Id = 4101,
                Method = "Debugger.getScriptSource",
                Params = new { scriptId }
            };

            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = JsonSerializer.Deserialize<ProtocolResponse<GetScriptSourceResult>>(responseJson, ProtocolJson.Options);
            if (response?.Result?.ScriptSource != null && _selectedScript?.ScriptId == scriptId)
            {
                _sourceText = response.Result.ScriptSource;
                Invalidate();
            }
        }
        catch
        {
        }
    }

    private sealed record ScriptListItem(string ScriptId, string Url, bool IsInline, int Length)
    {
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Url))
                {
                    return $"(inline) {ScriptId}";
                }

                try
                {
                    var fileName = Path.GetFileName(new Uri(Url).AbsolutePath);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        return fileName;
                    }
                }
                catch
                {
                }

                return Url;
            }
        }

        public string OriginGroup
        {
            get
            {
                if (IsInline)
                {
                    return "inline";
                }

                if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                {
                    return uri.Host;
                }

                return "local";
            }
        }
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
