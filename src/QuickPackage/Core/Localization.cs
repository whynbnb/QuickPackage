using System.Runtime.InteropServices;

namespace QuickPackage.Core;

/// <summary>
/// Minimal runtime localization. English message ids are the keys; the Chinese
/// catalog keeps the established translations. %1..%9 placeholders are
/// substituted in order.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Chinese = "zh_CN";

    private static string _language = DetectSystemLanguage();

    public static string Language => _language;

    public static bool IsChinese => _language == Chinese;

    public static string DetectSystemLanguage()
    {
        var value = Environment.GetEnvironmentVariable("LC_ALL")
            ?? Environment.GetEnvironmentVariable("LC_MESSAGES")
            ?? Environment.GetEnvironmentVariable("LANG")
            ?? string.Empty;
        if (value.Contains("zh", StringComparison.OrdinalIgnoreCase))
            return Chinese;
        return English;
    }

    public static void SetLanguage(string language)
    {
        _language = string.IsNullOrEmpty(language) ? DetectSystemLanguage() : language;
    }

    public static string T(string key)
        => IsChinese && TranslationsZh.Catalog.TryGetValue(key, out var value) ? value : key;

    public static string T(string key, params object?[] args) => Format(T(key), args);

    /// <summary>Plural selector: English picks the form by count, Chinese has one form.</summary>
    public static string Tn(string singular, string plural, int count, params object?[] args)
    {
        var pattern = count == 1 ? T(singular) : T(plural);
        return Format(pattern, args);
    }

    private static string Format(string pattern, object?[] args)
    {
        for (var i = args.Length; i >= 1; --i)
            pattern = pattern.Replace("%" + i.ToString(), args[i - 1]?.ToString() ?? string.Empty);
        return pattern;
    }

    /// <summary>Opens a URL with the desktop handler.</summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", url);
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch
        {
            // Opening URLs is best-effort.
        }
    }
}
