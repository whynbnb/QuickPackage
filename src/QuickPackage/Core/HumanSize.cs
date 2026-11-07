using System.Globalization;

namespace QuickPackage.Core;

/// <summary>Converts between pacman's human-readable sizes and bytes.</summary>
public static class HumanSize
{
    public static long Parse(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return 0;
        var space = text.IndexOf(' ');
        if (space <= 0 || !double.TryParse(
                text[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return 0;
        var unit = text[(space + 1)..].Trim().ToLowerInvariant();
        var factor = unit switch
        {
            "b" => 1L,
            "kb" => 1024L,
            "mb" => 1024L * 1024,
            "gb" => 1024L * 1024 * 1024,
            "tb" => 1024L * 1024 * 1024 * 1024,
            "kib" => 1024L,
            "mib" => 1024L * 1024,
            "gib" => 1024L * 1024 * 1024,
            "tib" => 1024L * 1024 * 1024 * 1024,
            _ => 0L,
        };
        return factor == 0 ? 0 : (long)(number * factor);
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            ++unit;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
