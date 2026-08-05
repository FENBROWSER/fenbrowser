using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.DependencyInjection;

namespace FenBrowser.DependencyInjection;

/// <summary>
/// Extension methods for registering core services.
/// </summary>
public static class CoreServiceRegistration
{
    /// <summary>
    /// Registers all core services with the container.
    /// </summary>
    public static IServiceContainer AddCoreServices(this IServiceContainer container)
    {
        // Register BrowserSettings as singleton
        container.RegisterSingleton<IBrowserSettings>(_ => new BrowserSettingsAdapter(BrowserSettings.Instance));

        // Register NetworkService
        container.RegisterSingleton<INetworkService>(_ => new NetworkService());

        // Register logging
        container.RegisterSingleton<ILogger>(_ => new LoggerAdapter());

        // Register settings accessors
        container.RegisterSingleton<LogSettings>(_ => BrowserSettings.Instance.Logging);
        container.RegisterSingleton<ResilienceSettings>(_ => BrowserSettings.Instance.Resilience);

        return container;
    }
}

/// <summary>
/// Simple logger adapter for ILogger interface.
/// </summary>
internal sealed class LoggerAdapter : ILogger
{
    public void Log(LogLevel level, string message)
    {
        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(LogCategory.General),
            EngineLogCompatibility.FromLegacyLevel(MapLevel(level)),
            message);
    }

    public void Log(LogLevel level, string message, Exception exception)
    {
        EngineLog.Write(
            EngineLogCompatibility.FromLegacyCategory(LogCategory.General),
            EngineLogCompatibility.FromLegacyLevel(MapLevel(level)),
            message,
            fields: exception != null ? new Dictionary<string, object> { ["exception"] = exception.ToString() } : null);
    }

    private static FenBrowser.Core.Logging.LogLevel MapLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => FenBrowser.Core.Logging.LogLevel.Trace,
            LogLevel.Debug => FenBrowser.Core.Logging.LogLevel.Debug,
            LogLevel.Info => FenBrowser.Core.Logging.LogLevel.Info,
            LogLevel.Warn => FenBrowser.Core.Logging.LogLevel.Warn,
            LogLevel.Error => FenBrowser.Core.Logging.LogLevel.Error,
            LogLevel.Fatal => FenBrowser.Core.Logging.LogLevel.Error,
            _ => FenBrowser.Core.Logging.LogLevel.Info
        };
    }
}