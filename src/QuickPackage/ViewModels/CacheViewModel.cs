using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public sealed class CacheEntryViewModel
{
    public required string Name { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IBrush? StatusForeground { get; init; }
}

/// <summary>Shows the pacman/AUR cache sizes, contents and native cleanup.</summary>
public partial class CacheViewModel : ObservableObject
{
    private readonly PackageManager _manager;
    private readonly List<CacheEntryViewModel> _pacmanAll = [];

    public CacheViewModel(PackageManager manager)
    {
        _manager = manager;
        _ = RefreshAsync();
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _pacmanPath = "/var/cache/pacman/pkg";

    [ObservableProperty]
    private string _pacmanSize = "…";

    [ObservableProperty]
    private string _aurPath = string.Empty;

    [ObservableProperty]
    private string _aurSize = string.Empty;

    [ObservableProperty]
    private string _aurHelper = string.Empty;

    [ObservableProperty]
    private bool _hasPaccache;

    [ObservableProperty]
    private bool _hasAurHelper;

    [ObservableProperty]
    private bool _hasPacmanEntries;

    [ObservableProperty]
    private bool _hasAurEntries;

    [ObservableProperty]
    private string _pacmanContentsHeader = string.Empty;

    [ObservableProperty]
    private string _aurContentsHeader = string.Empty;

    [ObservableProperty]
    private string _pacmanSearchText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<CacheEntryViewModel> PacmanEntries { get; } = [];

    public ObservableCollection<CacheEntryViewModel> AurEntries { get; } = [];

    public string Title => Loc.T("Cache cleanup");
    public string Subtitle => Loc.T("Clean the local package caches used by pacman and the AUR helper.");
    public string PacmanSection => Loc.T("pacman cache");
    public string AurSection => Loc.T("AUR helper cache");
    public string CleanUninstalledText => Loc.T("Clean uninstalled versions");
    public string KeepLatestText => Loc.T("Keep only the latest version");
    public string CleanAllText => Loc.T("Clean all");
    public string CleanAurText => Loc.T("Clean AUR cache");
    public string RefreshText => Loc.T("Refresh sizes");
    public string CloseText => Loc.T("Close");
    public string FilterPlaceholder => Loc.T("Filter cached packages…");
    public string NoPacmanEntriesText => Loc.T("No cached packages");
    public string NoAurEntriesText => Loc.T("No cached AUR sources");
    public string PaccacheHint => Loc.T("paccache is used when available; otherwise pacman -Sc is used.");
    public string AurHint => Loc.T("Uses the helper's native -Sc/-Scc flags.");
    public string NoAurHelperText => Loc.T("No AUR helper (paru or yay) found");
    public string VersionHeader => Loc.T("Version");
    public string PackageVersionHeader => Loc.T("Package / version");
    public string SizeHeader => Loc.T("Size");
    public string StatusHeader => Loc.T("Status");

    partial void OnPacmanSearchTextChanged(string value) => ApplyPacmanFilter();

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var usage = await _manager.GetCacheUsageAsync(CancellationToken.None);
            PacmanPath = usage.PacmanPath;
            PacmanSize = usage.PacmanSize;
            AurHelper = usage.AurHelper;
            AurPath = usage.AurPath;
            AurSize = usage.AurSize;
            HasPaccache = usage.HasPaccache;
            HasAurHelper = usage.AurHelper.Length > 0;

            var installed = await _manager.GetInstalledVersionsAsync(CancellationToken.None);
            LoadPacmanContents(installed);
            LoadAurContents();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task CleanPacmanAsync(PackageManager.CacheCleanMode mode)
        => RunAsync(() => _manager.CleanPacmanCacheAsync(mode, CancellationToken.None));

    public Task CleanAurAsync(bool all)
        => RunAsync(() => _manager.CleanAurCacheAsync(all, CancellationToken.None));

    private async Task RunAsync(Func<Task<CommandRunner.Result>> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var run = await action();
            StatusMessage = run.Ok
                ? Loc.T("Cache cleaned")
                : Loc.T("Failed: %1", Tail(run.Output));
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private void LoadPacmanContents(Dictionary<string, string> installed)
    {
        _pacmanAll.Clear();
        try
        {
            foreach (var file in Directory.EnumerateFiles(PacmanPath))
            {
                var fileName = Path.GetFileName(file);
                var match = PacmanCacheFileRegex().Match(fileName);
                if (!match.Success)
                    continue;

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                var version = match.Groups["version"].Value;
                var (status, brush) = !installed.TryGetValue(name, out var installedVersion)
                    ? (Loc.T("Not installed"), AbsentBrush)
                    : installedVersion == version
                        ? (Loc.T("Installed"), CurrentBrush)
                        : (Loc.T("Old version"), OldBrush);

                _pacmanAll.Add(new CacheEntryViewModel
                {
                    Name = name,
                    Version = version,
                    Size = FormatSize(length),
                    Status = status,
                    StatusForeground = brush,
                });
            }
        }
        catch
        {
            // The cache directory may be missing or partially unreadable.
        }

        _pacmanAll.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(b.Version, a.Version, StringComparison.Ordinal);
        });

        HasPacmanEntries = _pacmanAll.Count > 0;
        PacmanContentsHeader = Loc.T("%1 cached packages", _pacmanAll.Count);
        ApplyPacmanFilter();
    }

    private void LoadAurContents()
    {
        AurEntries.Clear();
        if (HasAurHelper)
        {
            var clone = Path.Combine(AurPath, "clone");
            if (Directory.Exists(clone))
            {
                foreach (var directory in Directory.EnumerateDirectories(clone)
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    AurEntries.Add(new CacheEntryViewModel { Name = Path.GetFileName(directory) });
                }
            }
        }

        HasAurEntries = AurEntries.Count > 0;
        AurContentsHeader = Loc.T("%1 cached AUR packages", AurEntries.Count);
    }

    private void ApplyPacmanFilter()
    {
        var search = PacmanSearchText.Trim();
        PacmanEntries.Clear();
        foreach (var entry in _pacmanAll)
        {
            if (search.Length > 0
                && !entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !entry.Version.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            PacmanEntries.Add(entry);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            ++unit;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static string Tail(string output)
        => output.Trim() is { Length: > 0 } text
            ? (text.Length <= 200 ? text : text[^200..])
            : string.Empty;

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static IBrush CurrentBrush => Brush("#57C99A", "#2E9E6B");
    private static IBrush OldBrush => Brush("#F0A44A", "#D97706");
    private static IBrush AbsentBrush => Brush("#6B7280", "#8A9099");

    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

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

    [GeneratedRegex(@"^(?<name>.+)-(?<version>[^-]+-[^-]+)-(?<arch>[^-]+)\.pkg\.tar(?:\.\w+)?$")]
    private static partial Regex PacmanCacheFileRegex();
}
