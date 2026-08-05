namespace FenBrowser.WebDriver.BiDi
{
    /// <summary>
    /// Transport bootstrap contract for WebDriver BiDi endpoint registration.
    /// Implementations register the BiDi WebSocket endpoint with the server.
    /// </summary>
    public interface IBiDiTransportBootstrap
    {
        void Register(BiDiBootstrapContext context);
    }

    public sealed class BiDiBootstrapContext
    {
        public BiDiBootstrapContext(int webDriverPort)
        {
            WebDriverPort = webDriverPort;
        }

        public int WebDriverPort { get; }

        /// <summary>
        /// The session manager used to authenticate BiDi upgrades.
        /// </summary>
        public SessionManager? SessionManager { get; init; }
    }

    public sealed class BiDiTransportOptions
    {
        public string EndpointPath { get; init; } = "/session/{sessionId}/bidi";
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// Wires the BiDi WebSocket transport onto the WebDriver server port.
    /// </summary>
    public sealed class BiDiWebSocketTransportBootstrap : IBiDiTransportBootstrap
    {
        public void Register(BiDiBootstrapContext context)
        {
            if (context.SessionManager == null)
            {
                return;
            }

            var server = new BiDiWebSocketServer(context.SessionManager, context.WebDriverPort);
            server.Start();
        }
    }

    public sealed class NoOpBiDiTransportBootstrap : IBiDiTransportBootstrap
    {
        public void Register(BiDiBootstrapContext context)
        {
            // Intentional no-op skeleton: concrete BiDi transport registration is out of scope.
        }
    }
}
