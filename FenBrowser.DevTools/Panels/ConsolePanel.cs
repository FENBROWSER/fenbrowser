using SkiaSharp;
using System.Text.Json;
using System.Threading;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;

namespace FenBrowser.DevTools.Panels;

/// <summary>
/// Console panel for JavaScript execution and log display.
/// </summary>
public class ConsolePanel : DevToolsPanelBase
{
    public override string Title => "Console";
    public override string? Shortcut => "Ctrl+Shift+J";
    
    private readonly List<ConsoleEntry> _entries = new();
    private string _inputText = "";
    private int _cursorPosition;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private bool _inputFocused;
    private bool _showBrowserLogs; // Default false: engine logs hidden by default
    private readonly SemaphoreSlim _protocolEntryLock = new(1, 1);
    private int _hostVersion;
    private bool _userHasScrolledUp;

    // Entry cap and collapse state.
    private const int MaxEntries = 5000;
    private const int BatchTrimSize = 500;
    private int _protocolParseFailureCount;

    private const float INPUT_HEIGHT = 28f;
    
    protected override void OnHostChanging(IDevToolsHost? previousHost)
    {
        _hostVersion++;
        if (previousHost != null)
        {
            previousHost.ProtocolEventReceived -= OnProtocolEvent;
        }

        _entries.Clear();
        ScrollY = 0;
        MaxScrollY = 0;
    }

    protected override void OnHostChanged()
    {
        if (Host != null)
        {
            Host.ProtocolEventReceived += OnProtocolEvent;
            
            // Enable Runtime domain
            _ = Host.SendProtocolCommandAsync(JsonSerializer.Serialize(new ProtocolRequest<object>
            {
                Method = "Runtime.enable",
                Params = new { }
            }, ProtocolJson.Options));
            _ = Host.SendProtocolCommandAsync(JsonSerializer.Serialize(new ProtocolRequest<object>
            {
                Method = "Log.enable",
                Params = new { }
            }, ProtocolJson.Options));
            
            // Load existing messages (Legacy fallback for phase D4 transition)
            foreach (var msg in Host.GetConsoleMessages())
            {
                AddEntry(msg);
            }
            
        }
    }

