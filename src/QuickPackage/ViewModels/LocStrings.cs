using QuickPackage.Core;

namespace QuickPackage.ViewModels;

/// <summary>Static UI captions bound from XAML (language is fixed per session).</summary>
public sealed class LocStrings
{
    public string File => Loc.T("File");
    public string Packages => Loc.T("Packages");
    public string Settings => Loc.T("Settings");
    public string Help => Loc.T("Help");
    public string Quit => Loc.T("Quit");
    public string About => Loc.T("About");

    public string RefreshLists => Loc.T("Refresh Lists");
    public string CheckForUpdates => Loc.T("Check for Updates");
    public string InstallSelected => Loc.T("Install Selected");
    public string RemoveSelected => Loc.T("Remove Selected");
    public string Preferences => Loc.T("Preferences…");
    public string ConfigureProxy => Loc.T("Configure Proxy…");

    public string SystemPackages => Loc.T("System packages");
    public string DeveloperPackages => Loc.T("Developer packages");
    public string SearchPackages => Loc.T("Search packages…");
    public string Refresh => Loc.T("Refresh");
    public string Updates => Loc.T("Updates");
    public string Install => Loc.T("Install");
    public string Remove => Loc.T("Remove");

    public string SelectPackageHint => Loc.T("Select a package to see details");
    public string UpdateDetail => Loc.T("Update");
    public string UninstallDetail => Loc.T("Uninstall");
    public string BackendsDisabledHint => Loc.T("All package backends are disabled. Enable at least one in Settings.");
    public string NoMatchingPackages => Loc.T("No matching packages");
    public string ShowOnlyUpdates => Loc.T("Show only packages with updates");
}
