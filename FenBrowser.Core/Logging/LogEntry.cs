using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Represents a single log entry with all associated metadata.
    /// Supports both human-readable and structured JSON output.
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogCategory Category { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public string ThreadId { get; set; }
        public Exception Exception { get; set; }
        
        // Structured logging properties
        public string SourceFile { get; set; }
        public int SourceLine { get; set; }
        public string MethodName { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public string CorrelationId { get; set; }
        public long? DurationMs { get; set; }
        public long? MemoryBytes { get; set; }

        public LogEntry()
        {
            Timestamp = DateTime.UtcNow;
            ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId.ToString();
        }

        /// <summary>
        /// Create a log entry with structured data
        /// </summary>
        public LogEntry WithData(string key, object value)
        {
            Data ??= new Dictionary<string, object>();
            Data[key] = value;
            return this;
        }

        /// <summary>
        /// Add correlation ID for request tracing
        /// </summary>
        public LogEntry WithCorrelation(string correlationId)
        {
            CorrelationId = correlationId;
            return this;
        }

        /// <summary>
        /// Add performance data
        /// </summary>
        public LogEntry WithPerformance(long durationMs, long? memoryBytes = null)
        {
            DurationMs = durationMs;
            MemoryBytes = memoryBytes;
            return this;
        }

        public override string ToString()
        {
            var levelStr = Level.ToString().ToUpper().PadRight(5);
            var categoryStr = Category.ToString().PadRight(12);
            var time = Timestamp.ToString("HH:mm:ss.fff");
            var msg = Exception != null ? $"{Message} - {Exception.Message}" : Message;
            
            var extra = new StringBuilder();
            if (DurationMs.HasValue)
                extra.Append($" [{DurationMs}ms]");
            if (!string.IsNullOrEmpty(CorrelationId))
                extra.Append($" [CID:{CorrelationId}]");
                
            return $"{time} [{levelStr}] {categoryStr} | {msg}{extra}";
        }

        /// <summary>
        /// Convert log entry to structured JSON format
        /// </summary>
        public string ToJson()
        {
            try
            {
                var obj = new Dictionary<string, object>
                {
                    ["@timestamp"] = Timestamp.ToString("O"),
                    ["level"] = Level.ToString().ToLower(),
                    ["category"] = Category.ToString(),
                    ["message"] = Message ?? string.Empty,
                    ["threadId"] = ThreadId
                };

                if (!string.IsNullOrEmpty(CorrelationId))
                    obj["correlationId"] = CorrelationId;

                if (DurationMs.HasValue)
                    obj["durationMs"] = DurationMs.Value;

                if (MemoryBytes.HasValue)
                    obj["memoryBytes"] = MemoryBytes.Value;

                if (!string.IsNullOrEmpty(SourceFile))
                {
                    obj["source"] = new Dictionary<string, object>
                    {
                        ["file"] = SourceFile,
                        ["line"] = SourceLine,
                        ["method"] = MethodName ?? string.Empty
                    };
                }

                if (Exception != null)
                {
                    obj["exception"] = new Dictionary<string, object>
                    {
                        ["type"] = Exception.GetType().FullName,
                        ["message"] = Exception.Message,
                        ["stackTrace"] = Exception.StackTrace ?? string.Empty
                    };
                }

                if (Data != null && Data.Count > 0)
                {
                    obj["data"] = Data;
                }

                return JsonSerializer.Serialize(obj, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            }
            catch
            {
                // Fallback to simple format
                return $"{{\"message\":\"{Message?.Replace("\"", "\\\"")}\"}}";
            }
        }

        /// <summary>
        /// Parse a JSON log line back to LogEntry (for log file reading)
        /// </summary>
        public static LogEntry FromJson(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var entry = new LogEntry
                {
                    Message = root.TryGetProperty("message", out var msg) ? msg.GetString() : string.Empty,
                    ThreadId = root.TryGetProperty("threadId", out var tid) ? tid.GetString() : "0"
                };

                if (root.TryGetProperty("@timestamp", out var ts) && DateTime.TryParse(ts.GetString(), out var dt))
                    entry.Timestamp = dt;

                if (root.TryGetProperty("level", out var lvl) && Enum.TryParse<LogLevel>(lvl.GetString(), true, out var level))
                    entry.Level = level;

                if (root.TryGetProperty("category", out var cat) && Enum.TryParse<LogCategory>(cat.GetString(), true, out var category))
                    entry.Category = category;

                if (root.TryGetProperty("correlationId", out var cid))
                    entry.CorrelationId = cid.GetString();

                if (root.TryGetProperty("durationMs", out var dur))
                    entry.DurationMs = dur.GetInt64();

                return entry;
            }
            catch
            {
                return new LogEntry { Message = json };
            }
        }
    }
}
