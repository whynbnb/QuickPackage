using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

/// <summary>A single package card. Wraps a <see cref="Package"/> and exposes display data.</summary>
public partial class PackageItemViewModel : ObservableObject
{
    private static readonly Dictionary<(uint, byte), IBrush> BrushCache = [];
    private static readonly IRelayCommand NoopCommand = new RelayCommand(() => { });

    private IReadOnlyList<string>? _groups;
    private IReadOnlyList<DependencyViewModel>? _groupChips;
    private readonly IRelayCommand<string>? _filterByGroup;

    public Package Model { get; }

    public ICommand? InstallCommand { get; init; }
    public ICommand? UninstallCommand { get; init; }
    public ICommand? UpdateCommand { get; init; }
    public ICommand? FilesCommand { get; init; }
    public ICommand? MakeExplicitCommand { get; init; }
    public ICommand? ToggleIgnoreCommand { get; init; }
    public ICommand? CopyNameCommand { get; init; }
    public ICommand? OpenAurCommand { get; init; }
    public ICommand? DownloadCommand { get; init; }
    public ICommand? ReinstallCommand { get; init; }
    public ICommand? DowngradeCommand { get; init; }

    public string InstallText => Loc.T("Install");
    public string UninstallText => Loc.T("Uninstall");
    public string UpdateText => Loc.T("Update");
    public string FilesText => Loc.T("Files");
    public string MakeExplicitText => Loc.T("Make Explicit");
    public string IgnoreText => Model.Ignored ? Loc.T("Cancel ignored updates") : Loc.T("Ignore updates");
    public string CopyNameText => Loc.T("Copy package name");
    public string OpenAurText => Loc.T("Open AUR page");
    public string DownloadText => Loc.T("Download only");
    public string ReinstallText => Loc.T("Reinstall");
    public string DowngradeText => Loc.T("Downgrade…");

    public bool ShowInstallAction => !Model.Installed;
    public bool ShowInstalledActions => Model.Installed;
    public bool ShowAurAction => Model.Backend == Backend.Aur;
    public bool ShowFilesAction => Model.Installed
        && Model.Backend is Backend.Pacman or Backend.Aur;
    public bool ShowMakeExplicitAction => Model.Installed
        && Model.Backend is Backend.Pacman or Backend.Aur
        && !Model.Explicit;
    public bool ShowDownloadAction => Model.Backend is Backend.Pacman or Backend.Aur;
    public bool ShowReinstallAction => Model.Installed;
    public bool ShowDowngradeAction => Model.Installed
        && Model.Backend is Backend.Pacman or Backend.Aur;

    public PackageItemViewModel(Package model, IRelayCommand<string>? filterByGroup = null)
    {
        Model = model;
        _filterByGroup = filterByGroup;
        _isSelected = model.Selected;
    }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Checkboxes only appear while the list is filtered to updates.</summary>
    [ObservableProperty]
    private bool _showSelection;

    partial void OnIsSelectedChanged(bool value) => Model.Selected = value;

    public string Name => Model.Name;

    public string VersionText => Model.NewVersion.Length == 0
        ? Model.Version
        : $"{(Model.Version.Length == 0 ? "—" : Model.Version)} → {Model.NewVersion}";

    public bool HasOrigin => Model.Origin.Length > 0;

    public string OriginText => Model.Origin;

    public IBrush OriginBackground
    {
        get
        {
            var (r, g, b) = OriginColor;
            return Brush(r, g, b, 40);
        }
    }

    public IBrush OriginBorderBrush
    {
        get
        {
            var (r, g, b) = OriginColor;
            return Brush(r, g, b, 115);
        }
    }

    public IBrush OriginForeground
    {
        get
        {
            var (r, g, b) = OriginColor;
            return Brush(r, g, b, 255);
        }
    }

    /// <summary>Only installed pacman/AUR packages carry an install reason.</summary>
    public bool ShowInstallReason => Model.Installed
        && Model.Backend is Backend.Pacman or Backend.Aur;

    public string InstallReasonText => Model.Explicit
        ? Loc.T("Explicit")
        : Loc.T("Dependency");

    public bool ShowOrphan => Model.Orphan;

    public string OrphanText => Loc.T("Orphan");

