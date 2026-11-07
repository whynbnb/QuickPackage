using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public sealed class CachedVersionViewModel
{
    public required string Version { get; init; }
    public required string Size { get; init; }
    public required string Path { get; init; }
    public string CurrentText { get; init; } = string.Empty;
}

/// <summary>Lets the user pick an older cached pacman archive to install.</summary>
public partial class DowngradeViewModel : ObservableObject
{
    public DowngradeViewModel(Package package, IReadOnlyList<PackageManager.CachedPackage> versions)
    {
        PackageName = package.Name;
        foreach (var version in versions)
        {
            Items.Add(new CachedVersionViewModel
            {
                Version = version.Version,
                Size = version.Size,
                Path = version.Path,
                CurrentText = version.Version == package.Version ? Loc.T("current") : string.Empty,
            });
        }
        Selected = Items.FirstOrDefault(item => item.CurrentText.Length == 0) ?? Items.FirstOrDefault();
    }

    public string PackageName { get; }

    public ObservableCollection<CachedVersionViewModel> Items { get; } = [];

    [ObservableProperty]
    private CachedVersionViewModel? _selected;

    public bool CanConfirm => Selected is not null;

    partial void OnSelectedChanged(CachedVersionViewModel? value)
        => OnPropertyChanged(nameof(CanConfirm));

    public string Title => Loc.T("Downgrade package");
    public string Message => Loc.T("Choose a cached version of %1 to install.", PackageName);
    public string VersionHeader => Loc.T("Version");
    public string SizeHeader => Loc.T("Size");
    public string OkText => Loc.T("Downgrade");
    public string CancelText => Loc.T("Cancel");
}
