using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FenBrowser.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        // Initialize logging
        var settings = FenBrowser.Core.BrowserSettings.Instance;
        FenBrowser.Core.Logging.LogManager.Initialize(
            settings.Logging.EnableLogging, 
            (FenBrowser.Core.Logging.LogCategory)settings.Logging.EnabledCategories, 
            (FenBrowser.Core.Logging.LogLevel)settings.Logging.MinimumLevel);
            
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            int? port = null;
            string initialUrl = null;
            var args = desktop.Args;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                    {
                        port = p;
                        i++; // Skip next arg
                    }
                    else if (args[i].StartsWith("--port="))
                    {
                        if (int.TryParse(args[i].Substring(7), out int p2))
                        {
                            port = p2;
                        }
                    }
                    else if (!args[i].StartsWith("--"))
                    {
                        // Assume it's a URL if it doesn't start with --
                        initialUrl = args[i];
                    }
                }
            }
            desktop.MainWindow = new MainWindow(port, initialUrl);
            try
            {
                ThemeManager.Initialize(); // Initialize theme preference
            }
            catch (Exception ex)
            {
               System.Diagnostics.Debug.WriteLine($"[App] Theme Init Failed: {ex}");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
