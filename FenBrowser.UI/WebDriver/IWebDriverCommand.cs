using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Interface for modular WebDriver command handlers.
    /// Each command module implements this interface to handle specific endpoint groups.
    /// </summary>
    public interface IWebDriverCommand
    {
        /// <summary>
        /// Determines if this handler can process the given request.
        /// </summary>
        /// <param name="method">HTTP method (GET, POST, DELETE)</param>
        /// <param name="path">Request path</param>
        /// <returns>True if this handler can process the request</returns>
        bool CanHandle(string method, string path);

        /// <summary>
        /// Executes the command and returns the response data.
        /// </summary>
        /// <param name="context">Request context containing method, path, body, and session</param>
        /// <returns>Response object to be serialized as JSON</returns>
        Task<WebDriverResponse> ExecuteAsync(WebDriverContext context);
    }

    /// <summary>
    /// Context passed to command handlers containing all request information.
    /// </summary>
    public class WebDriverContext
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public string[] PathSegments { get; set; }
        public JsonElement Body { get; set; }
        public WebDriverSession Session { get; set; }
        public IBrowser Browser { get; set; }

        /// <summary>
        /// Extracts a path parameter by position (e.g., session ID, element ID)
        /// </summary>
        public string GetPathParam(int index)
        {
            return index < PathSegments.Length ? PathSegments[index] : null;
        }

        /// <summary>
        /// Gets the session ID from the path (typically at index 2)
        /// </summary>
        /// <summary>
        /// Gets the session ID from the path (typically at index 1)
        /// </summary>
        public string SessionId => GetPathParam(1);

        /// <summary>
        /// Gets the element ID from the path (typically at index 3)
        /// </summary>
        public string ElementId => GetPathParam(3);
    }

    /// <summary>
    /// Standard WebDriver response wrapper.
    /// </summary>
    public class WebDriverResponse
    {
        public object Value { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }
        public int StatusCode { get; set; } = 200;

        public static WebDriverResponse Success(object value = null)
            => new WebDriverResponse { Value = value };

        public static WebDriverResponse Error404(string message)
            => new WebDriverResponse { StatusCode = 404, Error = "unknown command", Message = message };

        public static WebDriverResponse Error400(string message)
            => new WebDriverResponse { StatusCode = 400, Error = "invalid argument", Message = message };

        public static WebDriverResponse Error500(string message)
            => new WebDriverResponse { StatusCode = 500, Error = "unknown error", Message = message };

        public static WebDriverResponse NoSuchElement(string message)
            => new WebDriverResponse { StatusCode = 404, Error = "no such element", Message = message };

        public static WebDriverResponse NoSuchWindow(string message)
            => new WebDriverResponse { StatusCode = 404, Error = "no such window", Message = message };

        public static WebDriverResponse NoSuchCookie(string message)
            => new WebDriverResponse { StatusCode = 404, Error = "no such cookie", Message = message };

        public static WebDriverResponse NoAlertOpen(string message)
            => new WebDriverResponse { StatusCode = 404, Error = "no such alert", Message = message };

        public static WebDriverResponse InvalidSession()
            => new WebDriverResponse { StatusCode = 404, Error = "invalid session id", Message = "Session not found" };
    }
}
