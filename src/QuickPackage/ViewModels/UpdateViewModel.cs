using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public partial class UpdateItemViewModel : ObservableObject
{
    public UpdateItemViewModel(Package package)
    {
        Package = package;
        BackendLabel = package.Foreign ? Loc.T("AUR") : BackendInfo.DisplayName(package.Backend);
    }

    public Package Package { get; }

    public string Name => Package.Name;

    public string BackendLabel { get; }

    public string CurrentVersion => Package.Version;

    public string NewVersion => Package.NewVersion;

    [ObservableProperty]
    private bool _isChecked = true;
}

public sealed class UpdateViewModel
{
    public UpdateViewModel(IEnumerable<Package> updates)
    {
        Items = new ObservableCollection<UpdateItemViewModel>(
            updates.Select(package => new UpdateItemViewModel(package)));
    }

    public ObservableCollection<UpdateItemViewModel> Items { get; }

    public int Count => Items.Count;

    public string Title => Loc.Tn("1 Update Available", "%1 Updates Available", Count, Count);

    public string HeaderPackage => Loc.T("Package");

    public string HeaderBackend => Loc.T("Backend");

    public string HeaderCurrent => Loc.T("Current");

    public string HeaderNew => Loc.T("New");

    public string UpdateText => Loc.T("Update");

    public string CancelText => Loc.T("Cancel");

    public List<Package> CheckedPackages
        => Items.Where(item => item.IsChecked).Select(item => item.Package).ToList();
}
