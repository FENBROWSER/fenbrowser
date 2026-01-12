using SkiaSharp;
using System.Text.Json;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;
using FenBrowser.Core.Logging;

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
    private bool _showBrowserLogs = true; // Toggle to show FenLogger entries
    
    private const float INPUT_HEIGHT = 28f;
    
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
            
            // Load existing messages (Legacy fallback for phase D4 transition)
            foreach (var msg in Host.GetConsoleMessages())
            {
                AddEntry(msg);
            }
            
            // Subscribe to FenLogger for browser internal logs
            LogManager.LogEntryAdded += OnLogEntryAdded;
        }
    }
    
    private void OnLogEntryAdded(LogEntry entry)
    {
        if (!_showBrowserLogs) return;
        
        var level = entry.Level switch
        {
            LogLevel.Error => ConsoleLevel.Error,
            LogLevel.Warn => ConsoleLevel.Warn,
            LogLevel.Info => ConsoleLevel.Info,
            LogLevel.Debug => ConsoleLevel.Debug,
            _ => ConsoleLevel.Log
        };
        
        // Format: [Category] Message
        string msg = $"[{entry.Category}] {entry.Message}";
        
        _entries.Add(new ConsoleEntry(msg, level, entry.Timestamp, null, null));
        
        // Update scroll
        MaxScrollY = Math.Max(0, _entries.Count * DevToolsTheme.ItemHeight - Bounds.Height + INPUT_HEIGHT + 20);
        ScrollY = MaxScrollY; // Auto-scroll to bottom
        
        Invalidate();
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
                    foreach (var arg in evt.Params.Args)
                    {
                        var level = evt.Params.Type.ToLower() switch
                        {
                            "error" => ConsoleLevel.Error,
                            "warning" => ConsoleLevel.Warn,
                            "info" => ConsoleLevel.Info,
                            "debug" => ConsoleLevel.Debug,
                            _ => ConsoleLevel.Log
                        };

                        AddEntry(new ConsoleMessageInfo(
                            arg.Description ?? arg.Value?.ToString() ?? "undefined",
                            level,
                            DateTime.Now,
                            null, null, null
                        ));
                    }
                    Invalidate();
                }
            }
        }
        catch { }
    }
    
    private void AddEntry(ConsoleMessageInfo msg)
    {
        _entries.Add(new ConsoleEntry(
            msg.Message,
            msg.Level,
            msg.Timestamp,
            msg.SourceFile,
            msg.LineNumber
        ));
        
        // Update scroll
        MaxScrollY = Math.Max(0, _entries.Count * DevToolsTheme.ItemHeight - Bounds.Height + INPUT_HEIGHT + 20);
        ScrollY = MaxScrollY; // Auto-scroll to bottom
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
        float y = bounds.Top + DevToolsTheme.PaddingNormal + ScrollY;
        
        for (int i = 0; i < _entries.Count; i++)
        {
            float itemY = y + i * DevToolsTheme.ItemHeight;
            
            // Skip if outside visible area
            if (itemY + DevToolsTheme.ItemHeight < bounds.Top - ScrollY) continue;
            if (itemY > bounds.Bottom - ScrollY) break;
            
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
            
            using var iconPaint = DevToolsTheme.CreateTextPaint(color, DevToolsTheme.FontSizeSmall);
            canvas.DrawText(icon, x, textY, iconPaint);
            x += 20;
            
            // Message
            using var msgPaint = DevToolsTheme.CreateTextPaint(color);
            string msg = entry.Message;
            if (msg.Length > 100) msg = msg.Substring(0, 97) + "...";
            canvas.DrawText(msg, x, textY, msgPaint);
            
            // Source location
            if (!string.IsNullOrEmpty(entry.Source))
            {
                string source = System.IO.Path.GetFileName(entry.Source);
                if (entry.Line.HasValue) source += $":{entry.Line}";
                
                using var sourcePaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted, DevToolsTheme.FontSizeSmall);
                float sourceWidth = sourcePaint.MeasureText(source);
                canvas.DrawText(source, bounds.Right - sourceWidth - DevToolsTheme.PaddingNormal, textY, sourcePaint);
            }
        }
        
        // Empty state
        if (_entries.Count == 0)
        {
            using var hintPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TextMuted);
            canvas.DrawText("No console messages", bounds.Left + DevToolsTheme.PaddingNormal, bounds.Top + 30, hintPaint);
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
        using var promptPaint = DevToolsTheme.CreateTextPaint(DevToolsTheme.TabBorder);
        canvas.DrawText(">", x, textY, promptPaint);
        x += 16;
        
        // Input text
        using var textPaint = DevToolsTheme.CreateTextPaint();
        canvas.DrawText(_inputText, x, textY, textPaint);
        
        // Cursor
        if (_inputFocused)
        {
            float cursorX = x + textPaint.MeasureText(_inputText.Substring(0, Math.Min(_cursorPosition, _inputText.Length)));
            using var cursorPaint = DevToolsTheme.CreateFillPaint(DevToolsTheme.TextPrimary);
            canvas.DrawRect(new SKRect(cursorX, bounds.Top + 6, cursorX + 1, bounds.Bottom - 6), cursorPaint);
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
        _entries.Add(new ConsoleEntry("> " + input, ConsoleLevel.Log, DateTime.Now, null, null));
        
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
                string output = response.Result.Result.Description ?? "undefined";
                _entries.Add(new ConsoleEntry("< " + output, ConsoleLevel.Log, DateTime.Now, null, null));
            }
            else if (response?.Error != null)
            {
                _entries.Add(new ConsoleEntry(response.Error.Message, ConsoleLevel.Error, DateTime.Now, null, null));
            }
        }
        catch (Exception ex)
        {
            _entries.Add(new ConsoleEntry(ex.Message, ConsoleLevel.Error, DateTime.Now, null, null));
        }
        
        // Scroll to bottom
        MaxScrollY = Math.Max(0, _entries.Count * DevToolsTheme.ItemHeight - Bounds.Height + INPUT_HEIGHT + 20);
        ScrollY = MaxScrollY;
        
        Invalidate();
    }
    
    /// <summary>
    /// Console entry.
    /// </summary>
    private record ConsoleEntry(string Message, ConsoleLevel Level, DateTime Timestamp, string? Source, int? Line);
}
