namespace FenBrowser.WebDriver.BiDi
{
    /// <summary>
    /// Transport bootstrap contract for future WebDriver BiDi endpoint registration.
    /// This phase intentionally wires only registration shape and no runtime socket behavior.
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
    }

    public sealed class BiDiTransportOptions
    {
        public string EndpointPath { get; init; } = "/session/{sessionId}/bidi";
        public bool Enabled { get; init; }
    }

    public sealed class NoOpBiDiTransportBootstrap : IBiDiTransportBootstrap
    {
        public void Register(BiDiBootstrapContext context)
        {
            // Intentional no-op skeleton: concrete BiDi transport registration is out of scope.
        }
    }
}
