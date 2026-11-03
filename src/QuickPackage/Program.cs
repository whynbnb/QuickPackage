using Avalonia;
using QuickPackage.Core;

namespace QuickPackage;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The language is resolved before any UI string is produced.
        var config = Config.Load();
        var language = config.GetString("General", "Language", "system");
        Loc.SetLanguage(language == "system" ? Loc.DetectSystemLanguage() : language);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
