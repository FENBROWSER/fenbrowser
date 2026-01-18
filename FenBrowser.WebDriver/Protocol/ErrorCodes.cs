// =============================================================================
// ErrorCodes.cs
// W3C WebDriver Error Codes (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §6.6 - Errors
//                 https://www.w3.org/TR/webdriver2/#errors
// =============================================================================

namespace FenBrowser.WebDriver.Protocol
{
    /// <summary>
    /// W3C WebDriver error codes per spec.
    /// </summary>
    public static class ErrorCodes
    {
        // Session errors
        public const string SessionNotCreated = "session not created";
        public const string InvalidSessionId = "invalid session id";
        
        // Element errors
        public const string NoSuchElement = "no such element";
        public const string StaleElementReference = "stale element reference";
        public const string ElementNotInteractable = "element not interactable";
        public const string ElementClickIntercepted = "element click intercepted";
        public const string InvalidElementState = "invalid element state";
        
        // Navigation errors
        public const string NoSuchWindow = "no such window";
        public const string NoSuchFrame = "no such frame";
        public const string NoSuchAlert = "no such alert";
        public const string Timeout = "timeout";
        
        // Script errors
        public const string JavaScriptError = "javascript error";
        public const string ScriptTimeout = "script timeout";
        
        // Argument errors
        public const string InvalidArgument = "invalid argument";
        public const string InvalidSelector = "invalid selector";
        
        // General errors
        public const string UnknownCommand = "unknown command";
        public const string UnknownMethod = "unknown method";
        public const string UnknownError = "unknown error";
        public const string UnsupportedOperation = "unsupported operation";
        
        // Security
        public const string InsecureCertificate = "insecure certificate";
        
        /// <summary>
        /// Get HTTP status code for error.
        /// </summary>
        public static int GetHttpStatus(string errorCode)
        {
            return errorCode switch
            {
                SessionNotCreated => 500,
                InvalidSessionId => 404,
                NoSuchElement => 404,
                NoSuchWindow => 404,
                NoSuchFrame => 404,
                NoSuchAlert => 404,
                StaleElementReference => 404,
                ElementNotInteractable => 400,
                ElementClickIntercepted => 400,
                InvalidElementState => 400,
                InvalidArgument => 400,
                InvalidSelector => 400,
                UnknownCommand => 404,
                UnknownMethod => 405,
                Timeout => 408,
                ScriptTimeout => 408,
                JavaScriptError => 500,
                InsecureCertificate => 400,
                UnsupportedOperation => 500,
                _ => 500
            };
        }
    }
}
