// =============================================================================
// WebDriverResponse.cs
// W3C WebDriver Response Types (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §6.3 - Processing Model
//                 https://www.w3.org/TR/webdriver2/#processing-model
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenBrowser.WebDriver.Protocol
{
    /// <summary>
    /// W3C WebDriver response wrapper.
    /// </summary>
    public class WebDriverResponse
    {
        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object Value { get; set; }
        
        public static WebDriverResponse Success(object value = null)
        {
            return new WebDriverResponse { Value = value };
        }
        
        public static WebDriverResponse Error(string error, string message, string stacktrace = null, object data = null)
        {
            return new WebDriverResponse
            {
                Value = new WebDriverError
                {
                    Error = error,
                    Message = message,
                    Stacktrace = stacktrace ?? "",
                    Data = data
                }
            };
        }
        
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }
    }
    
    /// <summary>
    /// W3C WebDriver error object.
    /// </summary>
    public class WebDriverError
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
        
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
        
        [JsonPropertyName("stacktrace")]
        public string Stacktrace { get; set; } = string.Empty;
        
        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object Data { get; set; }
    }
    
    /// <summary>
    /// Session creation response.
    /// </summary>
    public class NewSessionResponse
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }
        
        [JsonPropertyName("capabilities")]
        public Capabilities Capabilities { get; set; }
    }
    
    /// <summary>
    /// Element reference.
    /// </summary>
    public class ElementReference
    {
        public const string Identifier = "element-6066-11e4-a52e-4f735466cecf";
        
        [JsonPropertyName("element-6066-11e4-a52e-4f735466cecf")]
        public string ElementId { get; set; }
        
        public ElementReference(string id)
        {
            ElementId = id;
        }
    }

    /// <summary>
    /// Shadow root reference.
    /// </summary>
    public class ShadowRootReference
    {
        public const string Identifier = "shadow-6066-11e4-a52e-4f735466cecf";

        [JsonPropertyName("shadow-6066-11e4-a52e-4f735466cecf")]
        public string ShadowId { get; set; }

        public ShadowRootReference(string id)
        {
            ShadowId = id;
        }
    }
    
    /// <summary>
    /// Window rect for position/size.
    /// </summary>
    public class WindowRect
    {
        [JsonPropertyName("x")]
        public int X { get; set; }
        
        [JsonPropertyName("y")]
        public int Y { get; set; }
        
        [JsonPropertyName("width")]
        public int Width { get; set; }
        
        [JsonPropertyName("height")]
        public int Height { get; set; }
    }
}