    /// <summary>Pacman package groups, shown as pills in the list and detail pane.</summary>
    public IReadOnlyList<string> Groups => _groups ??= Model.Groups
        .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Clickable group chips that filter the list to the chosen group.</summary>
    public IReadOnlyList<DependencyViewModel> GroupChips => _groupChips ??= Groups
        .Select(group => new DependencyViewModel
        {
            Name = group,
            NavigateCommand = _filterByGroup is null
                ? NoopCommand
                : new RelayCommand(() => _filterByGroup.Execute(group)),
        })
        .ToList();

    public bool HasGroup(string group)
        => Groups.Any(item => string.Equals(item, group, StringComparison.Ordinal));

    public string GroupsText => string.Join(", ", Groups);

    public IBrush ReasonBackground
    {
        get
        {
            var (r, g, b) = ReasonColor;
            return Brush(r, g, b, 40);
        }
    }

    public IBrush ReasonBorderBrush
    {
        get
        {
            var (r, g, b) = ReasonColor;
            return Brush(r, g, b, 115);
        }
    }

    public IBrush ReasonForeground
    {
        get
        {
            var (r, g, b) = ReasonColor;
            return Brush(r, g, b, 255);
        }
    }

    private (byte R, byte G, byte B) ReasonColor => Model.Explicit
        ? ((byte)46, (byte)160, (byte)120)
        : ((byte)214, (byte)132, (byte)40);

