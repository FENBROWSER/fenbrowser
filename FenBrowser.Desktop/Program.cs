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
        FenBrowser.Core.FenLogger.Initialize(@"C:\Users\udayk\Videos\FENBROWSER\fenbrowser.log");
        FenBrowser.Core.FenLogger.Info("FenBrowser Started", FenBrowser.Core.Logging.LogCategory.General);

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var msg = $"[CRASH] Unhandled Exception: {e.ExceptionObject}";
            Console.WriteLine(msg);
            FenBrowser.Core.FenLogger.Error(msg, FenBrowser.Core.Logging.LogCategory.General, e.ExceptionObject as Exception);
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
