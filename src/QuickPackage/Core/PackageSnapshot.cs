using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPackage.Core;

/// <summary>A point-in-time export of the enabled package backends.</summary>
public sealed class PackageSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public List<SnapshotEntry> Packages { get; set; } = [];
}

public sealed class SnapshotEntry
{
    public Backend Backend { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

/// <summary>Persists snapshots next to the main configuration file.</summary>
public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath
    {
        get
        {
            var directory = Path.GetDirectoryName(Config.FilePath) ?? ".";
            return Path.Combine(directory, "quickpackage-snapshots.json");
        }
    }

    public static List<PackageSnapshot> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<PackageSnapshot>>(json, Options) ?? [];
        }
        catch
        {
            // A corrupt snapshot file must never block the UI.
            return [];
        }
    }

    public static void Save(IEnumerable<PackageSnapshot> snapshots)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(snapshots.ToList(), Options);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, overwrite: true);
    }
}
