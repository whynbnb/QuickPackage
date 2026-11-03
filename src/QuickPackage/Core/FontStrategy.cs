using System.Globalization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace QuickPackage.Core;

/// <summary>
/// Cross-platform font fallback strategy.
///
/// The primary UI family stays the platform default (so Latin text matches the
/// desktop), while the CJK ranges are pinned to an ordered list of families.
/// The list differs per platform and per UI language, because each platform
/// ships different CJK fonts and the fallback order decides which regional
/// glyph variants (Simplified Chinese vs. Japanese, for example) are used.
/// Families that are not installed are skipped by the font manager.
/// </summary>
public static class FontStrategy
{
    private static readonly UnicodeRange[] CjkRanges =
    [
        new(0x2E80, 0x2FFF),   // CJK radicals, Kangxi radicals
        new(0x3000, 0x303F),   // CJK symbols and punctuation
        new(0x3040, 0x30FF),   // Hiragana and Katakana
        new(0x3100, 0x312F),   // Bopomofo
        new(0x3400, 0x4DBF),   // CJK Unified Ideographs Extension A
        new(0x4E00, 0x9FFF),   // CJK Unified Ideographs
        new(0xAC00, 0xD7AF),   // Hangul syllables
        new(0xF900, 0xFAFF),   // CJK Compatibility Ideographs
        new(0xFF00, 0xFFEF),   // Halfwidth and Fullwidth Forms
        new(0x20000, 0x2FA1F), // CJK Unified Ideographs Extension B..F
    ];

    private static readonly string PrimaryFamilyNameValue = ResolvePrimaryFamilyName();

    /// <summary>
    /// Concrete system UI family for the current platform. Avalonia's own
    /// default can resolve to a monospace face on Linux, so the family is
    /// resolved through Skia/fontconfig before the font manager starts.
    /// </summary>
    public static string PrimaryFamilyName => PrimaryFamilyNameValue;


    public static FontFamily PrimaryFamily => new(PrimaryFamilyName);

    /// <summary>Font manager options with ordered CJK fallbacks for this platform.</summary>
    public static FontManagerOptions CreateOptions()
    {
        var families = OrderedFamilies();
        var fallbacks = new List<FontFallback>(families.Length * CjkRanges.Length);
        foreach (var range in CjkRanges)
        {
            // Within one range the family order is preserved, so the preferred
            // regional face is always tried first.
            foreach (var family in families)
            {
                fallbacks.Add(new FontFallback
                {
                    FontFamily = new FontFamily(family),
                    UnicodeRange = range,
                });
            }
        }
        return new FontManagerOptions
        {
            // Never rely on the platform default blindly: on some Linux setups
            // it resolves to a monospace face. "sans-serif" lets fontconfig
            // pick the desktop's configured UI sans (Noto Sans CJK SC here).
            DefaultFamilyName = PrimaryFamilyName,
            FontFallbacks = fallbacks,
        };
    }

    private static string ResolvePrimaryFamilyName()
    {
        var manager = SKFontManager.Default;

        // Prefer the font the desktop is actually configured with, so Latin,
        // digits and CJK all come from the same family (KDE keeps this in
        // kdeglobals, GNOME exposes it through gsettings).
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            var desktopFamily = DesktopUiFontFamily();
            if (!string.IsNullOrEmpty(desktopFamily))
            {
                using var desktopTypeface = manager.MatchFamily(desktopFamily);
                if (desktopTypeface is not null)
                    return desktopTypeface.FamilyName;
            }
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? ["Segoe UI", "Tahoma", "Arial"]
            : OperatingSystem.IsMacOS()
                ? ["Helvetica Neue", "SF Pro Text", "Arial"]
                : ["sans-serif", "Noto Sans CJK SC", "Noto Sans", "Adwaita Sans",
                   "Cantarell", "DejaVu Sans", "Liberation Sans", "FreeSans"];

        foreach (var candidate in candidates)
        {
            using var typeface = manager.MatchFamily(candidate);
            if (typeface is not null)
                return typeface.FamilyName;
        }
        return "Arial";
    }

    /// <summary>UI font family configured by the desktop environment, if any.</summary>
    private static string? DesktopUiFontFamily()
    {
        if (ReadKdeGeneral().TryGetValue("font", out var kdeFont))
        {
            var comma = kdeFont.IndexOf(',');
            return (comma > 0 ? kdeFont[..comma] : kdeFont).Trim();
        }

        // GNOME: 'Noto Sans CJK SC  10' -> Noto Sans CJK SC
        var gnomeFont = ReadGSettings("font-name");
        if (!string.IsNullOrEmpty(gnomeFont))
        {
            var match = System.Text.RegularExpressions.Regex.Match(gnomeFont, @"^(.*?)\s+\d+$");
            return (match.Success ? match.Groups[1].Value : gnomeFont).Trim();
        }
        return null;
    }

    /// <summary>
    /// Text antialiasing mode configured by the desktop. KDE keeps the Xft
    /// settings in kdeglobals; GNOME exposes them through gsettings. Native
    /// applications honour these, so matching them avoids the different
    /// colour fringing that makes Avalonia text look "off".
    /// </summary>
    public static TextRenderingMode DesktopTextRenderingMode { get; } = ResolveTextRenderingMode();

    /// <summary>Glyph hinting configured by the desktop.</summary>
    public static TextHintingMode DesktopTextHintingMode { get; } = ResolveTextHintingMode();

