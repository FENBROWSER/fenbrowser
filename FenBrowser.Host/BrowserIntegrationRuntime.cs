using System;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Host
{
    /// <summary>
    /// Bounded runtime configuration seam for Host-created <see cref="BrowserHost"/> instances.
    /// Tooling may override the host options factory before tabs are created; the product
    /// runtime uses the default no-op options.
    /// </summary>
    public static class BrowserIntegrationRuntime
    {
        private static Func<BrowserHostOptions> _browserHostOptionsFactory = () => BrowserHostOptions.Default;

        public static Func<BrowserHostOptions> BrowserHostOptionsFactory
        {
            get => _browserHostOptionsFactory;
            set => _browserHostOptionsFactory = value ?? (() => BrowserHostOptions.Default);
        }

        public static BrowserHostOptions CreateBrowserHostOptions()
        {
            return _browserHostOptionsFactory?.Invoke() ?? BrowserHostOptions.Default;
        }

        public static void Reset()
        {
            _browserHostOptionsFactory = () => BrowserHostOptions.Default;
        }
    }
}
