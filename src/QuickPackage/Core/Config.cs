namespace QuickPackage.Core;

/// <summary>
/// INI store ("quickpackagerc") in the established format, so user settings
/// from earlier releases carry over.
/// </summary>
public sealed class Config
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.Ordinal);

    public static string FilePath
    {
        get
        {
            var baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "quickpackagerc");
        }
    }

    public static Config Load()
    {
        var config = new Config();
        try
        {
            if (!File.Exists(FilePath))
                return config;
            Dictionary<string, string>? section = null;
            foreach (var rawLine in File.ReadAllLines(FilePath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    var name = line[1..^1];
                    section = config.GetSection(name);
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq <= 0 || section is null)
                    continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                // String values may be quoted in the file format.
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                section[key] = value;
            }
        }
        catch
        {
            // A broken config must never prevent the app from starting.
        }
        return config;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var lines = new List<string>();
        foreach (var (name, section) in _sections)
        {
            lines.Add($"[{name}]");
            foreach (var (key, value) in section)
                lines.Add($"{key}={value}");
            lines.Add(string.Empty);
        }
        File.WriteAllLines(FilePath, lines);
    }

    private Dictionary<string, string> GetSection(string name)
    {
        if (!_sections.TryGetValue(name, out var section))
        {
            section = new Dictionary<string, string>(StringComparer.Ordinal);
            _sections[name] = section;
        }
        return section;
    }

    public string GetString(string section, string key, string fallback = "")
        => _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string section, string key, int fallback)
        => int.TryParse(GetString(section, key, fallback.ToString()), out var value) ? value : fallback;

    public bool GetBool(string section, string key, bool fallback)
        => bool.TryParse(GetString(section, key, fallback.ToString()), out var value) ? value : fallback;

    public void Set(string section, string key, string value) => GetSection(section)[key] = value;

    public void Set(string section, string key, int value) => Set(section, key, value.ToString());

    public void Set(string section, string key, bool value) => Set(section, key, value ? "true" : "false");
}
