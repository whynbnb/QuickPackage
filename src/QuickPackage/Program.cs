using System.Globalization;
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

        // Give text shaping an explicit language so CJK glyph variants are
        // selected deterministically (Simplified Chinese uses the SC forms).
        var culture = CultureInfo.GetCultureInfo(Loc.IsChinese ? "zh-CN" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            // Keep the Wayland app id in sync with the installed desktop entry
            // so compositors can associate the window with its icon/metadata.
            .With(new WaylandPlatformOptions { AppId = "org.quickpackage.QuickPackage" })
            .UsePlatformDetect()
            // Prefer the native Wayland backend when a compositor is reachable
            // and fall back to the X11 backend otherwise (including XWayland).
            .UseWaylandWithFallback()
            .With(FontStrategy.CreateOptions())
            .LogToTrace();
}
