using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

/// <summary>A single package card. Wraps a <see cref="Package"/> and exposes display data.</summary>
public partial class PackageItemViewModel : ObservableObject
{
    private static readonly Dictionary<(uint, byte), IBrush> BrushCache = [];

    public Package Model { get; }

    public PackageItemViewModel(Package model)
    {
        Model = model;
        _isSelected = model.Selected;
    }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => Model.Selected = value;

    public string Name => Model.Name;

    public string VersionText => Model.NewVersion.Length == 0
        ? Model.Version
        : $"{(Model.Version.Length == 0 ? "—" : Model.Version)} → {Model.NewVersion}";

    /// <summary>"origin · id · description", with empty parts omitted.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (Model.Origin.Length > 0)
                parts.Add(Model.Origin);
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

    public string IconGlyph => Model.Foreign ? "A" : Model.Backend switch
    {
        Backend.Pacman => "P",
        Backend.Flatpak => "F",
        Backend.Cargo => "C",
        Backend.Mise => "M",
        Backend.Rustup => "R",
        Backend.Fvm => "V",
        _ => "?",
    };

    public IBrush IconBackground
    {
        get
        {
            var (r, g, b) = TagColor;
            return Brush(r, g, b, 42);
        }
    }

    public IBrush IconForeground
    {
        get
        {
            var (r, g, b) = TagColor;
            return Brush(r, g, b, 255);
        }
    }

    public bool ShowBadge => Model.Status != PackageStatus.Idle;

    public string BadgeText => Model.Status switch
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
            var (r, g, b) = Model.Status switch
            {
                PackageStatus.NeedsUpdate => (226, 100, 0),
                PackageStatus.Failed => (200, 40, 40),
                _ => (0, 110, 190),
            };
            return Brush((byte)r, (byte)g, (byte)b, 255);
        }
    }

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
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(TagText));
        OnPropertyChanged(nameof(TagFontWeight));
        OnPropertyChanged(nameof(TagBackground));
        OnPropertyChanged(nameof(TagForeground));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IconBackground));
        OnPropertyChanged(nameof(IconForeground));
        OnPropertyChanged(nameof(ShowBadge));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeBackground));
        OnPropertyChanged(nameof(BadgeForeground));
    }
}
