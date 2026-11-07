using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public sealed class HistoryEntryViewModel
{
    public required string TimeText { get; init; }
    public required string ActionText { get; init; }
    public required string Name { get; init; }
    public required string VersionText { get; init; }
    public required IBrush? ActionForeground { get; init; }
}

/// <summary>Shows the recent libalpm transactions with a text/action filter.</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly List<HistoryEntry> _all;

    public HistoryViewModel()
    {
        _all = PacmanHistory.Load();
        ActionFilters.Add(new OptionItem { Value = "all", Label = Loc.T("All actions") });
        ActionFilters.Add(new OptionItem { Value = "installed", Label = Loc.T("Installed") });
        ActionFilters.Add(new OptionItem { Value = "upgraded", Label = Loc.T("Upgraded") });
        ActionFilters.Add(new OptionItem { Value = "removed", Label = Loc.T("Removed") });
        ActionFilters.Add(new OptionItem { Value = "reinstalled", Label = Loc.T("Reinstalled") });
        ActionFilters.Add(new OptionItem { Value = "downgraded", Label = Loc.T("Downgraded") });
        _selectedActionFilter = ActionFilters[0];
        Apply();
    }

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    public ObservableCollection<OptionItem> ActionFilters { get; } = [];

    [ObservableProperty]
    private OptionItem? _selectedActionFilter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private bool _hasEntries;

    public string Title => Loc.T("Package history");
    public string Subtitle => Loc.T("Recent pacman transactions from /var/log/pacman.log");
    public string SearchPlaceholder => Loc.T("Filter history…");
    public string TimeHeader => Loc.T("Time");
    public string ActionHeader => Loc.T("Action");
    public string PackageHeader => Loc.T("Package");
    public string VersionHeader => Loc.T("Version");
    public string EmptyText => Loc.T("No matching history entries");
    public string CloseText => Loc.T("Close");

    partial void OnSelectedActionFilterChanged(OptionItem? value) => Apply();

    partial void OnSearchTextChanged(string value) => Apply();

    private void Apply()
    {
        var search = SearchText.Trim();
        var filter = SelectedActionFilter?.Value ?? "all";
        Entries.Clear();
        foreach (var entry in _all)
        {
            if (filter != "all" && !string.Equals(entry.Action.ToString(), filter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (search.Length > 0
                && !entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !entry.NewVersion.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !entry.OldVersion.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            Entries.Add(new HistoryEntryViewModel
            {
                TimeText = entry.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ActionText = ActionText(entry.Action),
                Name = entry.Name,
                VersionText = entry.Action is HistoryAction.Upgraded or HistoryAction.Downgraded
                    && entry.OldVersion.Length > 0 && entry.NewVersion.Length > 0
                    ? entry.OldVersion + " → " + entry.NewVersion
                    : entry.NewVersion,
                ActionForeground = ActionBrush(entry.Action),
            });
        }
        HasEntries = Entries.Count > 0;
        CountText = Loc.T("%1 entries", Entries.Count);
    }

    private static string ActionText(HistoryAction action) => action switch
    {
        HistoryAction.Installed => Loc.T("Installed"),
        HistoryAction.Upgraded => Loc.T("Upgraded"),
        HistoryAction.Removed => Loc.T("Removed"),
        HistoryAction.Reinstalled => Loc.T("Reinstalled"),
        HistoryAction.Downgraded => Loc.T("Downgraded"),
        _ => string.Empty,
    };

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    private static IBrush ActionBrush(HistoryAction action) => action switch
    {
        HistoryAction.Installed => Brush("#57C99A", "#2E9E6B"),
        HistoryAction.Upgraded => Brush("#6EA8FE", "#2563EB"),
        HistoryAction.Removed => Brush("#E5534B", "#C0392B"),
        HistoryAction.Reinstalled => Brush("#F0A44A", "#D97706"),
        HistoryAction.Downgraded => Brush("#C792EA", "#7C3AED"),
        _ => Brush("#8A9099", "#6B7280"),
    };

    private static IBrush Brush(string dark, string light)
    {
        var value = IsDark ? dark : light;
        if (!Brushes.TryGetValue(value, out var brush))
        {
            brush = new SolidColorBrush(Color.Parse(value));
            Brushes[value] = brush;
        }
        return brush;
    }
}
