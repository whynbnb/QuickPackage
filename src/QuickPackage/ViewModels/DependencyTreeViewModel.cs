using System.Collections.ObjectModel;
using Avalonia.Media;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public sealed class DependencyNodeViewModel
{
    public required string Name { get; init; }
    public string VersionText { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public IBrush? Foreground { get; init; }
    public ObservableCollection<DependencyNodeViewModel> Children { get; } = [];
}

/// <summary>A recursive forward-dependency tree rooted at one package.</summary>
public sealed class DependencyTreeViewModel
{
    public DependencyTreeViewModel(
        string packageName,
        IReadOnlyList<DependencyNodeViewModel> roots,
        int totalNodes)
    {
        PackageName = packageName;
        Roots = new ObservableCollection<DependencyNodeViewModel>(roots);
        SummaryText = Loc.T("%1 dependencies", totalNodes);
    }

    public string PackageName { get; }

    public ObservableCollection<DependencyNodeViewModel> Roots { get; }

    public string Title => Loc.T("Dependency tree");
    public string Subtitle => Loc.T("Forward dependencies of %1", PackageName);
    public string SummaryText { get; }
    public string EmptyText => Loc.T("No dependencies to show");
    public bool HasRoots => Roots.Count > 0;
    public string CloseText => Loc.T("Close");
}
