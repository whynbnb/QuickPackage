using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPackage.Core;

/// <summary>A point-in-time export of the enabled package backends.</summary>
public sealed class PackageSnapshot
{
    public const int CurrentSchema = 3;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Bumped when the entry shape changes so old files can migrate.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;

    public List<SnapshotEntry> Packages { get; set; } = [];
}

public sealed class SnapshotEntry
{
    public Backend Backend { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    /// True/false for backends with an install reason (pacman/AUR); null for
    /// backends that do not have the concept at all.
    /// </summary>
    public bool? Explicit { get; set; }

    public bool Orphan { get; set; }
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
            return Deserialize(File.ReadAllText(FilePath));
        }
        catch
        {
            // A corrupt snapshot file must never block the UI.
            return [];
        }
    }

    public static List<PackageSnapshot> Deserialize(string json)
    {
        var snapshots = JsonSerializer.Deserialize<List<PackageSnapshot>>(json, Options) ?? [];
        foreach (var snapshot in snapshots)
        {
            if (snapshot.SchemaVersion >= PackageSnapshot.CurrentSchema)
                continue;
            foreach (var entry in snapshot.Packages)
            {
                if (entry.Backend is Backend.Pacman or Backend.Aur)
                {
                    // Version 1 stored only the explicit lists.
                    if (snapshot.SchemaVersion < 2)
                        entry.Explicit = true;
                }
                else
                {
                    // Other backends have no install-reason concept.
                    entry.Explicit = null;
                }
            }
            snapshot.SchemaVersion = PackageSnapshot.CurrentSchema;
        }
        return snapshots;
    }

    public static string Serialize(IEnumerable<PackageSnapshot> snapshots)
        => JsonSerializer.Serialize(snapshots.ToList(), Options);

    public static void Save(IEnumerable<PackageSnapshot> snapshots)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = Serialize(snapshots);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, overwrite: true);
    }
}
