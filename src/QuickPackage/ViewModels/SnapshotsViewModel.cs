using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public partial class SnapshotListItemViewModel : ObservableObject
{
    public SnapshotListItemViewModel(PackageSnapshot snapshot)
    {
        Snapshot = snapshot;
        var explicitCount = snapshot.Packages.Count(entry => entry.Explicit == true);
        var dependencyCount = snapshot.Packages.Count(entry => entry.Explicit == false);
        var orphanCount = snapshot.Packages.Count(entry => entry.Orphan);
        CountText = Loc.T("%1 packages · %2 explicit · %3 dependency · %4 orphans",
            snapshot.Packages.Count,
            explicitCount,
            dependencyCount,
            orphanCount);
        BackendText = string.Join(" · ", snapshot.Packages
            .GroupBy(entry => entry.Backend)
            .OrderBy(group => group.Key)
            .Select(group => $"{BackendInfo.DisplayName(group.Key)} {group.Count()}"));
    }

    public PackageSnapshot Snapshot { get; }

    public string TimeText => Snapshot.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string CountText { get; }

    public string BackendText { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class SnapshotColumnViewModel
{
    public required string Label { get; init; }
}

public sealed class SnapshotCellViewModel
{
    public required string Text { get; init; }
    public IBrush? Foreground { get; init; }
    public FontWeight FontWeight { get; init; } = FontWeight.Normal;
}

public sealed class SnapshotRowViewModel
{
    public required string Name { get; init; }
    public required string BackendText { get; init; }
    public required string ReasonText { get; init; }
    public required IReadOnlyList<SnapshotCellViewModel> Cells { get; init; }
    public IBrush? NameForeground { get; init; }
    public IBrush? ReasonForeground { get; init; }
    public bool ShowReason { get; init; }
    public bool ShowOrphan { get; init; }
    public string OrphanText => Loc.T("Orphan");
}

/// <summary>Lists saved snapshots and compares any selected subset.</summary>
public partial class SnapshotsViewModel : ObservableObject
{
    private readonly PackageManager _manager;

    public SnapshotsViewModel(PackageManager manager)
    {
        _manager = manager;

        BackendFilters.Add(new OptionItem { Value = "all", Label = Loc.T("All backends") });
        foreach (var backend in new[]
                 {
                     Backend.Pacman, Backend.Aur, Backend.Flatpak, Backend.Cargo,
                     Backend.Mise, Backend.Rustup, Backend.Fvm,
                 })
        {
            BackendFilters.Add(new OptionItem
            {
                Value = backend.ToString(),
                Label = BackendInfo.DisplayName(backend),
            });
        }

        ReasonFilters.Add(new OptionItem { Value = "all", Label = Loc.T("All reasons") });
        ReasonFilters.Add(new OptionItem { Value = "explicit", Label = Loc.T("Explicit") });
        ReasonFilters.Add(new OptionItem { Value = "dependency", Label = Loc.T("Dependency") });
        ReasonFilters.Add(new OptionItem { Value = "orphan", Label = Loc.T("Orphan") });

        _selectedBackendFilter = BackendFilters[0];
        _selectedReasonFilter = ReasonFilters[0];

        foreach (var snapshot in SnapshotStore.Load().OrderByDescending(item => item.CreatedAt))
            AddItem(snapshot);
        Rebuild();
        // Show the newest snapshot, and the one before it when available, so
        // the window opens on something useful.
        if (Items.Count >= 1)
            Items[0].IsSelected = true;
        if (Items.Count >= 2)
            Items[1].IsSelected = true;
    }

    public ObservableCollection<SnapshotListItemViewModel> Items { get; } = [];

    public ObservableCollection<SnapshotColumnViewModel> Columns { get; } = [];

    public ObservableCollection<SnapshotRowViewModel> Rows { get; } = [];

    public ObservableCollection<OptionItem> BackendFilters { get; } = [];

    public ObservableCollection<OptionItem> ReasonFilters { get; } = [];

    [ObservableProperty]
    private OptionItem? _selectedBackendFilter;

    [ObservableProperty]
    private OptionItem? _selectedReasonFilter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statsText = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    private bool _showComparison;

    [ObservableProperty]
    private bool _showEmptyList;

    [ObservableProperty]
    private bool _canDelete;

    [ObservableProperty]
    private bool _canRestore;

    public string Title => Loc.T("Package snapshots");
    public string Subtitle => Loc.T("Compare package sets captured at different times");
    public string CreateText => Loc.T("Create snapshot");
    public string DeleteText => Loc.T("Delete selected");
    public string ExportText => Loc.T("Export");
    public string ImportText => Loc.T("Import");
    public string RestoreText => Loc.T("Restore missing");
    public string PackageHeader => Loc.T("Package");
    public string BackendHeader => Loc.T("Backend / origin");
    public string ReasonHeader => Loc.T("Install reason");
    public string SearchPlaceholder => Loc.T("Filter snapshots…");
    public string LegendText => Loc.T("Green = added · orange = version changed · red = removed");

    partial void OnSelectedBackendFilterChanged(OptionItem? value) => Rebuild();

    partial void OnSelectedReasonFilterChanged(OptionItem? value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnIsBusyChanged(bool value)
        => CanRestore = Items.Any(item => item.IsSelected) && !value;

    private SnapshotListItemViewModel AddItem(PackageSnapshot snapshot)
    {
        var item = new SnapshotListItemViewModel(snapshot);
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);
        return item;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SnapshotListItemViewModel.IsSelected))
            Rebuild();
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        HintText = Loc.T("Capturing packages from every enabled backend…");
        try
        {
            var snapshot = await _manager.CaptureSnapshotAsync(CancellationToken.None);
            var item = AddItem(snapshot);
            Items.Move(Items.IndexOf(item), 0);
            var hadSelection = Items.Any(other => other.IsSelected && other != item);
            item.IsSelected = true;
            if (!hadSelection && Items.Count > 1)
                Items[1].IsSelected = true;
            Persist();
            Rebuild();
        }
        catch (Exception exception)
        {
            HintText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the selected snapshots to a portable JSON file.</summary>
    public async Task ExportSelectedAsync(string path)
    {
        var selected = Items.Where(item => item.IsSelected).Select(item => item.Snapshot).ToList();
        if (selected.Count == 0)
            return;
        await File.WriteAllTextAsync(path, SnapshotStore.Serialize(selected));
        HintText = Loc.T("Exported %1 snapshots", selected.Count);
    }

    /// <summary>Imports snapshots from a JSON file and keeps the current set intact.</summary>
    public async Task ImportAsync(string path)
    {
        var imported = SnapshotStore.Deserialize(await File.ReadAllTextAsync(path));
        var existing = Items.Select(item => item.Snapshot).ToList();
        var added = 0;
        foreach (var snapshot in imported)
        {
            if (existing.Any(other => other.Id == snapshot.Id))
                snapshot.Id = Guid.NewGuid().ToString("N");
            var item = AddItem(snapshot);
            item.IsSelected = true;
            ++added;
        }
        Persist();
        Rebuild();
        HintText = Loc.T("Imported %1 snapshots", added);
    }

    /// <summary>
    /// Installs packages present in the newest selected snapshot but missing
    /// now. Versions are not pinned; the backends install their current
    /// candidate for each name.
    /// </summary>
    public async Task<int> RestoreMissingAsync()
    {
        var snapshot = Items
            .Where(item => item.IsSelected)
            .OrderByDescending(item => item.Snapshot.CreatedAt)
            .FirstOrDefault()?.Snapshot;
        if (snapshot is null || IsBusy)
            return 0;

        IsBusy = true;
        var count = 0;
        try
        {
            HintText = Loc.T("Loading the installed package set…");
            var installed = await _manager.LoadPackagesAsync(CancellationToken.None);
            var present = installed
                .Where(package => package.Installed)
                .Select(package => (package.Backend, package.Name))
                .ToHashSet();

            var missing = snapshot.Packages
                .Where(entry => _manager.IsBackendEnabled(entry.Backend))
                .Where(entry => !present.Contains((entry.Backend, entry.Name)))
                .GroupBy(entry => (entry.Backend, entry.Name))
                .Select(group => group.First())
                .Select(entry => new Package
                {
                    Name = entry.Name,
                    PackageId = entry.Name,
                    Version = entry.Version,
                    Origin = entry.Origin,
                    Backend = entry.Backend,
                    Installed = false,
                })
                .ToList();

            if (missing.Count == 0)
            {
                HintText = Loc.T("Everything in this snapshot is already installed.");
                return 0;
            }

            count = missing.Count;
            HintText = Loc.T("Installing %1 missing packages…", count);
            await _manager.RunOperationAsync(PackageOperation.Install, missing, CancellationToken.None);
            HintText = Loc.T("Restore requested for %1 packages", count);
            return count;
        }
        catch (Exception exception)
        {
            HintText = exception.Message;
            return 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Removes every checked snapshot after the window confirms it.</summary>
    public void DeleteSelected()
    {
        var selected = Items.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
            return;
        foreach (var item in selected)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            Items.Remove(item);
        }
        Persist();
        Rebuild();
    }

    private void Persist()
        => SnapshotStore.Save(Items
            .Select(item => item.Snapshot)
            .OrderByDescending(snapshot => snapshot.CreatedAt));

    private void Rebuild()
    {
        var selected = Items
            .Where(item => item.IsSelected)
            .Select(item => item.Snapshot)
            .OrderBy(snapshot => snapshot.CreatedAt)
            .ToList();

        ShowEmptyList = Items.Count == 0;
        CanDelete = selected.Count > 0;
        CanRestore = selected.Count > 0 && !IsBusy;
        ShowComparison = selected.Count >= 1;

        Columns.Clear();
        Rows.Clear();
        SummaryText = string.Empty;
        StatsText = string.Empty;

        if (!ShowComparison)
        {
            HintText = Items.Count == 0
                ? Loc.T("No snapshots yet. Create one to start viewing or comparing.")
                : Loc.T("Select one or more snapshots to view or compare.");
            return;
        }

        HintText = string.Empty;
        foreach (var snapshot in selected)
        {
            Columns.Add(new SnapshotColumnViewModel
            {
                Label = snapshot.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm:ss"),
            });
        }

        var maps = selected.Select(MapOf).ToList();
        var keys = maps
            .SelectMany(map => map.Keys)
            .Distinct()
            .ToList();

        var latest = selected[^1];
        var explicitCount = latest.Packages.Count(entry => entry.Explicit == true);
        StatsText = Loc.T("%1 explicit · %2 dependency · %3 orphans",
            explicitCount,
            latest.Packages.Count(entry => entry.Explicit == false),
            latest.Packages.Count(entry => entry.Orphan));

        if (selected.Count == 1)
        {
            SummaryText = Loc.T("%1 snapshots · %2 packages",
                selected.Count, latest.Packages.Count);
        }
        else
        {
            var first = maps[0];
            var last = maps[^1];
            int added = 0, removed = 0, changed = 0;
            foreach (var key in keys)
            {
                var inFirst = first.ContainsKey(key);
                var inLast = last.ContainsKey(key);
                if (!inFirst && inLast)
                    ++added;
                else if (inFirst && !inLast)
                    ++removed;
                else if (inFirst && inLast && first[key].Version != last[key].Version)
                    ++changed;
            }

            SummaryText = Loc.T("%1 snapshots · %2 added · %3 removed · %4 changed",
                selected.Count, added, removed, changed);
        }

        var backendFilter = SelectedBackendFilter?.Value ?? "all";
        var reasonFilter = SelectedReasonFilter?.Value ?? "all";
        var search = SearchText.Trim();

        var rows = new List<(SnapshotRowViewModel Row, bool Changed)>(keys.Count);
        foreach (var key in keys)
        {
            SnapshotEntry? representative = null;
            for (var i = maps.Count - 1; i >= 0 && representative is null; --i)
                maps[i].TryGetValue(key, out representative);

            if (representative is null)
                continue;
            if (backendFilter != "all" && representative.Backend.ToString() != backendFilter)
                continue;
            if (reasonFilter switch
                {
                    "explicit" => representative.Explicit != true,
                    "dependency" => representative.Explicit != false,
                    "orphan" => !representative.Orphan,
                    _ => false,
                })
            {
                continue;
            }
            if (search.Length > 0
                && !representative.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versions = maps
                .Select(map => map.TryGetValue(key, out var entry) ? entry.Version : null)
                .ToList();
            var isChanged = versions.Distinct().Count() > 1;

            var cells = new List<SnapshotCellViewModel>(versions.Count);
            for (var i = 0; i < versions.Count; ++i)
            {
                var present = versions[i] is not null;
                var previousPresent = i > 0 && versions[i - 1] is not null;
                if (!present)
                {
                    cells.Add(new SnapshotCellViewModel
                    {
                        Text = "—",
                        Foreground = previousPresent ? RemovedBrush : AbsentBrush,
                    });
                    continue;
                }

                var cellChanged = i > 0 && previousPresent && versions[i] != versions[i - 1];
                cells.Add(new SnapshotCellViewModel
                {
                    Text = versions[i]!,
                    Foreground = i > 0 && !previousPresent
                        ? AddedBrush
                        : cellChanged ? ChangedBrush : NormalBrush,
                    FontWeight = cellChanged
                        ? FontWeight.SemiBold
                        : FontWeight.Normal,
                });
            }

            var backendText = BackendInfo.DisplayName(representative.Backend);
            if (representative.Origin.Length > 0)
                backendText += " · " + representative.Origin;

            rows.Add((new SnapshotRowViewModel
            {
                Name = representative.Name,
                BackendText = backendText,
                ReasonText = representative.Explicit switch
                {
                    true => Loc.T("Explicit"),
                    false => Loc.T("Dependency"),
                    null => string.Empty,
                },
                ReasonForeground = representative.Explicit switch
                {
                    true => AddedBrush,
                    false => ChangedBrush,
                    null => null,
                },
                ShowReason = representative.Explicit.HasValue,
                ShowOrphan = representative.Orphan,
                Cells = cells,
                NameForeground = isChanged ? ChangedBrush : NormalBrush,
            }, isChanged));
        }

        foreach (var (row, _) in rows
            .OrderByDescending(item => item.Changed)
            .ThenBy(item => item.Row.Name, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(row);
        }
    }

    private static Dictionary<(Backend, string), SnapshotEntry> MapOf(PackageSnapshot snapshot)
    {
        var map = new Dictionary<(Backend, string), SnapshotEntry>();
        foreach (var entry in snapshot.Packages)
            map.TryAdd((entry.Backend, entry.Name), entry);
        return map;
    }

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static IBrush NormalBrush => Brush("#E7E9EC", "#1F2328");
    private static IBrush AbsentBrush => Brush("#6B7280", "#8A9099");
    private static IBrush AddedBrush => Brush("#57C99A", "#2E9E6B");
    private static IBrush RemovedBrush => Brush("#FF6B6B", "#C63B3B");
    private static IBrush ChangedBrush => Brush("#F0A44A", "#D97706");

    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    /// <summary>Returns the dark-theme colour first and the light one second.</summary>
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
