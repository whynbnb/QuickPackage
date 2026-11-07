using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

/// <summary>UI captions bound from XAML; refreshed on language changes.</summary>
public sealed class LocStrings : ObservableObject
{
    /// <summary>Raises change notifications for every caption after Loc.SetLanguage.</summary>
    public void NotifyAll()
    {
        foreach (var property in typeof(LocStrings).GetProperties())
            OnPropertyChanged(property.Name);
    }

    public string File => Loc.T("File");
    public string Packages => Loc.T("Packages");
    public string Settings => Loc.T("Settings");
    public string Help => Loc.T("Help");
    public string Quit => Loc.T("Quit");
    public string About => Loc.T("About");

    public string RefreshLists => Loc.T("Refresh Lists");
    public string SyncDatabases => Loc.T("Sync databases");
    public string CheckForUpdates => Loc.T("Check for Updates");
    public string InstallSelected => Loc.T("Install Selected");
    public string RemoveSelected => Loc.T("Remove Selected");
    public string Select => Loc.T("Select");
    public string SelectAll => Loc.T("Select all");
    public string ClearSelection => Loc.T("Clear selection");
    public string ClearFilters => Loc.T("Clear filters");
    public string ClearFiltersHint => Loc.T("Reset search and all active filters");
    public string SelectPackagesHint => Loc.T("Show checkboxes and batch install or uninstall the selected packages");
    public string Preferences => Loc.T("Preferences…");

    public string SystemPackages => Loc.T("System packages");
    public string DeveloperPackages => Loc.T("Developer packages");
    public string SearchPackages => Loc.T("Search packages…");
    public string SearchAur => Loc.T("Search AUR");
    public string SearchAurHint => Loc.T("Search the AUR by name and description");
    public string SearchOnline => Loc.T("Online search");
    public string SearchOnlineHint => Loc.T("Search an online package catalogue for new packages");
    public string SearchFlatpak => Loc.T("Search Flatpak");
    public string SearchCrates => Loc.T("Search crates.io");
    public string ClearSearch => Loc.T("Clear search");
    public string SearchHistory => Loc.T("Search history");
    public string Refresh => Loc.T("Refresh");
    public string Snapshots => Loc.T("Snapshots");
    public string CacheCleanup => Loc.T("Cache cleanup");
    public string History => Loc.T("Package history");
    public string Updates => Loc.T("Updates");
    public string Install => Loc.T("Install");
    public string Remove => Loc.T("Remove");

    public string SelectPackageHint => Loc.T("Select a package to see details");
    public string UpdateDetail => Loc.T("Update");
    public string UninstallDetail => Loc.T("Uninstall");
    public string MakeExplicit => Loc.T("Make Explicit");
    public string More => Loc.T("More");
    public string Files => Loc.T("Files");
    public string DownloadOnly => Loc.T("Download only");
    public string Reinstall => Loc.T("Reinstall");
    public string Downgrade => Loc.T("Downgrade…");
    public string InstallLocalPackage => Loc.T("Install local package…");
    public string DependencyTree => Loc.T("Dependency tree");
    public string Diagnostics => Loc.T("Diagnose backends");
    public string News => Loc.T("Arch news and mirrors");
    public string BackendsDisabledHint => Loc.T("All package backends are disabled. Enable at least one in Settings.");
    public string NoMatchingPackages => Loc.T("No matching packages");
    public string ShowOnlyUpdates => Loc.T("Show only packages with updates");
    public string ShowOnlyOrphans => Loc.T("Show only orphan packages");
    public string ClearGroupFilter => Loc.T("Clear group filter");
    public string InstalledOnly => Loc.T("Installed");
    public string NotInstalledOnly => Loc.T("Not installed");
    public string AllPackages => Loc.T("All packages");
    public string OriginFilterHint => Loc.T("Filter by package origin");
    public string SortByHint => Loc.T("Sort the package list");
    public string SortDirectionHint => Loc.T("Toggle the sort direction");
    public string ReasonFilterHint => Loc.T("Filter installed pacman/AUR packages by install reason");
    public string Back => Loc.T("Back");
    public string Forward => Loc.T("Forward");
    public string DependsOnDeps => Loc.T("Depends on");
    public string RequiredByDeps => Loc.T("Required by");

    public string ActivityLog => Loc.T("Activity log");
    public string Stop => Loc.T("Stop");
    public string LogDetails => Loc.T("Details");
    public string CopyLog => Loc.T("Copy");
    public string ClearLog => Loc.T("Clear");
}
