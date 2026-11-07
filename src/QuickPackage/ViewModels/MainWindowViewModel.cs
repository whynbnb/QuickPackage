using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Config _config;
    private readonly ProxySettings _proxy = new();
    private readonly PackageManager _manager = new();
    private readonly OnlineService _online;
    private readonly List<PackageItemViewModel> _all = [];
    private readonly Dictionary<ChipKind, ChipViewModel> _chipByKind = [];
    private readonly Dictionary<string, string> _descriptionCache = new(StringComparer.Ordinal);
    private readonly bool _checkUpdatesOnStart;
    private readonly ConcurrentQueue<Action> _pendingLog = new();
    private readonly Dictionary<long, LogItemViewModel> _commandsByActivity = [];
    private readonly Dictionary<long, string> _activeCommands = [];
    private string _languageSetting = "system";
    private int _logFlushScheduled;
    private bool _logDirty;

    private const int MaxLogItems = 40;
    private const double MinLogPanelHeight = 90;
    private const double MaxLogPanelHeight = 600;
    private Dictionary<Backend, Dictionary<string, string>> _updates = [];
    private bool _updatesLoaded;
    private int _scopedUpdateCount;
    private bool _suppressChipEvents;
    private bool _isDevMode;
    private ChipKind _selectedChip = ChipKind.All;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _remoteSearchCts;

    // Detail-pane navigation history (dependency links and list selections).
    private readonly List<PackageItemViewModel> _navigation = [];
    private int _navigationIndex = -1;
    private bool _suppressNavigation;

    private bool _pacmanEnabled = true;
    private bool _aurEnabled = true;
    private bool _flatpakEnabled = true;
    private bool _cargoEnabled = true;
    private bool _miseEnabled;
    private bool _rustupEnabled;
    private bool _fvmEnabled;
    private readonly HashSet<string> _ignoredPackages = new(StringComparer.Ordinal);
    private readonly ObservableCollection<string> _searchHistory = [];

    public MainWindowViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _config = Config.Load();
        _proxy.Load(_config);
        _online = new OnlineService(_proxy);

        // Sudo needs a password when no ticket is cached; the manager asks
        // back through this callback so the prompt lives in the UI.
        _manager.SetPasswordProvider(AskSudoPasswordAsync);

        _checkUpdatesOnStart = _config.GetBool("General", "CheckUpdatesOnStart", true);
        _languageSetting = _config.GetString("General", "Language", "system");
        LoadBackendSettings();
        LoadIgnoredPackages();
        ApplyBackendSettings();
        RefreshBackendHealth();

        _isLogExpanded = _config.GetBool("General", "LogPanelExpanded", false);
        _isLogDetailed = _config.GetBool("General", "LogDetails", true);
        _logPanelHeight = Math.Clamp(
            _config.GetInt("General", "LogPanelHeight", 170), (int)MinLogPanelHeight, (int)MaxLogPanelHeight);
        _contentSplitRatio = Math.Clamp(
            _config.GetInt("General", "SplitRatioPercent", 60), 20, 80) / 100.0;

        _manager.Message += message =>
        {
            AddLog(() =>
            {
                LogItems.Add(LogItemViewModel.CreateInfo(message));
                TrimLog();
                _logDirty = true;
            });
            Dispatcher.UIThread.Post(() => StatusMessage = message);
        };
        CommandActivity.Started += OnCommandStarted;
        CommandActivity.Output += OnCommandOutput;
        CommandActivity.Finished += OnCommandFinished;

        L = new LocStrings();
        _allOriginsLabel = Loc.T("All origins");
        OriginOptions.Add(_allOriginsLabel);
        _suppressOriginChange = true;
        SelectedOrigin = _allOriginsLabel;
        _suppressOriginChange = false;
        StatusMessage = Loc.T("Ready");
        BuildChips();
        BuildSortOptions();
        LoadSearchHistory();
        RefreshModeUi();
        UpdateCounters();
    }

    public LocStrings L { get; }

    public ObservableCollection<PackageItemViewModel> Packages { get; } = [];

    public ObservableCollection<ChipViewModel> Chips { get; } = [];

    public ObservableCollection<DetailFieldViewModel> DetailFields { get; } = [];

    public ObservableCollection<DependencyViewModel> ForwardDependencies { get; } = [];

    public ObservableCollection<DependencyViewModel> ReverseDependencies { get; } = [];

    public ObservableCollection<LogItemViewModel> LogItems { get; } = [];

    /// <summary>Raised after a batch of log changes has been applied on the UI thread.</summary>
    public event Action? LogUpdated;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _groupFilter = string.Empty;

    public bool HasGroupFilter => GroupFilter.Length > 0;

    public string GroupFilterText => HasGroupFilter
        ? Loc.T("Group: %1", GroupFilter) + "  ✕"
        : string.Empty;

    [ObservableProperty]
    private bool _showUpdatesOnly;

    [ObservableProperty]
    private bool _showOrphansOnly;

    /// <summary>Shows checkboxes on every visible row for batch install/remove.</summary>
    [ObservableProperty]
    private bool _isSelectionMode;

    [ObservableProperty]
    private bool _isSearchingRemote;

    public bool CanSearchAur =>
        _aurEnabled && !_isDevMode && !IsBusy && !IsSearchingRemote
        && _selectedChip is ChipKind.Aur or ChipKind.All
        && SearchText.Trim().Length >= 2;

    public bool CanSearchFlatpak =>
        _flatpakEnabled && !_isDevMode && !IsBusy && !IsSearchingRemote
        && _selectedChip is ChipKind.Flatpak or ChipKind.All
        && SearchText.Trim().Length >= 2;

    public bool CanSearchCrates =>
        _cargoEnabled && !_isDevMode && !IsBusy && !IsSearchingRemote
        && _selectedChip is ChipKind.Cargo or ChipKind.All
        && SearchText.Trim().Length >= 2;

    public bool CanSearchOnline => CanSearchAur || CanSearchFlatpak || CanSearchCrates;

    private void NotifySearchAvailability()
    {
        OnPropertyChanged(nameof(CanSearchAur));
        OnPropertyChanged(nameof(CanSearchFlatpak));
        OnPropertyChanged(nameof(CanSearchCrates));
        OnPropertyChanged(nameof(CanSearchOnline));
    }

    [ObservableProperty]
    private PackageItemViewModel? _selectedPackage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _hasProgress;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string _updatesChipText = string.Empty;

    [ObservableProperty]
    private string _updateSelectedText = string.Empty;

    [ObservableProperty]
    private string _removeSelectedText = string.Empty;

    [ObservableProperty]
    private string _installSelectedText = string.Empty;

    [ObservableProperty]
    private string _uninstallSelectedText = string.Empty;

    [ObservableProperty]
    private bool _hasInstallSelection;

    [ObservableProperty]
    private bool _hasUninstallSelection;

    [ObservableProperty]
    private string _orphansChipText = string.Empty;

    [ObservableProperty]
    private bool _hasOrphans;

    [ObservableProperty]
    private bool _hasUpdates;

    [ObservableProperty]
    private bool _hasAnyUpdates;

    [ObservableProperty]
    private string _updateAllText = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _detailDescription = string.Empty;

    [ObservableProperty]
    private bool _hasDetailDescription;

    [ObservableProperty]
    private bool _isPackageSelected;

    [ObservableProperty]
    private bool _canInstallSelectedPackage;

    [ObservableProperty]
    private bool _canUpdateSelectedPackage;

    [ObservableProperty]
    private bool _canUninstallSelectedPackage;

    [ObservableProperty]
    private bool _canMakeExplicit;

    [ObservableProperty]
    private bool _canShowPackageFiles;

    [ObservableProperty]
    private bool _canToggleIgnore;

    [ObservableProperty]
    private string _ignoreMenuText = string.Empty;

    public bool CanMoreActions => CanMakeExplicit || CanShowPackageFiles || CanToggleIgnore;

    partial void OnCanMakeExplicitChanged(bool value)
        => OnPropertyChanged(nameof(CanMoreActions));

    partial void OnCanShowPackageFilesChanged(bool value)
        => OnPropertyChanged(nameof(CanMoreActions));

    partial void OnCanToggleIgnoreChanged(bool value)
        => OnPropertyChanged(nameof(CanMoreActions));

    [ObservableProperty]
    private bool _isInstalledSelectedPackage;

    [ObservableProperty]
    private bool _hasBackendIssues;

    [ObservableProperty]
    private string _backendIssueText = string.Empty;

    [ObservableProperty]
    private bool _isLogExpanded;

    [ObservableProperty]
    private bool _isLogDetailed = true;

    [ObservableProperty]
    private double _logPanelHeight = 170;

    private double _contentSplitRatio = 0.6;

    /// <summary>Fraction of the master/detail width given to the package list.</summary>
    public double ContentSplitRatio => _contentSplitRatio;

    public void SaveContentSplitRatio(double ratio)
    {
        _contentSplitRatio = Math.Clamp(ratio, 0.2, 0.8);
        _config.Set("General", "SplitRatioPercent",
            (int)Math.Round(_contentSplitRatio * 100));
        _config.Save();
    }

    [ObservableProperty]
    private string _currentCommand = string.Empty;

    [ObservableProperty]
    private LogItemViewModel? _logContext;

    public bool IsNotBusy => !IsBusy;

    /// <summary>Single-line footer: the live command while busy, otherwise the last status.</summary>
    public string FooterText => CurrentCommand.Length > 0 ? CurrentCommand : StatusMessage;

    public string LogChevron => IsLogExpanded ? "▾" : "▸";

    public bool ShowLogContext => IsLogDetailed && LogContext is not null;

    public bool CanBulkUpdate => HasSelection && !IsBusy;

    public bool CanBulkInstall => HasInstallSelection && !IsBusy;

    public bool CanBulkUninstall => HasUninstallSelection && !IsBusy;

    public bool ShowBulkInstall => IsSelectionMode && HasInstallSelection;

    public bool ShowBulkUninstall => IsSelectionMode && HasUninstallSelection;

    public bool CanGoBack => _navigationIndex > 0;

    public bool CanGoForward =>
        _navigationIndex >= 0 && _navigationIndex + 1 < _navigation.Count;

    [ObservableProperty]
    private bool _hasForwardDependencies;

    [ObservableProperty]
    private bool _hasReverseDependencies;

    public bool HasDevBackends => _miseEnabled || _rustupEnabled || _fvmEnabled;

    public bool ShowBackendHint =>
        _isDevMode
            ? !HasDevBackends
            : !_pacmanEnabled && !_aurEnabled && !_flatpakEnabled && !_cargoEnabled;

    public bool ShowEmpty => Packages.Count == 0 && !ShowBackendHint;

    public bool IsDevMode
    {
        get => _isDevMode;
        set
        {
            if (value)
            {
                SetDevMode(true);
                return;
            }
            if (_isDevMode && HasSystemBackend)
                SetDevMode(false);
            else
                OnPropertyChanged(nameof(IsDevMode));
        }
    }

    public bool IsSystemMode
    {
        get => !_isDevMode;
        set
        {
            if (value)
                SetDevMode(false);
            else if (_isDevMode)
                OnPropertyChanged(nameof(IsSystemMode));
        }
    }

    private bool HasSystemBackend =>
        _pacmanEnabled || _aurEnabled || _flatpakEnabled || _cargoEnabled;

    // ------------------------------------------------------- install / origin

    private InstallStateFilter _installState = InstallStateFilter.Installed;
    private string _allOriginsLabel = "All origins";
    private bool _suppressOriginChange;

    public ObservableCollection<string> OriginOptions { get; } = [];

    public ObservableCollection<OptionItem> SortOptions { get; } = [];

    public ObservableCollection<string> SearchHistory => _searchHistory;

    public bool HasSearchHistory => _searchHistory.Count > 0;

    public bool HasSearchText => SearchText.Length > 0;

    private void LoadSearchHistory()
    {
        foreach (var entry in _config.GetString("General", "SearchHistory")
                     .Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _searchHistory.Add(entry);
        }
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    /// <summary>Remembers the current query for the search history flyout.</summary>
    public void RememberSearch()
    {
        var text = SearchText.Trim();
        if (text.Length < 2)
            return;
        _searchHistory.Remove(text);
        _searchHistory.Insert(0, text);
        while (_searchHistory.Count > 10)
            _searchHistory.RemoveAt(_searchHistory.Count - 1);
        _config.Set("General", "SearchHistory", string.Join('\u001f', _searchHistory));
        _config.Save();
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    public void ApplySearch(string query) => SearchText = query;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool _suppressSortChange;

    [ObservableProperty]
    private OptionItem? _selectedSortOption;

    [ObservableProperty]
    private bool _sortDescending;

    public string SortDirectionText => SortDescending ? "↓" : "↑";

    partial void OnSelectedSortOptionChanged(OptionItem? value)
    {
        if (_suppressSortChange)
            return;
        SaveSort();
        ApplyFilter();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        if (_suppressSortChange)
            return;
        SaveSort();
        ApplyFilter();
    }

    private void BuildSortOptions()
    {
        SortOptions.Add(new OptionItem { Value = "name", Label = Loc.T("Name") });
        SortOptions.Add(new OptionItem { Value = "size", Label = Loc.T("Installed size") });
        SortOptions.Add(new OptionItem { Value = "download", Label = Loc.T("Download size") });
        SortOptions.Add(new OptionItem { Value = "installdate", Label = Loc.T("Install date") });
        SortOptions.Add(new OptionItem { Value = "builddate", Label = Loc.T("Build date") });
        SortOptions.Add(new OptionItem { Value = "origin", Label = Loc.T("Origin") });
        SortOptions.Add(new OptionItem { Value = "updates", Label = Loc.T("Update status") });

        var saved = _config.GetString("General", "SortKey", "name");
        _suppressSortChange = true;
        SelectedSortOption = SortOptions.FirstOrDefault(option => option.Value == saved) ?? SortOptions[0];
        SortDescending = _config.GetBool("General", "SortDescending", false);
        _suppressSortChange = false;
    }

    private void SaveSort()
    {
        _config.Set("General", "SortKey", SelectedSortOption?.Value ?? "name");
        _config.Set("General", "SortDescending", SortDescending);
        _config.Save();
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    public string AllOriginsLabel => _allOriginsLabel;

    [ObservableProperty]
    private string _selectedOrigin = string.Empty;

    partial void OnSelectedOriginChanged(string value)
    {
        if (!_suppressOriginChange)
            ApplyFilter();
    }

    public bool IsInstalledFilter
    {
        get => _installState == InstallStateFilter.Installed;
        set
        {
            if (value)
                SetInstallState(InstallStateFilter.Installed);
            else
                OnPropertyChanged();
        }
    }

    public bool IsNotInstalledFilter
    {
        get => _installState == InstallStateFilter.NotInstalled;
        set
        {
            if (value)
                SetInstallState(InstallStateFilter.NotInstalled);
            else
                OnPropertyChanged();
        }
    }

    public bool IsAllPackagesFilter
    {
        get => _installState == InstallStateFilter.All;
        set
        {
            if (value)
                SetInstallState(InstallStateFilter.All);
            else
                OnPropertyChanged();
        }
    }

    private void SetInstallState(InstallStateFilter state)
    {
        if (_installState != state)
        {
            _installState = state;
            RebuildOriginOptions();
            SyncFilterSelection();
            ApplyFilter();
        }
        OnPropertyChanged(nameof(IsInstalledFilter));
        OnPropertyChanged(nameof(IsNotInstalledFilter));
        OnPropertyChanged(nameof(IsAllPackagesFilter));
        UpdateCounters();
    }

    private bool MatchesInstallState(PackageItemViewModel item) => _installState switch
    {
        InstallStateFilter.Installed => item.Model.Installed,
        InstallStateFilter.NotInstalled => !item.Model.Installed,
        _ => true,
    };

    private bool MatchesOrigin(PackageItemViewModel item)
    {
        var selected = SelectedOrigin;
        if (string.IsNullOrEmpty(selected)
            || string.Equals(selected, _allOriginsLabel, StringComparison.Ordinal))
            return true;
        return string.Equals(item.Model.Origin, selected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuilds the origin filter list from the loaded packages. The selected
    /// origin is kept when it still exists and otherwise falls back to "all".
    /// </summary>
    private void RebuildOriginOptions()
    {
        var previous = SelectedOrigin ?? string.Empty;
        _allOriginsLabel = Loc.T("All origins");
        var origins = _all
            .Where(MatchesScope)
            .Select(item => item.Model.Origin)
            .Where(origin => origin.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(origin => origin, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressOriginChange = true;
        OriginOptions.Clear();
        OriginOptions.Add(_allOriginsLabel);
        foreach (var origin in origins)
            OriginOptions.Add(origin);
        SelectedOrigin = OriginOptions.Contains(previous) ? previous : _allOriginsLabel;
        _suppressOriginChange = false;
        OnPropertyChanged(nameof(AllOriginsLabel));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        ApplyFilter();
        NotifySearchAvailability();
        ScheduleAurSearch(value);
    }

    partial void OnIsSearchingRemoteChanged(bool value)
        => NotifySearchAvailability();

    partial void OnGroupFilterChanged(string value)
    {
        OnPropertyChanged(nameof(HasGroupFilter));
        OnPropertyChanged(nameof(GroupFilterText));
        ApplyFilter();
    }

    partial void OnShowUpdatesOnlyChanged(bool value)
    {
        if (value && ShowOrphansOnly)
            ShowOrphansOnly = false;
        SyncFilterSelection();
        ApplyFilter();
    }

    partial void OnShowOrphansOnlyChanged(bool value)
    {
        if (value && ShowUpdatesOnly)
            ShowUpdatesOnly = false;
        SyncFilterSelection();
        ApplyFilter();
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            foreach (var item in _all)
                item.IsSelected = false;
        }
        SyncFilterSelection();
        UpdateCounters();
        OnPropertyChanged(nameof(ShowBulkInstall));
        OnPropertyChanged(nameof(ShowBulkUninstall));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanBulkUpdate));
        OnPropertyChanged(nameof(CanBulkInstall));
        OnPropertyChanged(nameof(CanBulkUninstall));
        NotifySearchAvailability();
    }

    partial void OnHasSelectionChanged(bool value) => OnPropertyChanged(nameof(CanBulkUpdate));

    partial void OnHasInstallSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBulkInstall));
        OnPropertyChanged(nameof(ShowBulkInstall));
    }

    partial void OnHasUninstallSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBulkUninstall));
        OnPropertyChanged(nameof(ShowBulkUninstall));
    }

    partial void OnSelectedPackageChanged(PackageItemViewModel? value)
    {
        RebuildDetail(value);
        if (!_suppressNavigation && value is not null)
            PushNavigation(value);
    }

    private void OnPackagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PackageItemViewModel.IsSelected))
            UpdateCounters();
    }

    partial void OnCurrentCommandChanged(string value) => OnPropertyChanged(nameof(FooterText));

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(FooterText));

    partial void OnIsLogExpandedChanged(bool value) => OnPropertyChanged(nameof(LogChevron));

    partial void OnIsLogDetailedChanged(bool value)
    {
        foreach (var item in LogItems)
            item.ShowDetails = value;
        OnPropertyChanged(nameof(ShowLogContext));
    }

    partial void OnLogContextChanged(LogItemViewModel? value)
        => OnPropertyChanged(nameof(ShowLogContext));

    public async Task InitializeAsync()
    {
        // Apply the saved theme before the first paint already happened in App;
        // re-applying here keeps the view models and the shell in sync.
        App.ApplyTheme(_config.GetString("General", "Theme", "system"));

        await Task.Delay(350);
        await RefreshAsync();
        if (_checkUpdatesOnStart)
            await CheckUpdatesAsync();
    }

    /// <summary>Cancels a running operation (called when the window closes).</summary>
    public void CancelCurrentOperation() => _operationCts?.Cancel();

    // ------------------------------------------------------------------ mode

    private void SetDevMode(bool dev)
    {
        if (dev && !HasDevBackends)
        {
            OnPropertyChanged(nameof(IsDevMode));
            return;
        }
        if (dev == _isDevMode)
        {
            OnPropertyChanged(nameof(IsDevMode));
            OnPropertyChanged(nameof(IsSystemMode));
            return;
        }
        _isDevMode = dev;
        OnPropertyChanged(nameof(IsDevMode));
        OnPropertyChanged(nameof(IsSystemMode));
        // Groups only exist for system (pacman/AUR) packages.
        if (GroupFilter.Length > 0)
            GroupFilter = string.Empty;
        if (dev)
            SelectChip(ChipKind.All);
        RefreshModeUi(adjustMode: false);
        RebuildOriginOptions();
        ApplyFilter();
        UpdateCounters();
    }

    private void RefreshModeUi(bool adjustMode = true)
    {
        if (adjustMode)
        {
            if (_isDevMode && !HasDevBackends)
                _isDevMode = false;
            else if (!_isDevMode && !HasSystemBackend && HasDevBackends)
                _isDevMode = true;
            OnPropertyChanged(nameof(IsDevMode));
            OnPropertyChanged(nameof(IsSystemMode));
        }
        OnPropertyChanged(nameof(HasDevBackends));
        OnPropertyChanged(nameof(ShowBackendHint));

        SetChipVisible(ChipKind.Pacman, !_isDevMode && _pacmanEnabled);
        SetChipVisible(ChipKind.Aur, !_isDevMode && _aurEnabled);
        SetChipVisible(ChipKind.Flatpak, !_isDevMode && _flatpakEnabled);
        SetChipVisible(ChipKind.Cargo, !_isDevMode && _cargoEnabled);
        SetChipVisible(ChipKind.Mise, _isDevMode && _miseEnabled);
        SetChipVisible(ChipKind.Rustup, _isDevMode && _rustupEnabled);
        SetChipVisible(ChipKind.Fvm, _isDevMode && _fvmEnabled);

        if (!IsChipVisible(_selectedChip))
            SelectChip(ChipKind.All);
        OnPropertyChanged(nameof(ShowEmpty));
        NotifySearchAvailability();
    }

    private void SetChipVisible(ChipKind kind, bool visible)
    {
        if (_chipByKind.TryGetValue(kind, out var chip))
            chip.IsVisible = visible;
    }

    private bool IsChipVisible(ChipKind kind)
        => _selectedChip == ChipKind.All
           || (_chipByKind.TryGetValue(kind, out var chip) && chip.IsVisible);

    private void BuildChips()
    {
        Chips.Clear();
        _chipByKind.Clear();
        AddChip(ChipKind.All, Loc.T("All"));
        AddChip(ChipKind.Pacman, BackendInfo.DisplayName(Backend.Pacman));
        AddChip(ChipKind.Aur, Loc.T("AUR"));
        AddChip(ChipKind.Flatpak, BackendInfo.DisplayName(Backend.Flatpak));
        AddChip(ChipKind.Cargo, BackendInfo.DisplayName(Backend.Cargo));
        AddChip(ChipKind.Mise, BackendInfo.DisplayName(Backend.Mise));
        AddChip(ChipKind.Rustup, BackendInfo.DisplayName(Backend.Rustup));
        AddChip(ChipKind.Fvm, BackendInfo.DisplayName(Backend.Fvm));
        SelectChip(ChipKind.All);
    }

    private void AddChip(ChipKind kind, string label)
    {
        var chip = new ChipViewModel(kind, label) { IsChecked = kind == ChipKind.All };
        chip.PropertyChanged += OnChipPropertyChanged;
        _chipByKind[kind] = chip;
        Chips.Add(chip);
    }

    private void OnChipPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ChipViewModel.IsChecked) || _suppressChipEvents
            || sender is not ChipViewModel chip)
            return;
        if (chip.IsChecked)
            SelectChip(chip.Kind);
        else if (chip.Kind == _selectedChip)
        {
            // A chip cannot be deselected directly; fall back to "All".
            _suppressChipEvents = true;
            chip.IsChecked = true;
            _suppressChipEvents = false;
        }
    }

    private void SelectChip(ChipKind kind)
    {
        _selectedChip = kind;
        _suppressChipEvents = true;
        foreach (var chip in Chips)
            chip.IsChecked = chip.Kind == kind;
        _suppressChipEvents = false;
        RebuildOriginOptions();
        ApplyFilter();
        // Narrowing the backend also narrows the bulk selection, otherwise
        // hidden rows of other backends would still be updated/removed and
        // inflate the toolbar counts.
        if (ShowUpdatesOnly || ShowOrphansOnly)
            SyncFilterSelection();
        UpdateCounters();
        NotifySearchAvailability();
        if (kind == ChipKind.Aur)
            ScheduleAurSearch(SearchText);
        else
            CancelRemoteSearch();
    }

    // --------------------------------------------------------------- filtering

    /// <summary>True when the item belongs to the currently selected backend chip.</summary>
    private bool MatchesChip(PackageItemViewModel item)
        => _selectedChip == ChipKind.All || (Backend)_selectedChip == item.Model.Backend;

    /// <summary>True when the item is both in the active mode and under the selected chip.</summary>
    private bool MatchesScope(PackageItemViewModel item)
        => MatchesChip(item)
            && BackendInfo.IsDev(item.Model.Backend) == _isDevMode
            && MatchesInstallState(item);

    /// <summary>
    /// Shows checkboxes only on rows the active filter can act on and checks
    /// them all; rows outside the selected backend are left alone so bulk
    /// actions only ever touch what is currently visible.
    /// </summary>
    private void SyncFilterSelection()
    {
        var filtered = ShowUpdatesOnly || ShowOrphansOnly;
        foreach (var item in _all)
        {
            var inScope = MatchesScope(item);
            var selectable = filtered
                && inScope
                && ((ShowUpdatesOnly && item.Model.Status == PackageStatus.NeedsUpdate)
                    || (ShowOrphansOnly && item.Model.Orphan));
            item.ShowSelection = IsSelectionMode ? inScope : selectable;
            if (filtered)
                item.IsSelected = selectable;
            else if (!IsSelectionMode)
                item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void FilterByGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
            return;
        // A group view is a fresh scope: drop text and update/orphan filters
        // so an unrelated previous filter cannot silently narrow it.
        if (SearchText.Length > 0)
            SearchText = string.Empty;
        if (ShowUpdatesOnly)
            ShowUpdatesOnly = false;
        if (ShowOrphansOnly)
            ShowOrphansOnly = false;
        GroupFilter = group;
        StatusMessage = Loc.T("%1 packages in group %2", Packages.Count, group);
    }

    [RelayCommand]
    private void ClearGroupFilter()
    {
        GroupFilter = string.Empty;
        StatusMessage = Loc.T("Loaded %1 packages", _all.Count);
    }

    private void ApplyFilter()
    {
        var filtered = new List<PackageItemViewModel>(_all.Count);
        foreach (var item in _all)
        {
            if (Accepts(item))
                filtered.Add(item);
        }
        filtered.Sort(ComparePackages);
        Packages.Clear();
        foreach (var item in filtered)
            Packages.Add(item);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private int ComparePackages(PackageItemViewModel left, PackageItemViewModel right)
    {
        var a = left.Model;
        var b = right.Model;
        var key = SelectedSortOption?.Value ?? "name";
        var result = key switch
        {
            "size" => HumanSize.Parse(a.Size).CompareTo(HumanSize.Parse(b.Size)),
            "download" => HumanSize.Parse(a.DownloadSize).CompareTo(HumanSize.Parse(b.DownloadSize)),
            "installdate" => ParseDate(a.InstallDate).CompareTo(ParseDate(b.InstallDate)),
            "builddate" => ParseDate(a.BuildDate).CompareTo(ParseDate(b.BuildDate)),
            "origin" => string.Compare(a.Origin, b.Origin, StringComparison.OrdinalIgnoreCase),
            "updates" => (a.Status == PackageStatus.NeedsUpdate ? 1 : 0)
                .CompareTo(b.Status == PackageStatus.NeedsUpdate ? 1 : 0),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return SortDescending ? -result : result;
    }

    private static DateTime ParseDate(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : DateTime.MinValue;

    // --------------------------------------------------------- remote search

    [RelayCommand]
    private Task SearchAurAsync()
        => StartRemoteSearch(Backend.Aur, Loc.T("Searching the AUR…"),
            (query, token) => _online.SearchAurAsync(query, token));

    [RelayCommand]
    private Task SearchFlatpakAsync()
        => StartRemoteSearch(Backend.Flatpak, Loc.T("Searching Flatpak…"),
            (query, token) => _manager.SearchFlatpakAsync(query, token));

    [RelayCommand]
    private Task SearchCratesAsync()
        => StartRemoteSearch(Backend.Cargo, Loc.T("Searching crates.io…"),
            (query, token) => _online.SearchCratesAsync(query, token));

    private Task StartRemoteSearch(
        Backend backend,
        string status,
        Func<string, CancellationToken, Task<List<Package>>> search)
    {
        var query = SearchText.Trim();
        if (query.Length < 2)
            return Task.CompletedTask;
        CancelRemoteSearch();
        var cts = new CancellationTokenSource();
        _remoteSearchCts = cts;
        return RunRemoteSearchAsync(backend, query, cts, status, search);
    }

    private void ScheduleAurSearch(string text)
    {
        CancelRemoteSearch();
        var query = text.Trim();
        if (!_aurEnabled || _isDevMode || _selectedChip != ChipKind.Aur || query.Length < 2)
            return;

        var cts = new CancellationTokenSource();
        _remoteSearchCts = cts;
        _ = DebouncedAurSearchAsync(query, cts);
    }

    private async Task DebouncedAurSearchAsync(string query, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(700, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            await RunRemoteSearchAsync(
                Backend.Aur, query, cts, Loc.T("Searching the AUR…"),
                (text, token) => _online.SearchAurAsync(text, token));
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this one.
        }
    }

    private async Task RunRemoteSearchAsync(
        Backend backend,
        string query,
        CancellationTokenSource cts,
        string status,
        Func<string, CancellationToken, Task<List<Package>>> search)
    {
        IsSearchingRemote = true;
        try
        {
            StatusMessage = status;
            var results = await search(query, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            RemoveRemoteResults(backend);
            if (_installState == InstallStateFilter.Installed)
                SetInstallState(InstallStateFilter.All);

            var existing = _all
                .Where(item => item.Model.Backend == backend)
                .SelectMany(item => new[] { item.Model.Name, item.Model.EffectiveId })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var package in results)
            {
                if (existing.Contains(package.Name) || existing.Contains(package.EffectiveId))
                    continue;
                existing.Add(package.Name);
                existing.Add(package.EffectiveId);
                var item = CreatePackageItem(package);
                item.PropertyChanged += OnPackagePropertyChanged;
                _all.Add(item);
                ++added;
            }

            RebuildOriginOptions();
            ApplyFilter();
            SyncFilterSelection();
            UpdateCounters();
            StatusMessage = Loc.T("Found %1 %2 packages", added, BackendInfo.DisplayName(backend));
        }
        catch (OperationCanceledException)
        {
            // A newer query replaced this one.
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_remoteSearchCts, cts))
                _remoteSearchCts = null;
            cts.Dispose();
            IsSearchingRemote = false;
        }
    }

    private void CancelRemoteSearch()
    {
        _remoteSearchCts?.Cancel();
        _remoteSearchCts = null;
        IsSearchingRemote = false;
    }

    private void RemoveRemoteResults(Backend backend)
    {
        for (var i = _all.Count - 1; i >= 0; --i)
        {
            var item = _all[i];
            if (!item.Model.RemoteSearch || item.Model.Backend != backend)
                continue;
            item.PropertyChanged -= OnPackagePropertyChanged;
            _all.RemoveAt(i);
        }
    }

    private bool Accepts(PackageItemViewModel item)
    {
        var model = item.Model;
        var backend = model.Backend;
        var isDev = BackendInfo.IsDev(backend);
        // The big switch splits system backends from developer tools.
        if (_isDevMode ? !isDev : isDev)
            return false;

        if (!MatchesInstallState(item))
            return false;

        if (!MatchesOrigin(item))
            return false;

        if (_selectedChip != ChipKind.All && (Backend)_selectedChip != backend)
            return false;

        if (ShowUpdatesOnly && model.Status != PackageStatus.NeedsUpdate)
            return false;

        if (ShowOrphansOnly && !model.Orphan)
            return false;

        if (GroupFilter.Length > 0 && !item.HasGroup(GroupFilter))
            return false;

        if (SearchText.Length > 0)
        {
            bool Matches(string text) => text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            if (!Matches(model.Name) && !Matches(model.PackageId) && !Matches(model.Description)
                && !Matches(model.Provides) && !Matches(model.Groups))
                return false;
        }
        return true;
    }

    // --------------------------------------------------------------- counters

    private void UpdateCounters()
    {
        int pacman = 0, aur = 0, flatpak = 0, cargo = 0, mise = 0, rustup = 0, fvm = 0;
        int pacmanUpdates = 0, aurUpdates = 0, devUpdates = 0, totalUpdates = 0, selected = 0, orphans = 0;
        int selectedInstall = 0, selectedRemove = 0;

        foreach (var item in _all)
        {
            var model = item.Model;
            // Backend chips describe the rows the current install-state
            // filter can show, so the default view keeps installed counts.
            if (!MatchesInstallState(item))
                continue;
            // Only rows the user can currently see take part in selection
            // counts; hidden backends must not leak into the toolbar.
            var inScope = MatchesScope(item);
            if (model.Selected && inScope)
            {
                ++selected;
                if (model.Installed)
                    ++selectedRemove;
                else
                    ++selectedInstall;
            }
            if (model.Orphan && inScope)
                ++orphans;
            var needsUpdate = model.Status == PackageStatus.NeedsUpdate;
            if (needsUpdate)
                ++totalUpdates;
            switch (model.Backend)
            {
                case Backend.Pacman:
                    ++pacman;
                    if (needsUpdate)
                        ++pacmanUpdates;
                    break;
                case Backend.Aur:
                    ++aur;
                    if (needsUpdate)
                        ++aurUpdates;
                    break;
                case Backend.Flatpak:
                    ++flatpak;
                    break;
                case Backend.Cargo:
                    ++cargo;
                    break;
                case Backend.Mise:
                    ++mise;
                    if (needsUpdate)
                        ++devUpdates;
                    break;
                case Backend.Rustup:
                    ++rustup;
                    if (needsUpdate)
                        ++devUpdates;
                    break;
                case Backend.Fvm:
                    ++fvm;
                    if (needsUpdate)
                        ++devUpdates;
                    break;
            }
        }

        static string ChipText(string label, int total, int updates)
            => updates > 0
                ? Loc.T("%1 (%2, %3 updates)", label, total, updates)
                : Loc.T("%1 (%2)", label, total);

        // The All chip only counts the packages of the active mode.
        SetChipLabel(ChipKind.All, _isDevMode
            ? ChipText(Loc.T("All"), mise + rustup + fvm, devUpdates)
            : ChipText(Loc.T("All"), pacman + aur + flatpak + cargo, totalUpdates - devUpdates));
        SetChipLabel(ChipKind.Pacman, ChipText(Loc.T("Pacman"), pacman, pacmanUpdates));
        SetChipLabel(ChipKind.Aur, ChipText(Loc.T("AUR"), aur, aurUpdates));
        SetChipLabel(ChipKind.Flatpak, ChipText(Loc.T("Flatpak"), flatpak, CountUpdates(Backend.Flatpak)));
        SetChipLabel(ChipKind.Cargo, ChipText(Loc.T("Cargo"), cargo, CountUpdates(Backend.Cargo)));
        SetChipLabel(ChipKind.Mise, ChipText(Loc.T("Mise"), mise, CountUpdates(Backend.Mise)));
        SetChipLabel(ChipKind.Rustup, ChipText(Loc.T("Rustup"), rustup, CountUpdates(Backend.Rustup)));
        SetChipLabel(ChipKind.Fvm, ChipText(Loc.T("FVM"), fvm, CountUpdates(Backend.Fvm)));

        // The updates/orphans filter chips describe the current backend scope.
        var scopedUpdates = _selectedChip == ChipKind.All
            ? (_isDevMode ? devUpdates : totalUpdates - devUpdates)
            : CountUpdates((Backend)_selectedChip);
        _scopedUpdateCount = scopedUpdates;

        UpdatesChipText = Loc.T("Updates (%1)", scopedUpdates);
        UpdateSelectedText = Loc.T("Update Selected (%1)", selected);
        RemoveSelectedText = Loc.T("Remove Selected (%1)", selected);
        InstallSelectedText = Loc.T("Install Selected (%1)", selectedInstall);
        UninstallSelectedText = Loc.T("Uninstall Selected (%1)", selectedRemove);
        HasInstallSelection = selectedInstall > 0;
        HasUninstallSelection = selectedRemove > 0;
        OrphansChipText = Loc.T("Orphans (%1)", orphans);
        HasUpdates = scopedUpdates > 0;
        if (!HasUpdates && ShowUpdatesOnly)
            ShowUpdatesOnly = false;
        HasOrphans = orphans > 0;
        if (!HasOrphans && ShowOrphansOnly)
            ShowOrphansOnly = false;

        var anyUpdates = _all.Count(item =>
            MatchesInstallState(item) && item.Model.Status == PackageStatus.NeedsUpdate);
        HasAnyUpdates = anyUpdates > 0;
        UpdateAllText = Loc.T("Update all (%1)", anyUpdates);

        SelectionText = selected > 0 ? Loc.T("Selected: %1", selected) : string.Empty;
        HasSelection = selected > 0;
    }

    private int CountUpdates(Backend backend)
    {
        var count = 0;
        foreach (var item in _all)
        {
            if (item.Model.Backend == backend && item.Model.Status == PackageStatus.NeedsUpdate)
                ++count;
        }
        return count;
    }

    private void SetChipLabel(ChipKind kind, string label)
    {
        if (_chipByKind.TryGetValue(kind, out var chip))
            chip.Label = label;
    }

    // -------------------------------------------------------------- operations

    private void BeginBusy()
    {
        IsBusy = true;
        ProgressPercent = 0;
        HasProgress = false;
        ProgressText = string.Empty;
        foreach (var chip in Chips)
            chip.IsEnabled = false;
    }

    private void EndBusy()
    {
        IsBusy = false;
        ProgressPercent = 0;
        HasProgress = false;
        ProgressText = string.Empty;
        _operationCts?.Dispose();
        _operationCts = null;
        foreach (var chip in Chips)
            chip.IsEnabled = true;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        BeginBusy();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        CancelRemoteSearch();
        StatusMessage = Loc.T("Loading package lists…");
        try
        {
            var packages = await _manager.LoadPackagesAsync(cts.Token);
            if (cts.IsCancellationRequested)
            {
                StatusMessage = Loc.T("Cancelled");
                return;
            }
            var selection = CaptureSelection();
            _all.Clear();
            foreach (var package in packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                package.Ignored = _ignoredPackages.Contains(package.Name);
                var item = CreatePackageItem(package);
                // Keep the footer selection count and action buttons in sync
                // when a card's checkbox is toggled.
                item.PropertyChanged += OnPackagePropertyChanged;
                _all.Add(item);
            }
            if (_updatesLoaded)
                ApplyUpdatesToModels();
            // A group can disappear between refreshes; drop the filter rather
            // than leaving the list silently empty.
            if (GroupFilter.Length > 0 && !_all.Any(item => item.HasGroup(GroupFilter)))
                GroupFilter = string.Empty;
            RebuildOriginOptions();
            ApplyFilter();
            SyncFilterSelection();
            UpdateCounters();
            var previousNavigation = _suppressNavigation;
            _suppressNavigation = true;
            RestoreSelection(selection);
            _suppressNavigation = previousNavigation;
            RebuildDetail(SelectedPackage);
            ResetNavigation(SelectedPackage);
            StatusMessage = Loc.T("Loaded %1 packages", packages.Count);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("Cancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task SyncDatabasesAsync()
    {
        if (IsBusy)
            return;
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Sync databases"),
            Loc.T("Download fresh repository metadata now? Syncing and then installing only some packages can cause a partial upgrade, so run a full system upgrade afterwards."),
            Loc.T("OK"), Loc.T("Cancel"));
        if (!confirmed)
            return;

        BeginBusy();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        var ok = false;
        try
        {
            StatusMessage = Loc.T("Syncing package databases…");
            var run = await _manager.SyncDatabasesAsync(cts.Token);
            ok = run.Ok;
            StatusMessage = ok
                ? Loc.T("Databases synced")
                : Loc.T("Failed: %1", run.Output.Trim());
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("Cancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            EndBusy();
        }

        if (!ok)
            return;
        await RefreshAsync();
        await CheckUpdatesAsync();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy)
            return;
        BeginBusy();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        StatusMessage = Loc.T("Checking for updates…");
        try
        {
            _updates = await _manager.CheckUpdatesAsync(cts.Token);
            if (cts.IsCancellationRequested)
            {
                StatusMessage = Loc.T("Cancelled");
                return;
            }
            _updatesLoaded = true;
            ApplyUpdatesToModels();
            ApplyFilter();
            SyncFilterSelection();
            UpdateCounters();
            RebuildDetail(SelectedPackage);
            StatusMessage = HasUpdates
                ? Loc.T("%1 updates available", _scopedUpdateCount)
                : Loc.T("All packages are up to date");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("Cancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private void ApplyUpdatesToModels()
    {
        // Drop markers from the previous check first. Otherwise a refresh
        // re-applies the old map and a later check that returns no entries
        // leaves packages looking updatable until another manual refresh.
        foreach (var item in _all)
        {
            if (item.Model.Status != PackageStatus.NeedsUpdate)
                continue;
            item.Model.Status = PackageStatus.Idle;
            item.Model.NewVersion = string.Empty;
            item.Refresh();
        }

        foreach (var (backend, entries) in _updates)
        {
            foreach (var (name, newVersion) in entries)
            {
                foreach (var item in _all)
                {
                    if (item.Model.Backend != backend || item.Model.Ignored)
                        continue;
                    if (item.Model.Name != name && item.Model.PackageId != name)
                        continue;
                    item.Model.Status = PackageStatus.NeedsUpdate;
                    if (newVersion.Length > 0)
                        item.Model.NewVersion = newVersion;
                    item.Refresh();
                    break;
                }
            }
        }
    }

    private (Backend Backend, string Id)? CaptureSelection()
        => SelectedPackage is { } item
            ? (item.Model.Backend, item.Model.EffectiveId)
            : null;

    private void RestoreSelection((Backend Backend, string Id)? key)
    {
        if (key is null)
            return;

        SelectedPackage = Packages.FirstOrDefault(item =>
            item.Model.Backend == key.Value.Backend
            && item.Model.EffectiveId == key.Value.Id);
    }

    /// <summary>Drops update entries for packages that a successful operation touched.</summary>
    private void ForgetUpdates(IReadOnlyList<Package> packages)
    {
        foreach (var package in packages)
        {
            if (!_updates.TryGetValue(package.Backend, out var entries))
                continue;
            entries.Remove(package.Name);
            entries.Remove(package.EffectiveId);
        }
    }

    // ------------------------------------------------------------- navigation

    private PackageItemViewModel? FindPackage(string name)
    {
        foreach (var item in _all)
        {
            if (string.Equals(item.Model.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Model.PackageId, name, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private void PushNavigation(PackageItemViewModel item)
    {
        if (_navigationIndex >= 0 && _navigationIndex < _navigation.Count
            && _navigation[_navigationIndex] == item)
        {
            return;
        }

        // Selecting a new package after going back discards the forward stack.
        if (_navigationIndex + 1 < _navigation.Count)
            _navigation.RemoveRange(
                _navigationIndex + 1, _navigation.Count - _navigationIndex - 1);

        _navigation.Add(item);
        _navigationIndex = _navigation.Count - 1;
        if (_navigation.Count > 50)
        {
            _navigation.RemoveAt(0);
            --_navigationIndex;
        }
        NotifyNavigationState();
    }

    private void ResetNavigation(PackageItemViewModel? current)
    {
        _navigation.Clear();
        _navigationIndex = -1;
        if (current is not null)
        {
            _navigation.Add(current);
            _navigationIndex = 0;
        }
        NotifyNavigationState();
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_navigationIndex <= 0)
            return;
        --_navigationIndex;
        ShowPackage(_navigation[_navigationIndex], record: false);
        NotifyNavigationState();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_navigationIndex < 0 || _navigationIndex + 1 >= _navigation.Count)
            return;
        ++_navigationIndex;
        ShowPackage(_navigation[_navigationIndex], record: false);
        NotifyNavigationState();
    }

    private void NavigateToDependency(string name)
    {
        var target = FindPackage(name);
        if (target is not null)
            ShowPackage(target, record: true);
    }

    private void ShowPackage(PackageItemViewModel item, bool record)
    {
        EnsurePackageVisible(item);
        var previous = _suppressNavigation;
        _suppressNavigation = true;
        SelectedPackage = item;
        _suppressNavigation = previous;
        if (record)
            PushNavigation(item);
        NotifyNavigationState();
    }

    /// <summary>
    /// Clears view filters that would hide the target so dependency and
    /// history navigation always lands on a visible list row.
    /// </summary>
    private void EnsurePackageVisible(PackageItemViewModel item)
    {
        var isDev = BackendInfo.IsDev(item.Model.Backend);
        if (isDev != _isDevMode)
            SetDevMode(isDev);

        if (!MatchesInstallState(item))
            SetInstallState(item.Model.Installed
                ? InstallStateFilter.Installed
                : InstallStateFilter.NotInstalled);
        if (!MatchesOrigin(item))
            SelectedOrigin = _allOriginsLabel;

        if (SearchText.Length > 0)
            SearchText = string.Empty;
        if (ShowUpdatesOnly)
            ShowUpdatesOnly = false;
        if (ShowOrphansOnly)
            ShowOrphansOnly = false;
        if (GroupFilter.Length > 0)
            GroupFilter = string.Empty;

        var chip = (ChipKind)item.Model.Backend;
        if (_selectedChip != ChipKind.All && _selectedChip != chip)
            SelectChip(chip);
    }

    /// <summary>
    /// Shows the resolved transaction first, then asks for confirmation. If
    /// pacman cannot plan the transaction, the error is shown but the user may
    /// still continue because some backends cannot be previewed.
    /// </summary>
    private async Task<bool> ConfirmOperationAsync(
        PackageOperation operation,
        IReadOnlyList<Package> packages,
        string title,
        string question)
    {
        var preview = await _manager.PreviewTransactionAsync(
            operation, packages, CancellationToken.None);
        var details = FormatPreview(preview);

        var summary = operation switch
        {
            PackageOperation.Install => Loc.T(
                "Install %1 packages · download %2 · installed %3",
                preview.Items.Count,
                FormatBytes(preview.Items.Sum(item => item.DownloadBytes)),
                FormatBytes(preview.Items.Sum(item => item.InstalledBytes))),
            PackageOperation.Remove => Loc.T(
                "Remove %1 packages · freed %2",
                preview.Items.Count,
                FormatBytes(preview.Items.Sum(item => item.InstalledBytes))),
            _ => Loc.T("Upgrade %1 packages", preview.Items.Count),
        };
        if (!preview.Ok)
            summary = Loc.T("The transaction preview could not be completed; the exact changes are unknown.");

        return await _dialogs.ConfirmDetailsAsync(
            title, question + "\n\n" + summary, details,
            Loc.T("OK"), Loc.T("Cancel"));
    }

    private static string FormatPreview(PackageManager.TransactionPreview preview)
    {
        var lines = new List<string>();
        if (preview.Commands.Count > 0)
        {
            lines.Add(Loc.T("Commands:"));
            foreach (var command in preview.Commands)
                lines.Add("  " + command);
        }
        if (preview.Items.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);
            lines.Add(Loc.T("Packages:"));
            foreach (var item in preview.Items)
            {
                var version = item.NewVersion.Length == 0
                    ? item.OldVersion
                    : item.OldVersion.Length == 0
                        ? item.NewVersion
                        : item.OldVersion + " -> " + item.NewVersion;
                var sizes = new List<string>();
                if (item.DownloadBytes > 0)
                    sizes.Add("↓ " + FormatBytes(item.DownloadBytes));
                if (item.InstalledBytes > 0)
                    sizes.Add("± " + FormatBytes(item.InstalledBytes));
                lines.Add($"  {item.Action,-7} {item.Name,-32} {version,-24} " +
                          string.Join("  ", sizes));
            }
        }
        if (preview.Notes.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("Note:") + " " + preview.Notes);
        }
        if (preview.Error.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("Preview error:"));
            lines.Add(preview.Error);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes)
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

    private PackageItemViewModel CreatePackageItem(Package package)
        => new(package, FilterByGroupCommand)
        {
            InstallCommand = InstallPackageCommand,
            UninstallCommand = UninstallPackageCommand,
            UpdateCommand = UpdatePackageCommand,
            FilesCommand = ShowPackageFilesCommand,
            MakeExplicitCommand = MakeExplicitCommand,
            ToggleIgnoreCommand = ToggleIgnoreCommand,
            CopyNameCommand = CopyPackageNameCommand,
            OpenAurCommand = OpenAurPageCommand,
        };

    [RelayCommand]
    private async Task CopyPackageNameAsync(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(item.Model.EffectiveId);
        StatusMessage = Loc.T("Package name copied to clipboard");
    }

    [RelayCommand]
    private static void OpenAurPage(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        Loc.OpenUrl("https://aur.archlinux.org/packages/" + Uri.EscapeDataString(item.Model.Name));
    }

    private async Task RunOperationAsync(PackageOperation operation, IReadOnlyList<Package> packages)
    {
        if (packages.Count == 0 || IsBusy)
            return;
        BeginBusy();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        var ok = true;
        var cancelled = false;
        var message = string.Empty;
        void OnFinished(bool success, string text)
        {
            ok = success;
            message = text;
        }

        _manager.OperationFinished += OnFinished;
        try
        {
            StatusMessage = operation switch
            {
                PackageOperation.Install => Loc.T("Installing selected packages…"),
                PackageOperation.Remove => Loc.T("Removing selected packages…"),
                _ => Loc.T("Updating packages…"),
            };
            await _manager.RunOperationAsync(operation, packages, cts.Token);
            if (cts.IsCancellationRequested)
            {
                cancelled = true;
                StatusMessage = Loc.T("Cancelled");
            }
            else if (!ok && message.Length > 0)
            {
                StatusMessage = message;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            StatusMessage = Loc.T("Cancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            _manager.OperationFinished -= OnFinished;
            EndBusy();
        }

        if (cancelled)
            return;

        // The just-updated packages must not be re-marked from the previous
        // update map while the full list reloads; the check below refreshes
        // the remaining entries.
        if (ok)
            ForgetUpdates(packages);

        await RefreshAsync();
        await CheckUpdatesAsync();
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in Packages)
        {
            if (item.ShowSelection)
                item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelected()
    {
        foreach (var item in _all)
            item.IsSelected = false;
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var packages = _all
            .Where(item => MatchesScope(item) && item.Model.Selected && !item.Model.Installed)
            .Select(item => item.Model)
            .ToList();
        if (packages.Count == 0)
            return;
        var names = string.Join(", ", packages.Select(package => package.Name));
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Install, packages, Loc.T("Install Packages"),
            Loc.Tn("Install the following package?", "Install the following %1 packages?",
                packages.Count, packages.Count) + "\n\n" + names);
        if (confirmed)
            await RunOperationAsync(PackageOperation.Install, packages);
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var packages = _all
            .Where(item => MatchesScope(item) && item.Model.Selected && item.Model.Installed)
            .Select(item => item.Model)
            .ToList();
        if (packages.Count == 0)
            return;
        var names = string.Join(", ", packages.Select(package => package.Name));
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Remove, packages, Loc.T("Uninstall Packages"),
            Loc.Tn("Uninstall the following package?", "Uninstall the following %1 packages?",
                packages.Count, packages.Count) + "\n\n" + names);
        if (confirmed)
            await RunOperationAsync(PackageOperation.Remove, packages);
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        var packages = _all
            .Where(item => MatchesScope(item)
                && item.Model.Selected
                && item.Model.Status == PackageStatus.NeedsUpdate)
            .Select(item => item.Model)
            .ToList();
        if (packages.Count == 0)
            return;
        var names = string.Join(", ", packages.Select(package => package.Name));
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Update, packages, Loc.T("Update Packages"),
            Loc.Tn("Update the following package?", "Update the following %1 packages?",
                packages.Count, packages.Count) + "\n\n" + names);
        if (!confirmed)
            return;
        await RunOperationAsync(PackageOperation.Update, packages);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var packages = _all
            .Where(item => MatchesInstallState(item) && item.Model.Status == PackageStatus.NeedsUpdate)
            .Select(item => item.Model)
            .ToList();
        if (packages.Count == 0)
            return;
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Update, packages, Loc.T("Update all backends"),
            Loc.Tn("Update the package below across every enabled backend?",
                "Update %1 packages across every enabled backend?", packages.Count, packages.Count));
        if (confirmed)
            await RunOperationAsync(PackageOperation.Update, packages);
    }

    [RelayCommand]
    private async Task RemoveSelectedOrphansAsync()
    {
        var packages = _all
            .Where(item => MatchesScope(item) && item.Model.Selected && item.Model.Orphan)
            .Select(item => item.Model)
            .ToList();
        if (packages.Count == 0)
            return;
        var names = string.Join(", ", packages.Select(package => package.Name));
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Remove, packages, Loc.T("Remove Packages"),
            Loc.Tn("Remove the following orphan package?", "Remove the following %1 orphan packages?",
                packages.Count, packages.Count) + "\n\n" + names);
        if (!confirmed)
            return;
        await RunOperationAsync(PackageOperation.Remove, packages);
    }

    [RelayCommand]
    private async Task InstallPackageAsync(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Install, [item.Model], Loc.T("Install Package"),
            Loc.T("Install %1?", item.Name));
        if (confirmed)
            await RunOperationAsync(PackageOperation.Install, [item.Model]);
    }

    [RelayCommand]
    private async Task UninstallPackageAsync(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Remove, [item.Model], Loc.T("Uninstall Package"),
            Loc.T("Uninstall %1?", item.Name));
        if (confirmed)
            await RunOperationAsync(PackageOperation.Remove, [item.Model]);
    }

    [RelayCommand]
    private async Task UpdatePackageAsync(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        var note = item.Model.Backend == Backend.Pacman
            ? Loc.T("Pacman updates always upgrade the whole system first (pacman -Syu) to avoid partial updates.")
            : string.Empty;
        var message = Loc.T("Update %1?", item.Name)
            + (note.Length == 0 ? string.Empty : "\n\n" + note);
        var confirmed = await ConfirmOperationAsync(
            PackageOperation.Update, [item.Model], Loc.T("Update Package"), message);
        if (confirmed)
            await RunOperationAsync(PackageOperation.Update, [item.Model]);
    }

    [RelayCommand]
    private async Task ShowPackageFilesAsync(PackageItemViewModel? item)
    {
        if (item is null || IsBusy)
            return;
        BeginBusy();
        try
        {
            StatusMessage = Loc.T("Listing files for %1…", item.Name);
            var result = await _manager.GetPackageFilesAsync(item.Model, CancellationToken.None);
            var viewModel = new PackageFilesViewModel(item.Name, result);
            await _dialogs.ShowPackageFilesAsync(viewModel);
            StatusMessage = Loc.T("Ready");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task MakeExplicitAsync(PackageItemViewModel? item)
    {
        if (item is null || IsBusy)
            return;
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Mark Explicitly Installed"),
            Loc.T("Mark %1 as explicitly installed?", item.Name),
            Loc.T("OK"), Loc.T("Cancel"));
        if (!confirmed)
            return;

        var ok = false;
        BeginBusy();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        try
        {
            StatusMessage = Loc.T("Marking %1 as explicitly installed…", item.Name);
            var run = await _manager.MarkExplicitAsync(item.Model, cts.Token);
            ok = run.Ok;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("Cancelled");
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            EndBusy();
        }

        await RefreshAsync();
        StatusMessage = ok
            ? Loc.T("%1 is now explicitly installed", item.Name)
            : Loc.T("Failed: %1", item.Name);
    }

    // ------------------------------------------------------------------- log

    private void OnCommandStarted(CommandStarted activity)
    {
        AddLog(() =>
        {
            var item = LogItemViewModel.CreateCommand(activity.Id, activity.CommandLine, activity.Operation);
            item.ShowDetails = IsLogDetailed;
            _commandsByActivity[activity.Id] = item;
            LogItems.Add(item);
            TrimLog();
            _activeCommands[activity.Id] = activity.CommandLine;
            UpdateCurrentCommand();
            _logDirty = true;
        });
    }

    private void OnCommandOutput(CommandOutput activity)
    {
        AddLog(() =>
        {
            if (!_commandsByActivity.TryGetValue(activity.Id, out var item))
                return;
            item.Append(activity.Text);
            if (IsBusy)
                UpdateProgress(activity.Text);
            _logDirty = true;
        });
    }

    /// <summary>Uses the last percentage in a command batch for the footer bar.</summary>
    private void UpdateProgress(string text)
    {
        var last = -1;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"(\d{1,3})\s*%"))
        {
            if (int.TryParse(match.Groups[1].Value, out var value))
                last = Math.Clamp(value, 0, 100);
        }
        if (last < 0)
            return;
        HasProgress = true;
        ProgressPercent = last;
        ProgressText = last + "%";
    }

    private void OnCommandFinished(CommandFinished activity)
    {
        AddLog(() =>
        {
            if (_commandsByActivity.Remove(activity.Id, out var item))
                item.Finish(
                    activity.Ok, activity.ExitCode, activity.Reason, activity.ExpectedNoteKey);
            _activeCommands.Remove(activity.Id);
            UpdateCurrentCommand();
            _logDirty = true;
        });
    }

    private void UpdateCurrentCommand()
    {
        CurrentCommand = _activeCommands.Count == 0
            ? string.Empty
            : _activeCommands.OrderByDescending(pair => pair.Key).First().Value;
    }

    /// <summary>Queues a UI-thread log mutation; concurrent commands stay separated.</summary>
    private void AddLog(Action action)
    {
        _pendingLog.Enqueue(action);
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(FlushLog, DispatcherPriority.Background);
    }

    private void FlushLog()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        while (_pendingLog.TryDequeue(out var action))
            action();
        if (!_logDirty)
            return;
        _logDirty = false;
        LogUpdated?.Invoke();
    }

    /// <summary>Drops the oldest finished blocks; running commands are never removed.</summary>
    private void TrimLog()
    {
        while (LogItems.Count > MaxLogItems)
        {
            var victim = LogItems.FirstOrDefault(item => !item.IsRunning);
            if (victim is null)
                break;
            LogItems.Remove(victim);
        }
    }

    [RelayCommand]
    private void ToggleLog()
    {
        IsLogExpanded = !IsLogExpanded;
        SaveLogPanelState();
    }

    [RelayCommand]
    private void ClearLog()
    {
        // Keep running commands so their live output can still be followed.
        for (var i = LogItems.Count - 1; i >= 0; --i)
        {
            if (!LogItems[i].IsRunning)
                LogItems.RemoveAt(i);
        }
        // Pending events are kept: dropping a finish event would leave a
        // running block stuck in "Running…" forever.
        LogContext = null;
        LogUpdated?.Invoke();
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            LogItems.Select(item => item.ToText()));
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
        StatusMessage = Loc.T("Log copied to clipboard");
    }

    /// <summary>Drags the log panel's top edge; positive delta grows towards the top.</summary>
    public void ResizeLogPanel(double delta, double availableHeight)
    {
        var maximum = Math.Clamp(availableHeight - 320, MinLogPanelHeight, MaxLogPanelHeight);
        LogPanelHeight = Math.Clamp(LogPanelHeight + delta, MinLogPanelHeight, maximum);
    }

    public void SaveLogPanelState()
    {
        _config.Set("General", "LogPanelHeight", (int)Math.Round(LogPanelHeight));
        _config.Set("General", "LogPanelExpanded", IsLogExpanded);
        _config.Set("General", "LogDetails", IsLogDetailed);
        _config.Save();
    }

    // ---------------------------------------------------------------- dialogs

    private async Task<string?> AskSudoPasswordAsync(Func<string, Task<bool>>? validate)
    {
        var title = Loc.T("Authentication required");
        var message = Loc.T("Enter your password to run pacman with administrator privileges.");
        if (Dispatcher.UIThread.CheckAccess())
            return await _dialogs.AskPasswordAsync(title, message, validate);
        return await Dispatcher.UIThread.InvokeAsync(
            () => _dialogs.AskPasswordAsync(title, message, validate));
    }

    [RelayCommand]
    private async Task SettingsAsync()
    {
        var viewModel = new SettingsViewModel(_config, _proxy);
        if (!await _dialogs.ShowSettingsAsync(viewModel))
            return;

        var newLanguage = viewModel.SelectedLanguage?.Value ?? "system";
        if (!string.Equals(newLanguage, _languageSetting, StringComparison.Ordinal))
        {
            _languageSetting = newLanguage;
            ApplyLanguage(newLanguage);
        }

        var backendsChanged = viewModel.BackendsChanged;
        LoadBackendSettings();
        LoadIgnoredPackages();
        ApplyBackendSettings();
        RefreshBackendHealth();
        RefreshModeUi();
        UpdateCounters();
        StatusMessage = _proxy.Url.Length == 0
            ? Loc.T("Proxy disabled")
            : Loc.T("Proxy: %1", _proxy.Url);
        if (backendsChanged)
        {
            await RefreshAsync();
            if (_checkUpdatesOnStart)
                await CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    private async Task SnapshotsAsync()
    {
        var viewModel = new SnapshotsViewModel(_manager);
        await _dialogs.ShowSnapshotsAsync(viewModel);
    }

    [RelayCommand]
    private async Task CacheCleanupAsync()
    {
        var viewModel = new CacheViewModel(_manager);
        await _dialogs.ShowCacheCleanupAsync(viewModel);
    }

    [RelayCommand]
    private async Task DiagnosticsAsync()
    {
        var viewModel = new DiagnosticsViewModel(_manager.GetDiagnostics());
        await _dialogs.ShowDiagnosticsAsync(viewModel);
    }

    [RelayCommand]
    private async Task HistoryAsync()
    {
        var viewModel = new HistoryViewModel();
        await _dialogs.ShowHistoryAsync(viewModel);
    }

    /// <summary>Updates the toolbar warning from the enabled backends' tools.</summary>
    private void RefreshBackendHealth()
    {
        var missing = _manager.GetDiagnostics()
            .Where(item => item.Required && !item.Available)
            .Select(item => item.Tool)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        HasBackendIssues = missing.Count > 0;
        BackendIssueText = missing.Count == 0
            ? string.Empty
            : Loc.T("Missing required tools: %1", string.Join(", ", missing));
    }

    [RelayCommand]
    private async Task AboutAsync()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var buildTime = assembly.Location.Length > 0 && File.Exists(assembly.Location)
            ? File.GetLastWriteTime(assembly.Location).ToString("yyyy-MM-dd HH:mm")
            : "—";
        var text = $"{Loc.T("QuickPackage")} 0.4.0\n"
            + Loc.T("A package manager for pacman, Flatpak, cargo and AUR")
            + "\n\n"
            + Loc.T("Build: %1", buildTime) + "\n"
            + Loc.T("UI font: %1", FontStrategy.PrimaryFamilyName) + "\n"
            + Loc.T("Text rendering: %1", FontStrategy.DescribeTextOptions())
            + "\n\n" + Loc.T("© 2026 QuickPackage contributors");
        await _dialogs.ShowInfoAsync(Loc.T("About"), text);
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsBusy)
            _operationCts?.Cancel();
    }

    [RelayCommand]
    private static void Quit()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    // ---------------------------------------------------------------- details

    private void RebuildDetail(PackageItemViewModel? item)
    {
        DetailFields.Clear();
        ClearDependencies();
        if (item is null)
        {
            IsPackageSelected = false;
            DetailDescription = string.Empty;
            HasDetailDescription = false;
            CanInstallSelectedPackage = false;
            CanUpdateSelectedPackage = false;
            CanUninstallSelectedPackage = false;
            IsInstalledSelectedPackage = false;
            CanMakeExplicit = false;
            CanShowPackageFiles = false;
            CanToggleIgnore = false;
            IgnoreMenuText = string.Empty;
            return;
        }

        var model = item.Model;
        IsPackageSelected = true;

        void Add(string label, string value)
        {
            if (value.Length > 0)
                DetailFields.Add(DetailFieldViewModel.Plain(label, value));
        }

        if (model.EffectiveId != model.Name)
            Add(Loc.T("Id:"), model.EffectiveId);
        Add(Loc.T("Version:"), model.NewVersion.Length == 0
            ? model.Version
            : (model.Version.Length == 0 ? "—" : model.Version) + " → " + model.NewVersion);
        Add(Loc.T("Origin:"), model.Origin);
        if (!model.Installed)
            Add(Loc.T("Status:"), Loc.T("Not installed"));
        Add(Loc.T("Size:"), model.Size);
        Add(Loc.T("Download size:"), model.DownloadSize);
        Add(Loc.T("Install date:"), model.InstallDate);
        Add(Loc.T("Build date:"), model.BuildDate);
        if (model.Installed)
            Add(Loc.T("Install reason:"), model.Backend is Backend.Pacman or Backend.Aur
                ? (model.Explicit ? Loc.T("Explicitly installed") : Loc.T("Installed as a dependency"))
                : model.InstallReason);
        Add(Loc.T("Architecture:"), model.Architecture);
        if (item.GroupChips.Count > 0)
            DetailFields.Add(DetailFieldViewModel.ChipList(Loc.T("Groups:"), item.GroupChips));
        Add(Loc.T("Packager:"), model.Packager);
        if (model.Url.Length > 0)
            DetailFields.Add(DetailFieldViewModel.Link(Loc.T("URL:"), model.Url));
        if (model.Backend == Backend.Aur)
        {
            DetailFields.Add(DetailFieldViewModel.Link(
                Loc.T("AUR page:"), "https://aur.archlinux.org/packages/" + Uri.EscapeDataString(model.Name)));
            if (model.RemoteSearch)
            {
                DetailFields.Add(DetailFieldViewModel.Plain(Loc.T("Votes:"), model.Votes.ToString()));
                DetailFields.Add(DetailFieldViewModel.Plain(
                    Loc.T("Popularity:"), model.Popularity.ToString("0.##", CultureInfo.InvariantCulture)));
                Add(Loc.T("Maintainer:"), model.Maintainer);
                if (model.OutOfDate)
                    DetailFields.Add(DetailFieldViewModel.Plain(
                        Loc.T("Status:"), Loc.T("Flagged out of date")));
            }
        }
        Add(Loc.T("Licenses:"), model.Licenses);
        Add(Loc.T("Provides:"), model.Provides);
        Add(Loc.T("Optional deps:"), model.OptionalDeps);
        Add(Loc.T("Conflicts:"), model.Conflicts);
        Add(Loc.T("Binaries:"), model.Binaries);
        UpdateDependencies(model);

        var statusText = model.Status == PackageStatus.Installed ? string.Empty : BackendInfo.StatusText(model.Status);
        if (statusText.Length > 0)
            DetailFields.Add(DetailFieldViewModel.Plain(Loc.T("Status:"), statusText));

        DetailDescription = model.Description;
        HasDetailDescription = model.Description.Length > 0;
        CanInstallSelectedPackage = !model.Installed;
        CanUpdateSelectedPackage = model.Status == PackageStatus.NeedsUpdate;
        CanUninstallSelectedPackage = model.Installed;
        IsInstalledSelectedPackage = model.Installed;
        CanMakeExplicit = model.Installed
            && model.Backend is Backend.Pacman or Backend.Aur
            && !model.Explicit;
        CanShowPackageFiles = model.Installed
            && model.Backend is Backend.Pacman or Backend.Aur;
        CanToggleIgnore = model.Installed;
        IgnoreMenuText = model.Ignored
            ? Loc.T("Cancel ignored updates")
            : Loc.T("Ignore updates");

        if (model.Backend == Backend.Cargo && model.Description.Length == 0)
            _ = FetchCargoDescriptionAsync(model);
    }

    private void ClearDependencies()
    {
        ForwardDependencies.Clear();
        ReverseDependencies.Clear();
        HasForwardDependencies = false;
        HasReverseDependencies = false;
    }

    private void UpdateDependencies(Package model)
    {
        ClearDependencies();
        if (model.Backend is not (Backend.Pacman or Backend.Aur))
            return;

        AddDependencies(ForwardDependencies, model.Depends);
        AddDependencies(ReverseDependencies, model.RequiredBy);
        HasForwardDependencies = ForwardDependencies.Count > 0;
        HasReverseDependencies = ReverseDependencies.Count > 0;
    }

    private void AddDependencies(
        ObservableCollection<DependencyViewModel> target, string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // "foo>=1.2" -> "foo"
            var cut = token.IndexOfAny(['<', '>', '=']);
            var name = (cut >= 0 ? token[..cut] : token).Trim();
            if (name.Length == 0 || name.Equals("None", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(name))
                continue;

            target.Add(new DependencyViewModel
            {
                Name = name,
                IsAvailable = FindPackage(name) is not null,
                NavigateCommand = new RelayCommand(() => NavigateToDependency(name)),
            });
        }
    }

    private async Task FetchCargoDescriptionAsync(Package package)
    {
        if (_descriptionCache.TryGetValue(package.Name, out var cached))
        {
            if (SelectedPackage?.Model == package)
            {
                DetailDescription = cached;
                HasDetailDescription = cached.Length > 0;
            }
            return;
        }
        try
        {
            using var handler = new HttpClientHandler();
            var proxyUrl = _proxy.Url;
            if (proxyUrl.Length > 0 && _proxy.IsEnabledFor(Backend.Cargo))
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxyUrl);
            }
            else
            {
                handler.UseProxy = false;
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QuickPackage/1.0 (package manager)");
            var response = await client.GetAsync($"https://crates.io/api/v1/crates/{Uri.EscapeDataString(package.Name)}");
            if (!response.IsSuccessStatusCode)
                return;
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var description = document.RootElement.GetProperty("crate").GetProperty("description").GetString()
                ?? string.Empty;
            _descriptionCache[package.Name] = description;
            if (SelectedPackage?.Model == package)
            {
                DetailDescription = description;
                HasDetailDescription = description.Length > 0;
            }
        }
        catch
        {
            // Descriptions are best-effort.
        }
    }

    // --------------------------------------------------------------- settings

    /// <summary>Switches the UI language immediately, without a restart.</summary>
    private void ApplyLanguage(string setting)
    {
        Loc.SetLanguage(setting is "system" or "" ? Loc.DetectSystemLanguage() : setting);
        var culture = CultureInfo.GetCultureInfo(Loc.IsChinese ? "zh-CN" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        L.NotifyAll();
        OnPropertyChanged(nameof(GroupFilterText));
        RebuildOriginOptions();
        RefreshBackendHealth();
        foreach (var item in LogItems)
            item.RefreshLocalization();
        UpdateCounters();
        RebuildDetail(SelectedPackage);
        StatusMessage = Loc.T("Ready");
    }

    private void LoadBackendSettings()
    {
        _pacmanEnabled = _config.GetBool("General", "EnablePacman", true);
        _aurEnabled = _config.GetBool("General", "EnableAur", true);
        _flatpakEnabled = _config.GetBool("General", "EnableFlatpak", true);
        _cargoEnabled = _config.GetBool("General", "EnableCargo", true);
        _miseEnabled = _config.GetBool("General", "EnableMise", false);
        _rustupEnabled = _config.GetBool("General", "EnableRustup", false);
        _fvmEnabled = _config.GetBool("General", "EnableFvm", false);
    }

    private void LoadIgnoredPackages()
    {
        _ignoredPackages.Clear();
        foreach (var name in _config.GetString("General", "IgnoredPackages")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _ignoredPackages.Add(name);
        }
    }

    private void SaveIgnoredPackages()
    {
        _config.Set("General", "IgnoredPackages",
            string.Join(',', _ignoredPackages.OrderBy(name => name, StringComparer.Ordinal)));
        _manager.SetIgnoredPackages(_ignoredPackages);
        _config.Save();
    }

    [RelayCommand]
    private void ToggleIgnore(PackageItemViewModel? item)
    {
        if (item is null || !item.Model.Installed)
            return;
        if (!_ignoredPackages.Remove(item.Model.Name))
            _ignoredPackages.Add(item.Model.Name);
        item.Model.Ignored = _ignoredPackages.Contains(item.Model.Name);
        SaveIgnoredPackages();
        ApplyUpdatesToModels();
        item.Refresh();
        ApplyFilter();
        UpdateCounters();
        RebuildDetail(item);
        StatusMessage = item.Model.Ignored
            ? Loc.T("%1 will no longer be upgraded", item.Name)
            : Loc.T("%1 will be upgraded again", item.Name);
    }

    private void ApplyBackendSettings()
    {
        _manager.SetBackendEnabled(Backend.Pacman, _pacmanEnabled);
        _manager.SetBackendEnabled(Backend.Aur, _aurEnabled);
        _manager.SetAurHelper(_config.GetString("General", "AurHelper"));
        _manager.SetBackendEnabled(Backend.Flatpak, _flatpakEnabled);
        _manager.SetBackendEnabled(Backend.Cargo, _cargoEnabled);
        _manager.SetBackendEnabled(Backend.Mise, _miseEnabled);
        _manager.SetBackendEnabled(Backend.Rustup, _rustupEnabled);
        _manager.SetBackendEnabled(Backend.Fvm, _fvmEnabled);
        _manager.SetProxy(_proxy);
        _manager.SetQueryTimeout(_config.GetInt("General", "CommandTimeout", 30));
        _manager.SetDownloadTimeout(_config.GetInt("General", "DownloadTimeout", 3600));
        _manager.SetNetworkRetries(_config.GetInt("General", "NetworkRetries", 2));
        _manager.SetIgnoredPackages(_ignoredPackages);
        _online.SetProxy(_proxy);
    }
}
