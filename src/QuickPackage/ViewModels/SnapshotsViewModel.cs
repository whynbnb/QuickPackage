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
        var composition = string.Join(" · ", snapshot.Packages
            .GroupBy(entry => entry.Backend)
            .OrderBy(group => group.Key)
            .Select(group => $"{BackendInfo.DisplayName(group.Key)} {group.Count()}"));
        DetailText = composition.Length == 0
            ? Loc.T("%1 packages", snapshot.Packages.Count)
            : Loc.T("%1 packages", snapshot.Packages.Count) + " · " + composition;
    }

    public PackageSnapshot Snapshot { get; }

    public string TimeText => Snapshot.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string DetailText { get; }

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
    public required string BackendLabel { get; init; }
    public required IReadOnlyList<SnapshotCellViewModel> Cells { get; init; }
    public IBrush? NameForeground { get; init; }
}

/// <summary>Lists saved snapshots and compares any selected subset.</summary>
public partial class SnapshotsViewModel : ObservableObject
{
    private readonly PackageManager _manager;

    public SnapshotsViewModel(PackageManager manager)
    {
        _manager = manager;
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

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    private bool _showComparison;

    [ObservableProperty]
    private bool _showEmptyList;

    [ObservableProperty]
    private bool _canDelete;

    public string Title => Loc.T("Package snapshots");
    public string Subtitle => Loc.T("Compare package sets captured at different times");
    public string CreateText => Loc.T("Create snapshot");
    public string DeleteText => Loc.T("Delete selected");
    public string PackageHeader => Loc.T("Package");
    public string BackendHeader => Loc.T("Backend");
    public string LegendText => Loc.T("Green = added · orange = version changed · red = removed");

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
        ShowComparison = selected.Count >= 1;

        Columns.Clear();
        Rows.Clear();
        SummaryText = string.Empty;

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

        var maps = selected
            .Select(snapshot => snapshot.Packages.ToDictionary(
                entry => (entry.Backend, entry.Name),
                entry => entry.Version))
            .ToList();
        var keys = maps
            .SelectMany(map => map.Keys)
            .Distinct()
            .ToList();

        var first = maps[0];
        var last = maps[^1];
        int added = 0, removed = 0, changed = 0;
        if (selected.Count == 1)
        {
            SummaryText = Loc.T("%1 snapshots · %2 packages",
                selected.Count, selected[0].Packages.Count);
        }
        else
        {
            foreach (var key in keys)
            {
                var inFirst = first.ContainsKey(key);
                var inLast = last.ContainsKey(key);
                if (!inFirst && inLast)
                    ++added;
                else if (inFirst && !inLast)
                    ++removed;
                else if (inFirst && inLast && first[key] != last[key])
                    ++changed;
            }

            SummaryText = Loc.T("%1 snapshots · %2 added · %3 removed · %4 changed",
                selected.Count, added, removed, changed);
        }

        var rows = new List<(SnapshotRowViewModel Row, bool Changed)>(keys.Count);
        foreach (var key in keys)
        {
            var versions = maps
                .Select(map => map.TryGetValue(key, out var version) ? version : null)
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

            rows.Add((new SnapshotRowViewModel
            {
                Name = key.Name,
                BackendLabel = BackendInfo.DisplayName(key.Backend),
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
