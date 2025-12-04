using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FenBrowser.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            int? port = null;
            var args = desktop.Args;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                    {
                        port = p;
                        break;
                    }
                    else if (args[i].StartsWith("--port="))
                    {
                        if (int.TryParse(args[i].Substring(7), out int p2))
                        {
                            port = p2;
                            break;
                        }
                    }
                }
            }
            desktop.MainWindow = new MainWindow(port);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