    /// <summary>Human-readable summary of the resolved antialiasing/hinting.</summary>
    public static string DescribeTextOptions()
    {
        var rendering = DesktopTextRenderingMode switch
        {
            TextRenderingMode.Antialias => "grayscale",
            TextRenderingMode.SubpixelAntialias => "subpixel",
            TextRenderingMode.Alias => "aliased",
            _ => "platform default",
        };
        var hinting = DesktopTextHintingMode switch
        {
            TextHintingMode.None => "none",
            TextHintingMode.Light => "light",
            TextHintingMode.Strong => "strong",
            _ => "default",
        };
        return $"{rendering}, hinting {hinting}";
    }

    public static void ApplyTextOptions(Visual visual)
    {
        if (DesktopTextRenderingMode != TextRenderingMode.Unspecified)
            TextOptions.SetTextRenderingMode(visual, DesktopTextRenderingMode);
        if (DesktopTextHintingMode != TextHintingMode.Unspecified)
            TextOptions.SetTextHintingMode(visual, DesktopTextHintingMode);
    }

    private static TextRenderingMode ResolveTextRenderingMode()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return TextRenderingMode.Unspecified;

        var general = ReadKdeGeneral();
        if (general.TryGetValue("XftAntialias", out var antialias)
            && antialias.Equals("false", StringComparison.OrdinalIgnoreCase))
            return TextRenderingMode.Alias;
        if (general.TryGetValue("XftSubPixel", out var subpixel))
        {
            return subpixel.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? TextRenderingMode.Antialias
                : TextRenderingMode.SubpixelAntialias;
        }

        var gnome = ReadGSettings("font-antialiasing");
        return gnome switch
        {
            "none" => TextRenderingMode.Alias,
            "grayscale" => TextRenderingMode.Antialias,
            "rgba" => TextRenderingMode.SubpixelAntialias,
            _ => TextRenderingMode.Unspecified,
        };
    }

    private static TextHintingMode ResolveTextHintingMode()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return TextHintingMode.Unspecified;

        var general = ReadKdeGeneral();
        if (general.TryGetValue("XftHintStyle", out var hintStyle))
        {
            return hintStyle.ToLowerInvariant() switch
            {
                "hintnone" => TextHintingMode.None,
                "hintslight" => TextHintingMode.Light,
                "hintmedium" or "hintfull" => TextHintingMode.Strong,
                _ => TextHintingMode.Unspecified,
            };
        }

        return ReadGSettings("font-hinting") switch
        {
            "none" => TextHintingMode.None,
            "slight" => TextHintingMode.Light,
            "medium" or "full" => TextHintingMode.Strong,
            _ => TextHintingMode.Unspecified,
        };
    }

    private static Dictionary<string, string> ReadKdeGeneral()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(ConfigHome(), "kdeglobals");
        if (!File.Exists(path))
            return values;
        try
        {
            var inGeneral = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    inGeneral = line.Equals("[General]", StringComparison.Ordinal);
                    continue;
                }
                if (!inGeneral)
                    continue;
                var eq = line.IndexOf('=');
                if (eq > 0)
                    values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch
        {
            // Ignore unreadable configuration.
        }
        return values;
    }

    private static string? ReadGSettings(string key)
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        if (!desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("gsettings")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("get");
            startInfo.ArgumentList.Add("org.gnome.desktop.interface");
            startInfo.ArgumentList.Add(key);
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd().Trim().Trim('\'', '"');
            process.WaitForExit(1500);
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static string ConfigHome()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return configHome;
    }

    private static string[] OrderedFamilies()
    {
        // The UI language decides which regional variants are preferred; the
        // other CJK faces follow as a last resort.
        var script = PreferredScript();
        var preferred = ScriptFamilies(script);
        var others = new[] { "SC", "TC", "JP", "KR" }
            .Where(other => other != script)
            .SelectMany(ScriptFamilies);
        return preferred.Concat(others)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string PreferredScript()
    {
        var culture = CultureInfo.CurrentUICulture;
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase);
            return traditional ? "TC" : "SC";
        }
        return culture.TwoLetterISOLanguageName switch
        {
            "ja" => "JP",
            "ko" => "KR",
            // Latin UI with CJK content: Simplified Chinese is the safest default.
            _ => "SC",
        };
    }

    private static string[] ScriptFamilies(string script)
    {
        if (OperatingSystem.IsWindows())
        {
            return script switch
            {
                "TC" => ["Microsoft JhengHei UI", "Microsoft JhengHei", "MingLiU"],
                "JP" => ["Yu Gothic UI", "Meiryo UI", "MS Gothic"],
                "KR" => ["Malgun Gothic", "Gulim"],
                _ => ["Microsoft YaHei UI", "Microsoft YaHei", "DengXian", "SimSun"],
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return script switch
            {
                "TC" => ["PingFang TC", "Heiti TC", "STHeiti"],
                "JP" => ["Hiragino Sans", "Hiragino Kaku Gothic ProN", "YuGothic"],
                "KR" => ["Apple SD Gothic Neo", "AppleGothic"],
                _ => ["PingFang SC", "Hiragino Sans GB", "Heiti SC", "STHeiti"],
            };
        }

        // Linux and other Unix systems: Noto CJK is the common baseline,
        // followed by Source Han and the distributions' older packages.
        return script switch
        {
            "TC" => ["Noto Sans CJK TC", "Noto Sans TC", "Source Han Sans TC"],
            "JP" => ["Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP"],
            "KR" => ["Noto Sans CJK KR", "Noto Sans KR", "Source Han Sans KR"],
            _ =>
            [
                "Noto Sans CJK SC",
                "Noto Sans SC",
                "Source Han Sans SC",
                "Source Han Sans CN",
                "WenQuanYi Micro Hei",
                "WenQuanYi Zen Hei",
                "Droid Sans Fallback",
            ],
        };
    }
}