    /// <summary>"id · description", with empty parts omitted.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (Model.PackageId.Length > 0 && Model.PackageId != Model.Name)
                parts.Add(Model.PackageId);
            if (Model.Description.Length > 0)
                parts.Add(Model.Description);
            return string.Join(" · ", parts);
        }
    }

    public string TagText => Model.Foreign ? Loc.T("AUR") : BackendInfo.DisplayName(Model.Backend);

    public FontWeight TagFontWeight => Model.Foreign ? FontWeight.Bold : FontWeight.Normal;

    public IBrush TagBackground
    {
        get
        {
            var (r, g, b) = TagColor;
            return Brush(r, g, b, Model.Foreign ? (byte)70 : (byte)45);
        }
    }

    public IBrush TagForeground
    {
        get
        {
            var (r, g, b) = TagColor;
            return Brush(r, g, b, 255);
        }
    }

    public bool ShowBadge =>
        Model.Status != PackageStatus.Idle || !Model.Installed || Model.Ignored;

    public string BadgeText => !Model.Installed
        ? Loc.T("Not installed")
        : Model.Ignored && Model.Status == PackageStatus.Idle
            ? Loc.T("Ignored")
            : Model.Status switch
            {
                PackageStatus.NeedsUpdate => Loc.T("Update"),
                PackageStatus.Installed => Loc.T("Installed"),
                PackageStatus.Updating => Loc.T("Updating…"),
                PackageStatus.Removing => Loc.T("Removing…"),
                PackageStatus.Failed => Loc.T("Failed"),
                _ => string.Empty,
            };

    public IBrush BadgeBackground
    {
        get
        {
            if (!Model.Installed)
                return Brush(120, 128, 140, 45);
            if (Model.Ignored && Model.Status == PackageStatus.Idle)
                return Brush(120, 128, 140, 45);
            var (r, g, b) = Model.Status switch
            {
                PackageStatus.NeedsUpdate => (246, 116, 0),
                PackageStatus.Failed => (200, 40, 40),
                _ => (0, 120, 210),
            };
            return Brush((byte)r, (byte)g, (byte)b, 45);
        }
    }

    public IBrush BadgeForeground
    {
        get
        {
            if (!Model.Installed)
                return Brush(100, 108, 120, 255);
            var (r, g, b) = Model.Status switch
            {
                PackageStatus.NeedsUpdate => (226, 100, 0),
                PackageStatus.Failed => (200, 40, 40),
                _ => (0, 110, 190),
            };
            return Brush((byte)r, (byte)g, (byte)b, 255);
        }
    }

    private (byte R, byte G, byte B) OriginColor
    {
        get
        {
            if (Model.Foreign)
                return ((byte)130, (byte)80, (byte)223);

            var origin = Model.Origin.Trim();
            if (origin.Length == 0)
                return TagColor;

            switch (origin.ToLowerInvariant())
            {
                case "core": return ((byte)214, (byte)86, (byte)73);
                case "extra": return ((byte)56, (byte)121, (byte)216);
                case "multilib": return ((byte)46, (byte)160, (byte)120);
                case "community": return ((byte)72, (byte)160, (byte)92);
                case "chaotic-aur": return ((byte)170, (byte)90, (byte)200);
                case "flathub": return ((byte)64, (byte)160, (byte)140);
                case "crates.io": return ((byte)207, (byte)90, (byte)33);
            }

            // Developer tool origins (e.g. FVM's "global") keep their
            // backend's colour so the two tags still read as a pair.
            if (Model.Backend != Backend.Pacman)
                return TagColor;

            // Unknown repository: pick a stable colour from the name.
            var hash = 0;
            foreach (var c in origin)
                hash = unchecked(hash * 31 + c);
            return OriginPalette[(hash & 0x7FFFFFFF) % OriginPalette.Length];
        }
    }

    private static readonly (byte R, byte G, byte B)[] OriginPalette =
    [
        ((byte)56, (byte)121, (byte)216),
        ((byte)214, (byte)86, (byte)73),
        ((byte)46, (byte)160, (byte)120),
        ((byte)170, (byte)90, (byte)200),
        ((byte)214, (byte)132, (byte)40),
        ((byte)56, (byte)150, (byte)190),
        ((byte)190, (byte)80, (byte)130),
        ((byte)120, (byte)140, (byte)60),
    ];

    private (byte R, byte G, byte B) TagColor => Model.Foreign
        ? ((byte)130, (byte)80, (byte)223)
        : Model.Backend switch
        {
            Backend.Pacman => ((byte)56, (byte)121, (byte)216),
            Backend.Flatpak => ((byte)64, (byte)160, (byte)140),
            Backend.Cargo => ((byte)207, (byte)90, (byte)33),
            Backend.Mise => ((byte)190, (byte)145, (byte)0),
            Backend.Rustup => ((byte)190, (byte)70, (byte)110),
            Backend.Fvm => ((byte)100, (byte)115, (byte)165),
            _ => ((byte)120, (byte)120, (byte)120),
        };

    private static IBrush Brush(byte r, byte g, byte b, byte a)
    {
        var key = ((uint)(r << 16 | g << 8 | b), a);
        if (!BrushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            BrushCache[key] = brush;
        }
        return brush;
    }

    /// <summary>Re-evaluates the display properties after the model changed (e.g. updates).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(HasOrigin));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(OriginBackground));
        OnPropertyChanged(nameof(OriginBorderBrush));
        OnPropertyChanged(nameof(OriginForeground));
        OnPropertyChanged(nameof(ShowInstallReason));
        OnPropertyChanged(nameof(InstallReasonText));
        OnPropertyChanged(nameof(ShowOrphan));
        OnPropertyChanged(nameof(OrphanText));
        _groups = null;
        _groupChips = null;
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(GroupsText));
        OnPropertyChanged(nameof(GroupChips));
        OnPropertyChanged(nameof(ReasonBackground));
        OnPropertyChanged(nameof(ReasonBorderBrush));
        OnPropertyChanged(nameof(ReasonForeground));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(TagText));
        OnPropertyChanged(nameof(TagFontWeight));
        OnPropertyChanged(nameof(TagBackground));
        OnPropertyChanged(nameof(TagForeground));
        OnPropertyChanged(nameof(ShowBadge));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeBackground));
        OnPropertyChanged(nameof(BadgeForeground));
        OnPropertyChanged(nameof(IgnoreText));
        OnPropertyChanged(nameof(ShowInstallAction));
        OnPropertyChanged(nameof(ShowInstalledActions));
        OnPropertyChanged(nameof(ShowAurAction));
        OnPropertyChanged(nameof(ShowFilesAction));
        OnPropertyChanged(nameof(ShowMakeExplicitAction));
        OnPropertyChanged(nameof(ShowDownloadAction));
        OnPropertyChanged(nameof(ShowReinstallAction));
        OnPropertyChanged(nameof(ShowDowngradeAction));
    }
}
