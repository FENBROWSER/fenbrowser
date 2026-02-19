using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FenBrowser.WebDriver.Protocol
{
    public class WdElementRect
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("height")]
        public double Height { get; set; }
    }

    public class WdCookie
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = "/";

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("secure")]
        public bool Secure { get; set; }

        [JsonPropertyName("httpOnly")]
        public bool HttpOnly { get; set; }

        [JsonPropertyName("expiry")]
        public long? Expiry { get; set; }

        [JsonPropertyName("sameSite")]
        public string SameSite { get; set; } = "Lax";
    }

    public class WdActionSequence
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("actions")]
        public List<WdActionItem> Actions { get; set; } = new();
    }

    public class WdActionItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("button")]
        public int Button { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("origin")]
        public string Origin { get; set; } = string.Empty;
    }

    public class WdPrintOptions
    {
        [JsonPropertyName("orientation")]
        public string Orientation { get; set; } = "portrait";

        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1.0;

        [JsonPropertyName("page")]
        public WdPrintPageOptions Page { get; set; } = new();
    }

    public class WdPrintPageOptions
    {
        [JsonPropertyName("width")]
        public double Width { get; set; } = 8.27;

        [JsonPropertyName("height")]
        public double Height { get; set; } = 11.69;
    }
}
