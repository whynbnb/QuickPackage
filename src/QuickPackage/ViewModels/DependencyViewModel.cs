using CommunityToolkit.Mvvm.Input;

namespace QuickPackage.ViewModels;

/// <summary>A dependency chip in the detail pane that navigates to its package.</summary>
public sealed class DependencyViewModel
{
    public required string Name { get; init; }

    public required IRelayCommand NavigateCommand { get; init; }

    /// <summary>False for virtual provides that are not installed under this name.</summary>
    public bool IsAvailable { get; init; } = true;
}
