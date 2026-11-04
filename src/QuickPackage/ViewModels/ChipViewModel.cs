using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public enum ChipKind
{
    // Pseudo filters above the backend ids, mirroring the former proxy ids.
    All = 100,

    Pacman = Backend.Pacman,
    Aur = Backend.Aur,
    Flatpak = Backend.Flatpak,
    Cargo = Backend.Cargo,
    Mise = Backend.Mise,
    Rustup = Backend.Rustup,
    Fvm = Backend.Fvm,
}

public partial class ChipViewModel : ObservableObject
{
    public ChipKind Kind { get; }

    public ChipViewModel(ChipKind kind, string label)
    {
        Kind = kind;
        _label = label;
    }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isEnabled = true;
}
