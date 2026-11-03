using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using QuickPackage.Core;
using QuickPackage.Views;

namespace QuickPackage;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyTheme(Config.Load().GetString("General", "Theme", "system"));
            // The Fluent theme points ContentControlThemeFontFamily at a bundled
            // Latin-only font that lacks CJK glyphs and falls back to the
            // platform default (a monospace face on some Linux setups).
            Resources["ContentControlThemeFontFamily"] = FontStrategy.PrimaryFamily;
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies "system", "light" or "dark" immediately.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Application.Current is null)
            return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
