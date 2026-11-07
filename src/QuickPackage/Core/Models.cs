namespace QuickPackage.Core;

/// <summary>Package sources, in display order.</summary>
public enum Backend
{
    Pacman = 0,
    Flatpak = 1,
    Cargo = 2,
    Mise = 3,
    Rustup = 4,
    Fvm = 5,
    Aur = 6,
}

public enum PackageStatus
{
    Idle = 0,
    Installed = 1,
    Updating = 2,
    Removing = 3,
    Failed = 4,
    NeedsUpdate = 5,
}

public enum PackageOperation
{
    Install,
    Remove,
    Update,
}

/// <summary>A single package / tool instance. Mutable: the UI updates status in place.</summary>
public sealed class Package
{
    public string Name { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string DownloadSize { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string BuildDate { get; set; } = string.Empty;
    public string InstallReason { get; set; } = string.Empty;
    public string Packager { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Licenses { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Groups { get; set; } = string.Empty;
    public string Provides { get; set; } = string.Empty;
    public string OptionalDeps { get; set; } = string.Empty;
    public string Conflicts { get; set; } = string.Empty;
    public string Depends { get; set; } = string.Empty;
    public string RequiredBy { get; set; } = string.Empty;
    public string Binaries { get; set; } = string.Empty;
    public Backend Backend { get; set; } = Backend.Pacman;
    public PackageStatus Status { get; set; } = PackageStatus.Idle;
    public bool Installed { get; set; }
    public bool Selected { get; set; }

    /// <summary>Foreign (AUR/local) pacman package: installed but absent from the sync repos.</summary>
    public bool Foreign { get; set; }

    /// <summary>Pacman install reason: true for an explicitly installed package.</summary>
    public bool Explicit { get; set; }

    /// <summary>Installed as a dependency but no longer required by anything.</summary>
    public bool Orphan { get; set; }

    public string EffectiveId => string.IsNullOrEmpty(PackageId) ? Name : PackageId;
}

public static class BackendInfo
{
    public static bool IsDev(Backend backend)
        => backend is Backend.Mise or Backend.Rustup or Backend.Fvm;

    /// <summary>Human-readable backend label.</summary>
    public static string DisplayName(Backend backend) => backend switch
    {
        Backend.Pacman => Loc.T("Pacman"),
        Backend.Aur => Loc.T("AUR"),
        Backend.Flatpak => Loc.T("Flatpak"),
        Backend.Cargo => Loc.T("Cargo"),
        Backend.Mise => Loc.T("Mise"),
        Backend.Rustup => Loc.T("Rustup"),
        Backend.Fvm => Loc.T("FVM"),
        _ => string.Empty,
    };

    public static string StatusText(PackageStatus status) => status switch
    {
        PackageStatus.Installed => Loc.T("Installed"),
        PackageStatus.Updating => Loc.T("Updating…"),
        PackageStatus.Removing => Loc.T("Removing…"),
        PackageStatus.Failed => Loc.T("Failed"),
        PackageStatus.NeedsUpdate => Loc.T("Update available"),
        _ => string.Empty,
    };
}
