using Avalonia;
using System;
using FenBrowser.UI;

namespace FenBrowser.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var msg = $"[CRASH] Unhandled Exception: {e.ExceptionObject}";
            Console.WriteLine(msg);
            try { System.IO.File.AppendAllText("crash_log_new.txt", msg + Environment.NewLine); } catch { }
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
