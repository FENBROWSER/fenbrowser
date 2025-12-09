using System;

namespace FenBrowser.UI
{
    public enum ConsoleLogLevel { Info, Warning, Error, Input, Result }

    public class ConsoleLogItem
    {
        public string Message { get; }
        public ConsoleLogLevel Level { get; }
        public DateTime Timestamp { get; }

        public ConsoleLogItem(string msg, ConsoleLogLevel level)
        {
            Message = msg;
            Level = level;
            Timestamp = DateTime.Now;
        }

        // Helper for XAML binding to choose icon/color
        public string IconKind 
        { 
            get 
            {
                switch (Level)
                {
                    case ConsoleLogLevel.Error: return "Error";
                    case ConsoleLogLevel.Warning: return "Warning";
                    case ConsoleLogLevel.Input: return "Input";
                    case ConsoleLogLevel.Result: return "Result";
                    default: return "Info";
                }
            }
        }

        public string TextColor
        {
            get
            {
                switch (Level)
                {
                    case ConsoleLogLevel.Error: return "#FF0000"; // Red
                    case ConsoleLogLevel.Warning: return "#5C3C00"; // Dark Orange/Brown
                    case ConsoleLogLevel.Input: return "#0078D7"; // Blue
                    case ConsoleLogLevel.Result: return "#666666"; // Gray
                    default: return "#333333"; // Black/Dark Gray
                }
            }
        }

        public string BackgroundColor
        {
            get
            {
                switch (Level)
                {
                    case ConsoleLogLevel.Error: return "#FFF0F0"; // Light Red
                    case ConsoleLogLevel.Warning: return "#FFFBE5"; // Light Yellow
                    default: return "Transparent";
                }
            }
        }
        
        public bool IsInputOrResult => Level == ConsoleLogLevel.Input || Level == ConsoleLogLevel.Result;
        public bool IsLog => !IsInputOrResult;
    }
}
