using System;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Represents a single log entry with all associated metadata.
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogCategory Category { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public string ThreadId { get; set; }
        public Exception Exception { get; set; }

        public LogEntry()
        {
            Timestamp = DateTime.Now;
            ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId.ToString();
        }

        public override string ToString()
        {
            var levelStr = Level.ToString().ToUpper().PadRight(5);
            var categoryStr = Category.ToString().PadRight(12);
            var time = Timestamp.ToString("HH:mm:ss.fff");
            var msg = Exception != null ? $"{Message} - {Exception.Message}" : Message;
            return $"{time} [{levelStr}] {categoryStr} | {msg}";
        }
    }
}