    private void OnProtocolEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("method", out var methodProp)) return;
            var method = methodProp.GetString();

            if (method == "Runtime.consoleAPICalled")
            {
                var evt = JsonSerializer.Deserialize<ProtocolEvent<ConsoleAPICalledEvent>>(json, ProtocolJson.Options);
                if (evt?.Params != null)
                {
                    _ = AppendProtocolConsoleEntriesAsync(evt.Params, _hostVersion);
                    Invalidate();
                }
            }
            else if (method == "Log.entryAdded")
            {
                var evt = JsonSerializer.Deserialize<ProtocolEvent<LogEntryAddedEvent>>(json, ProtocolJson.Options);
                if (evt?.Params?.Entry != null && _showBrowserLogs)
                {
                    AppendEngineLogEntry(evt.Params.Entry);
                }
            }
        }
        catch (Exception ex)
        {
            // Increment failure counter; emit at most one rate-limited diagnostic.
            Interlocked.Increment(ref _protocolParseFailureCount);
            if (_protocolParseFailureCount <= 3)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DevTools.Console] Protocol parse failure #{_protocolParseFailureCount}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void AppendEngineLogEntry(LogEntryPayload entry)
    {
        var level = entry.Severity?.ToLowerInvariant() switch
        {
            "fatal" => ConsoleLevel.Error,
            "error" => ConsoleLevel.Error,
            "warn" => ConsoleLevel.Warn,
            "info" => ConsoleLevel.Info,
            "debug" => ConsoleLevel.Debug,
            "trace" => ConsoleLevel.Debug,
            _ => ConsoleLevel.Log
        };

        DateTime timestamp;
        if (!DateTime.TryParse(entry.TimestampUtc, out timestamp))
        {
            timestamp = DateTime.Now;
        }

        var markerText = string.IsNullOrWhiteSpace(entry.Marker) || string.Equals(entry.Marker, "None", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"[{entry.Marker}]";
        var message = $"[Eng][{entry.Subsystem}][{entry.Severity}]{markerText} {entry.Message}".TrimEnd();

        AddEntryInternal(new ConsoleEntry(message, level, timestamp.ToLocalTime(), entry.SourceFile, entry.SourceLine > 0 ? entry.SourceLine : null));
    }
    
    private void AddEntry(ConsoleMessageInfo msg)
    {
        AddEntryInternal(new ConsoleEntry(
            msg.Message,
            msg.Level,
            msg.Timestamp,
            msg.SourceFile,
            msg.LineNumber
        ));
    }

    private void AddEntryInternal(ConsoleEntry entry)
    {
        // Collapse repeated messages: update the last entry's repeat count if it matches.
        if (_entries.Count > 0)
        {
            var last = _entries[_entries.Count - 1];
            if (string.Equals(last.Message, entry.Message, StringComparison.Ordinal) &&
                last.Level == entry.Level)
            {
                last.RepeatCount++;
                last.Timestamp = entry.Timestamp;
                Invalidate();
                return;
            }
        }

        _entries.Add(entry);

        // Hard cap: trim oldest entries in batches.
        if (_entries.Count > MaxEntries)
        {
            var trimCount = Math.Min(BatchTrimSize, _entries.Count - MaxEntries + BatchTrimSize);
            if (trimCount > 0 && trimCount < _entries.Count)
            {
                _entries.RemoveRange(0, trimCount);
                // Adjust scroll to compensate for removed entries.
                ScrollY = Math.Max(0, ScrollY - trimCount * DevToolsTheme.ItemHeight);
            }
        }

        // Update scroll — only auto-scroll if user hasn't manually scrolled up.
        MaxScrollY = Math.Max(0, _entries.Count * DevToolsTheme.ItemHeight - Bounds.Height + INPUT_HEIGHT + 20);
        if (!_userHasScrolledUp)
        {
            ScrollY = MaxScrollY;
        }

        Invalidate();
    }
    
    protected override void OnPaint(SKCanvas canvas, SKRect bounds)
    {
        // Log entries area
        var logBounds = new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - INPUT_HEIGHT);
        DrawLogs(canvas, logBounds);
        
        // Input area
        var inputBounds = new SKRect(bounds.Left, bounds.Bottom - INPUT_HEIGHT, bounds.Right, bounds.Bottom);
        DrawInput(canvas, inputBounds);
    }
    
    private void DrawLogs(SKCanvas canvas, SKRect bounds)
    {
        float y = bounds.Top + DevToolsTheme.PaddingNormal - ScrollY;
        
        for (int i = 0; i < _entries.Count; i++)
        {
            float itemY = y + i * DevToolsTheme.ItemHeight;
            
            // Skip if outside visible area
            if (itemY + DevToolsTheme.ItemHeight < bounds.Top) continue;
            if (itemY > bounds.Bottom) break;
            
            var entry = _entries[i];
            
            // Get color based on level
            SKColor color = entry.Level switch
            {
                ConsoleLevel.Error => DevToolsTheme.ConsoleError,
                ConsoleLevel.Warn => DevToolsTheme.ConsoleWarn,
                ConsoleLevel.Info => DevToolsTheme.ConsoleInfo,
                ConsoleLevel.Debug => DevToolsTheme.TextMuted,
                _ => DevToolsTheme.ConsoleLog
            };
            
            // Draw background for errors/warnings
            if (entry.Level == ConsoleLevel.Error)
            {
                using var bgPaint = DevToolsTheme.CreateFillPaint(new SKColor(50, 20, 20));
                canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), bgPaint);
            }
            else if (entry.Level == ConsoleLevel.Warn)
            {
                using var bgPaint = DevToolsTheme.CreateFillPaint(new SKColor(50, 45, 20));
                canvas.DrawRect(new SKRect(bounds.Left, itemY, bounds.Right, itemY + DevToolsTheme.ItemHeight), bgPaint);
            }
            
            float x = bounds.Left + DevToolsTheme.PaddingNormal;
            float textY = itemY + DevToolsTheme.ItemHeight / 2 + 4;
            
            // Level icon
            string icon = entry.Level switch
            {
                ConsoleLevel.Error => "✕",
                ConsoleLevel.Warn => "⚠",
                ConsoleLevel.Info => "ℹ",
                _ => "›"
            };
            
            using var iconFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
            using var iconColorPaint = DevToolsTheme.CreateTextColorPaint(color);
            canvas.DrawText(icon, x, textY, iconFont, iconColorPaint);
            x += 20;

            // Message (with collapse count for repeated entries)
            using var msgFont = DevToolsTheme.CreateTextFont();
            using var msgColorPaint = DevToolsTheme.CreateTextColorPaint(color);
            var displayText = entry.RepeatCount > 1
                ? $"{entry.Message}  × {entry.RepeatCount}"
                : entry.Message;
            canvas.DrawText(displayText, x, textY, msgFont, msgColorPaint);

            // Source location
            if (!string.IsNullOrEmpty(entry.Source))
            {
                string source = System.IO.Path.GetFileName(entry.Source);
                if (entry.Line.HasValue) source += $":{entry.Line}";

                using var sourceFont = DevToolsTheme.CreateTextFont(DevToolsTheme.FontSizeSmall);
                using var sourceColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);
                float sourceWidth = sourceFont.MeasureText(source);
                canvas.DrawText(source, bounds.Right - sourceWidth - DevToolsTheme.PaddingNormal, textY, sourceFont, sourceColorPaint);
            }
        }

        // Empty state
        if (_entries.Count == 0)
        {
            using var hintFont = DevToolsTheme.CreateTextFont();
            using var hintColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("No console messages", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + 30, hintFont, hintColorPaint);
        }
    }
    
    private void DrawInput(SKCanvas canvas, SKRect bounds)
    {
        // Background
        using var bgPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.BackgroundLight);
        canvas.DrawRect(bounds, bgPaint);
        
        // Top border
        using var borderPaint = DevToolsTheme.CreateStrokePaint(DevToolsTheme.Border);
        canvas.DrawLine(bounds.Left, bounds.Top, bounds.Right, bounds.Top, borderPaint);
        
        float x = bounds.Left + DevToolsTheme.PaddingNormal;
        float textY = bounds.MidY + 4;
        
        // Prompt
        using var promptFont = DevToolsTheme.CreateTextFont();
        using var promptColorPaint = DevToolsTheme.CreateTextColorPaint(DevToolsTheme.TabBorder);
        canvas.DrawText(">", x, textY, promptFont, promptColorPaint);
        x += 16;

        // Input text
        using var textFont = DevToolsTheme.CreateTextFont();
        using var textColorPaint = DevToolsTheme.CreateTextColorPaint();
        canvas.DrawText(_inputText, x, textY, textFont, textColorPaint);

        // Cursor
        if (_inputFocused)
        {
            float cursorX = x + textFont.MeasureText(_inputText.Substring(0, Math.Min(_cursorPosition, _inputText.Length)));
            using var cursorPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.TextPrimary);
            canvas.DrawRect(new SKRect(cursorX, bounds.Top + 6, cursorX + 1, bounds.Bottom - 6), cursorPaint);
        }
    }
    
    public override void OnMouseWheel(float x, float y, float deltaX, float deltaY)
    {
        var wasAtBottom = MaxScrollY <= 0 || ScrollY >= MaxScrollY - 4;

        base.OnMouseWheel(x, y, deltaX, deltaY);

        // If the user scrolls up from the bottom, mark as manually scrolled.
        // If they scroll back to the bottom, reset.
        if (MaxScrollY > 0 && ScrollY >= MaxScrollY - 4)
        {
            _userHasScrolledUp = false;
        }
        else if (!wasAtBottom)
        {
            _userHasScrolledUp = true;
        }
    }

    public override void OnMouseMove(float x, float y)
    {
        // Check if over input area
        bool isOverInput = y >= Bounds.Bottom - INPUT_HEIGHT;
        if (isOverInput)
        {
            Host?.RequestCursorChange(CursorType.Text);
        }
        else
        {
            Host?.RequestCursorChange(CursorType.Default);
        }
    }
    
    public override bool OnMouseDown(float x, float y, bool isRightButton)
    {
        // Check if clicked in input area
        _inputFocused = y >= Bounds.Bottom - INPUT_HEIGHT;
        Invalidate();
        return _inputFocused;
    }
    
    public override bool OnKeyDown(int keyCode, bool ctrl, bool shift, bool alt)
    {
        if (!_inputFocused) return false;
        
        switch (keyCode)
        {
            case 13: // Enter
                _ = ExecuteInput();
                return true;
            
            case 8: // Backspace
                if (_cursorPosition > 0 && _inputText.Length > 0)
                {
                    _inputText = _inputText.Remove(_cursorPosition - 1, 1);
                    _cursorPosition--;
                    Invalidate();
                }
                return true;
            
            case 46: // Delete
                if (_cursorPosition < _inputText.Length)
                {
                    _inputText = _inputText.Remove(_cursorPosition, 1);
                    Invalidate();
                }
                return true;
            
            case 37: // Left
                if (_cursorPosition > 0)
                {
                    _cursorPosition--;
                    Invalidate();
                }
                return true;
            
            case 39: // Right
                if (_cursorPosition < _inputText.Length)
                {
                    _cursorPosition++;
                    Invalidate();
                }
                return true;
            
            case 38: // Up (history)
                if (_history.Count > 0)
                {
                    if (_historyIndex < 0) _historyIndex = _history.Count;
                    if (_historyIndex > 0)
                    {
                        _historyIndex--;
                        _inputText = _history[_historyIndex];
                        _cursorPosition = _inputText.Length;
                        Invalidate();
                    }
                }
                return true;
            
            case 40: // Down (history)
                if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                    _inputText = _history[_historyIndex];
                    _cursorPosition = _inputText.Length;
                    Invalidate();
                }
                else
                {
                    _historyIndex = -1;
                    _inputText = "";
                    _cursorPosition = 0;
                    Invalidate();
                }
                return true;
            
            case 76 when ctrl: // Ctrl+L - Clear
                _entries.Clear();
                ScrollY = 0;
                MaxScrollY = 0;
                Invalidate();
                return true;
        }
        
        return false;
    }
    
    public override void OnTextInput(char c)
    {
        if (!_inputFocused) return;
        if (char.IsControl(c)) return;
        
        _inputText = _inputText.Insert(_cursorPosition, c.ToString());
        _cursorPosition++;
        Invalidate();
    }
    
    private async Task ExecuteInput()
    {
        if (string.IsNullOrWhiteSpace(_inputText) || Host == null) return;
        
        string input = _inputText.Trim();
        
        // Add to history
        _history.Add(input);
        _historyIndex = -1;
        
        // Add input as entry visually
        AddEntryInternal(new ConsoleEntry("> " + input, ConsoleLevel.Log, DateTime.Now, null, null));

        // Clear input immediately
        _inputText = "";
        _cursorPosition = 0;
        Invalidate();

        // Execute via protocol
        try
        {
            var request = new ProtocolRequest<object>
            {
                Method = "Runtime.evaluate",
                Params = new { expression = input }
            };

            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = JsonSerializer.Deserialize<ProtocolResponse<EvaluateResult>>(responseJson, ProtocolJson.Options);

            if (response?.Result?.Result != null)
            {
                string output = await FormatRemoteObjectAsync(response.Result.Result);
                AddEntryInternal(new ConsoleEntry("< " + output, ConsoleLevel.Log, DateTime.Now, null, null));
            }
            else if (response?.Error != null)
            {
                AddEntryInternal(new ConsoleEntry(response.Error.Message, ConsoleLevel.Error, DateTime.Now, null, null));
            }
        }
        catch (Exception ex)
        {
            AddEntryInternal(new ConsoleEntry(ex.Message, ConsoleLevel.Error, DateTime.Now, null, null));
        }
        
        // Scroll to bottom
        MaxScrollY = Math.Max(0, _entries.Count * DevToolsTheme.ItemHeight - Bounds.Height + INPUT_HEIGHT + 20);
        ScrollY = MaxScrollY;

        Invalidate();
    }

    private async Task AppendProtocolConsoleEntriesAsync(ConsoleAPICalledEvent evt, int hostVersion)
    {
        await _protocolEntryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (hostVersion != _hostVersion)
            {
                return;
            }

            var level = evt.Type.ToLowerInvariant() switch
            {
                "error" => ConsoleLevel.Error,
                "warning" => ConsoleLevel.Warn,
                "info" => ConsoleLevel.Info,
                "debug" => ConsoleLevel.Debug,
                _ => ConsoleLevel.Log
            };

            foreach (var arg in evt.Args)
            {
                if (hostVersion != _hostVersion)
                {
                    return;
                }

                var output = await FormatRemoteObjectAsync(arg).ConfigureAwait(false);
                AddEntry(new ConsoleMessageInfo(output, level, DateTime.Now, null, null, null));
            }

            Invalidate();
        }
        finally
        {
            _protocolEntryLock.Release();
        }
    }

    private async Task<string> FormatRemoteObjectAsync(RemoteObject remoteObject)
    {
        if (string.IsNullOrWhiteSpace(remoteObject.ObjectId) || Host == null)
        {
            return remoteObject.Description ?? remoteObject.Value?.ToString() ?? "undefined";
        }

        try
        {
            var request = new ProtocolRequest<object>
            {
                Id = 2101,
                Method = "Runtime.getProperties",
                Params = new { objectId = remoteObject.ObjectId, ownProperties = true }
            };

            var responseJson = await Host.SendProtocolCommandAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = JsonSerializer.Deserialize<ProtocolResponse<GetPropertiesResult>>(responseJson, ProtocolJson.Options);
            if (response?.Result?.Result is { Length: > 0 } properties)
            {
                return BuildObjectPreview(remoteObject, properties);
            }
        }
        catch
        {
            // Fall back to the remote-object description when property inspection fails.
        }

        return remoteObject.Description ?? remoteObject.Value?.ToString() ?? "undefined";
    }

    private static string BuildObjectPreview(RemoteObject remoteObject, RuntimePropertyDescriptor[] properties)
    {
        var visibleProperties = properties
            .Where(property => property.Enumerable)
            .Take(remoteObject.Subtype == "array" ? 5 : 6)
            .ToArray();

        if (visibleProperties.Length == 0)
        {
            return remoteObject.Description ?? "Object";
        }

        if (remoteObject.Subtype == "array")
        {
            var values = visibleProperties
                .Where(property => property.Name != "length")
                .Select(property => FormatPropertyValue(property.Value))
                .ToArray();

            return $"[{string.Join(", ", values)}]";
        }

        var preview = string.Join(", ", visibleProperties.Select(property => $"{property.Name}: {FormatPropertyValue(property.Value)}"));
        return "{ " + preview + " }";
    }

    private static string FormatPropertyValue(RemoteObject? value)
    {
        if (value == null)
        {
            return "undefined";
        }

        if (value.Type == "string")
        {
            return "\"" + (value.Value?.ToString() ?? string.Empty) + "\"";
        }

        return value.Description ?? value.Value?.ToString() ?? value.Type;
    }
    
    /// <summary>
    /// Console entry with deduplication support.
    /// </summary>
    private sealed class ConsoleEntry
    {
        public string Message { get; }
        public ConsoleLevel Level { get; }
        public DateTime Timestamp { get; set; }
        public string? Source { get; }
        public int? Line { get; }
        public int RepeatCount { get; set; }

        public ConsoleEntry(string message, ConsoleLevel level, DateTime timestamp, string? source, int? line)
        {
            Message = message;
            Level = level;
            Timestamp = timestamp;
            Source = source;
            Line = line;
            RepeatCount = 1;
        }
    }
}
