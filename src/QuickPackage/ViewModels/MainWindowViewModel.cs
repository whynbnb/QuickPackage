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
    private readonly List<PackageItemViewModel> _all = [];
    private readonly Dictionary<ChipKind, ChipViewModel> _chipByKind = [];
    private readonly Dictionary<string, string> _descriptionCache = new(StringComparer.Ordinal);
    private readonly bool _checkUpdatesOnStart;
    private readonly ConcurrentQueue<(LogEntryKind Kind, string Text)> _pendingLog = new();
    private readonly Dictionary<long, string> _activeCommands = [];
    private string _languageSetting = "system";
    private int _logFlushScheduled;

    private const int MaxLogEntries = 500;
    private const double MinLogPanelHeight = 90;
    private const double MaxLogPanelHeight = 600;

    private Dictionary<Backend, Dictionary<string, string>> _updates = [];
    private bool _updatesLoaded;
    private bool _suppressChipEvents;
    private bool _isDevMode;
    private ChipKind _selectedChip = ChipKind.All;
    private CancellationTokenSource? _operationCts;

    private bool _pacmanEnabled = true;
    private bool _flatpakEnabled = true;
    private bool _cargoEnabled = true;
    private bool _miseEnabled;
    private bool _rustupEnabled;
    private bool _fvmEnabled;

    public MainWindowViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _config = Config.Load();
        _proxy.Load(_config);

        _checkUpdatesOnStart = _config.GetBool("General", "CheckUpdatesOnStart", true);
        _languageSetting = _config.GetString("General", "Language", "system");
        LoadBackendSettings();
        ApplyBackendSettings();

        _isLogExpanded = _config.GetBool("General", "LogPanelExpanded", true);
        _logPanelHeight = Math.Clamp(
            _config.GetInt("General", "LogPanelHeight", 170), (int)MinLogPanelHeight, (int)MaxLogPanelHeight);

        _manager.Message += message =>
        {
            AddLog(LogEntryKind.Info, message);
            Dispatcher.UIThread.Post(() => StatusMessage = message);
        };
        CommandActivity.Started += OnCommandStarted;
        CommandActivity.Output += OnCommandOutput;
        CommandActivity.Finished += OnCommandFinished;

        L = new LocStrings();
        StatusMessage = Loc.T("Ready");
        BuildChips();
        RefreshModeUi();
        UpdateCounters();
    }

    public LocStrings L { get; }

    public ObservableCollection<PackageItemViewModel> Packages { get; } = [];

    public ObservableCollection<ChipViewModel> Chips { get; } = [];

    public ObservableCollection<DetailFieldViewModel> DetailFields { get; } = [];

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showUpdatesOnly;

    [ObservableProperty]
    private PackageItemViewModel? _selectedPackage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private string _updatesChipText = string.Empty;

    [ObservableProperty]
    private string _updatesButtonText = string.Empty;

    [ObservableProperty]
    private bool _hasUpdates;

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
    private bool _isLogExpanded;

    [ObservableProperty]
    private double _logPanelHeight = 170;

    [ObservableProperty]
    private string _currentCommand = string.Empty;

    public bool IsNotBusy => !IsBusy;

    public bool HasCurrentCommand => CurrentCommand.Length > 0;

    public string LogChevron => IsLogExpanded ? "▾" : "▸";

    public bool CanInstallRemove => HasSelection && !IsBusy;

    public bool HasDevBackends => _miseEnabled || _rustupEnabled || _fvmEnabled;

    public bool ShowBackendHint =>
        _isDevMode
            ? !HasDevBackends
            : !_pacmanEnabled && !_flatpakEnabled && !_cargoEnabled;

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

    private bool HasSystemBackend => _pacmanEnabled || _flatpakEnabled || _cargoEnabled;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowUpdatesOnlyChanged(bool value) => ApplyFilter();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanInstallRemove));
    }

    partial void OnHasSelectionChanged(bool value) => OnPropertyChanged(nameof(CanInstallRemove));

    partial void OnSelectedPackageChanged(PackageItemViewModel? value) => RebuildDetail(value);

    partial void OnCurrentCommandChanged(string value) => OnPropertyChanged(nameof(HasCurrentCommand));

    partial void OnIsLogExpandedChanged(bool value) => OnPropertyChanged(nameof(LogChevron));

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
        if (dev)
            SelectChip(ChipKind.All);
        RefreshModeUi(adjustMode: false);
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
        SetChipVisible(ChipKind.Aur, !_isDevMode && _pacmanEnabled);
        SetChipVisible(ChipKind.Flatpak, !_isDevMode && _flatpakEnabled);
        SetChipVisible(ChipKind.Cargo, !_isDevMode && _cargoEnabled);
        SetChipVisible(ChipKind.Mise, _isDevMode && _miseEnabled);
        SetChipVisible(ChipKind.Rustup, _isDevMode && _rustupEnabled);
        SetChipVisible(ChipKind.Fvm, _isDevMode && _fvmEnabled);

        if (!IsChipVisible(_selectedChip))
            SelectChip(ChipKind.All);
        OnPropertyChanged(nameof(ShowEmpty));
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
        ApplyFilter();
    }

    // --------------------------------------------------------------- filtering

    private void ApplyFilter()
    {
        var filtered = new List<PackageItemViewModel>(_all.Count);
        foreach (var item in _all)
        {
            if (Accepts(item))
                filtered.Add(item);
        }
        Packages.Clear();
        foreach (var item in filtered)
            Packages.Add(item);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private bool Accepts(PackageItemViewModel item)
    {
        var model = item.Model;
        var backend = model.Backend;
        var isDev = BackendInfo.IsDev(backend);
        // The big switch splits system backends from developer tools.
        if (_isDevMode ? !isDev : isDev)
            return false;

        if (_selectedChip == ChipKind.Aur)
        {
            if (backend != Backend.Pacman || !model.Foreign)
                return false;
        }
        else if (_selectedChip != ChipKind.All)
        {
            if ((Backend)_selectedChip != backend)
                return false;
            // The pacman chip only lists repo packages; AUR has its own chip.
            if (backend == Backend.Pacman && model.Foreign)
                return false;
        }

        if (ShowUpdatesOnly && model.Status != PackageStatus.NeedsUpdate)
            return false;

        if (SearchText.Length > 0)
        {
            bool Matches(string text) => text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            if (!Matches(model.Name) && !Matches(model.PackageId) && !Matches(model.Description))
                return false;
        }
        return true;
    }

    // --------------------------------------------------------------- counters

    private void UpdateCounters()
    {
        int pacman = 0, aur = 0, flatpak = 0, cargo = 0, mise = 0, rustup = 0, fvm = 0;
        int pacmanUpdates = 0, aurUpdates = 0, devUpdates = 0, totalUpdates = 0, selected = 0;

        foreach (var item in _all)
        {
            var model = item.Model;
            if (model.Selected)
                ++selected;
            var needsUpdate = model.Status == PackageStatus.NeedsUpdate;
            if (needsUpdate)
                ++totalUpdates;
            switch (model.Backend)
            {
                case Backend.Pacman:
                    if (model.Foreign)
                    {
                        ++aur;
                        if (needsUpdate)
                            ++aurUpdates;
                    }
                    else
                    {
                        ++pacman;
                        if (needsUpdate)
                            ++pacmanUpdates;
                    }
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

        UpdatesChipText = Loc.T("Updates (%1)", totalUpdates);
        UpdatesButtonText = Loc.T("Updates");
        HasUpdates = totalUpdates > 0;
        if (!HasUpdates && ShowUpdatesOnly)
            ShowUpdatesOnly = false;

        CountText = Loc.T("%1 selected · %2 updates", selected, totalUpdates);
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
        foreach (var chip in Chips)
            chip.IsEnabled = false;
    }

    private void EndBusy()
    {
        IsBusy = false;
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
        _operationCts = new CancellationTokenSource();
        StatusMessage = Loc.T("Loading package lists…");
        try
        {
            var packages = await _manager.LoadPackagesAsync(_operationCts.Token);
            _all.Clear();
            foreach (var package in packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                _all.Add(new PackageItemViewModel(package));
            if (_updatesLoaded)
                ApplyUpdatesToModels();
            ApplyFilter();
            UpdateCounters();
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
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy)
            return;
        BeginBusy();
        _operationCts = new CancellationTokenSource();
        StatusMessage = Loc.T("Checking for updates…");
        try
        {
            _updates = await _manager.CheckUpdatesAsync(_operationCts.Token);
            _updatesLoaded = true;
            ApplyUpdatesToModels();
            ApplyFilter();
            UpdateCounters();
            StatusMessage = HasUpdates
                ? Loc.T("%1 updates available", CountAllUpdates())
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

    private int CountAllUpdates()
    {
        var count = 0;
        foreach (var item in _all)
        {
            if (item.Model.Status == PackageStatus.NeedsUpdate)
                ++count;
        }
        return count;
    }

    private void ApplyUpdatesToModels()
    {
        foreach (var (backend, entries) in _updates)
        {
            foreach (var (name, newVersion) in entries)
            {
                foreach (var item in _all)
                {
                    if (item.Model.Backend != backend)
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

    private async Task RunOperationAsync(PackageOperation operation, IReadOnlyList<Package> packages)
    {
        if (packages.Count == 0 || IsBusy)
            return;
        BeginBusy();
        _operationCts = new CancellationTokenSource();
        var ok = true;
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
            await _manager.RunOperationAsync(operation, packages, _operationCts.Token);
            if (!ok && message.Length > 0)
                StatusMessage = message;
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
            _manager.OperationFinished -= OnFinished;
            EndBusy();
        }

        await RefreshAsync();
        await CheckUpdatesAsync();
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var packages = _all.Where(item => item.Model.Selected).Select(item => item.Model).ToList();
        if (packages.Count == 0)
            return;
        var names = string.Join(", ", packages.Select(package => package.Name));
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Install Packages"),
            Loc.Tn("Install the following package?", "Install the following %1 packages?",
                packages.Count, packages.Count) + "\n\n" + names,
            Loc.T("OK"), Loc.T("Cancel"));
        if (!confirmed)
            return;
        await RunOperationAsync(PackageOperation.Install, packages);
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var packages = _all.Where(item => item.Model.Selected).Select(item => item.Model).ToList();
        if (packages.Count == 0)
            return;
        var names = string.Join(", ", packages.Select(package => package.Name));
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Remove Packages"),
            Loc.Tn("Remove the following package?", "Remove the following %1 packages?",
                packages.Count, packages.Count) + "\n\n" + names,
            Loc.T("OK"), Loc.T("Cancel"));
        if (!confirmed)
            return;
        await RunOperationAsync(PackageOperation.Remove, packages);
    }

    [RelayCommand]
    private async Task ShowUpdatesAsync()
    {
        var updates = _all.Where(item => item.Model.Status == PackageStatus.NeedsUpdate)
            .Select(item => item.Model).ToList();
        if (updates.Count == 0)
            return;
        var viewModel = new UpdateViewModel(updates);
        var selected = await _dialogs.ShowUpdatesAsync(viewModel);
        if (selected is null || selected.Count == 0)
            return;
        await RunOperationAsync(PackageOperation.Update, selected);
    }

    [RelayCommand]
    private async Task InstallPackageAsync(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Install Package"), Loc.T("Install %1?", item.Name), Loc.T("OK"), Loc.T("Cancel"));
        if (confirmed)
            await RunOperationAsync(PackageOperation.Install, [item.Model]);
    }

    [RelayCommand]
    private async Task UninstallPackageAsync(PackageItemViewModel? item)
    {
        if (item is null)
            return;
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Uninstall Package"), Loc.T("Uninstall %1?", item.Name), Loc.T("OK"), Loc.T("Cancel"));
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
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Update Package"), message, Loc.T("OK"), Loc.T("Cancel"));
        if (confirmed)
            await RunOperationAsync(PackageOperation.Update, [item.Model]);
    }

    // ------------------------------------------------------------------- log

    private void OnCommandStarted(CommandStarted activity)
    {
        AddLog(LogEntryKind.Command, "$ " + activity.CommandLine);
        Dispatcher.UIThread.Post(() =>
        {
            _activeCommands[activity.Id] = activity.CommandLine;
            UpdateCurrentCommand();
        });
    }

    private void OnCommandOutput(CommandOutput activity) => AddLog(LogEntryKind.Output, activity.Line);

    private void OnCommandFinished(CommandFinished activity)
    {
        if (activity.Operation)
        {
            // Mutating commands (install/remove/update) carry real output, so
            // their exit status deserves a clear success/failure line.
            AddLog(
                activity.Ok ? LogEntryKind.Success : LogEntryKind.Error,
                activity.Ok
                    ? "\u2713 " + activity.CommandLine
                    : $"\u2717 {activity.CommandLine} ({Loc.T("exit %1", activity.ExitCode)})");
        }
        else
        {
            // Read-only queries (e.g. checkupdates exits 2 when nothing is
            // pending) are informational, never failures.
            AddLog(
                LogEntryKind.Info,
                $"\u00B7 {activity.CommandLine} ({Loc.T("exit %1", activity.ExitCode)})");
        }
        Dispatcher.UIThread.Post(() =>
        {
            _activeCommands.Remove(activity.Id);
            UpdateCurrentCommand();
        });
    }

    private void UpdateCurrentCommand()
    {
        CurrentCommand = _activeCommands.Count == 0
            ? string.Empty
            : _activeCommands.OrderByDescending(pair => pair.Key).First().Value;
    }

    /// <summary>Queues a log line; the UI drains the queue on the dispatching thread.</summary>
    private void AddLog(LogEntryKind kind, string text)
    {
        _pendingLog.Enqueue((kind, text));
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(FlushLog, DispatcherPriority.Background);
    }

    private void FlushLog()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        while (_pendingLog.TryDequeue(out var entry))
            LogEntries.Add(new LogEntryViewModel(entry.Kind, entry.Text));
        while (LogEntries.Count > MaxLogEntries)
            LogEntries.RemoveAt(0);
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
        LogEntries.Clear();
        while (_pendingLog.TryDequeue(out _))
        {
            // Drop anything that has not been painted yet.
        }
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        var text = string.Join(Environment.NewLine, LogEntries.Select(entry => entry.Text));
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
        _config.Save();
    }

    // ---------------------------------------------------------------- dialogs

    [RelayCommand]
    private async Task SettingsAsync()
    {
        var viewModel = new SettingsViewModel(_config);
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
        ApplyBackendSettings();
        RefreshModeUi();
        UpdateCounters();
        if (backendsChanged)
        {
            await RefreshAsync();
            if (_checkUpdatesOnStart)
                await CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    private async Task ProxyAsync()
    {
        var viewModel = new ProxyViewModel(_proxy);
        if (!await _dialogs.ShowProxyAsync(viewModel))
            return;
        _manager.SetProxy(_proxy);
        StatusMessage = _proxy.Url.Length == 0
            ? Loc.T("Proxy disabled")
            : Loc.T("Proxy: %1", _proxy.Url);
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
    private static void Quit()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    // ---------------------------------------------------------------- details

    private void RebuildDetail(PackageItemViewModel? item)
    {
        DetailFields.Clear();
        if (item is null)
        {
            IsPackageSelected = false;
            DetailDescription = string.Empty;
            HasDetailDescription = false;
            CanInstallSelectedPackage = false;
            CanUpdateSelectedPackage = false;
            CanUninstallSelectedPackage = false;
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
        Add(Loc.T("Size:"), model.Size);
        Add(Loc.T("Install date:"), model.InstallDate);
        Add(Loc.T("Install reason:"), model.InstallReason);
        Add(Loc.T("Packager:"), model.Packager);
        if (model.Url.Length > 0)
            DetailFields.Add(DetailFieldViewModel.Link(Loc.T("URL:"), model.Url));
        Add(Loc.T("Licenses:"), model.Licenses);
        Add(Loc.T("Depends on:"), model.Depends);
        Add(Loc.T("Binaries:"), model.Binaries);

        var statusText = model.Status == PackageStatus.Installed ? string.Empty : BackendInfo.StatusText(model.Status);
        if (statusText.Length > 0)
            DetailFields.Add(DetailFieldViewModel.Plain(Loc.T("Status:"), statusText));

        DetailDescription = model.Description;
        HasDetailDescription = model.Description.Length > 0;
        CanInstallSelectedPackage = !model.Installed;
        CanUpdateSelectedPackage = model.Status == PackageStatus.NeedsUpdate;
        CanUninstallSelectedPackage = model.Installed;

        if (model.Backend == Backend.Cargo && model.Description.Length == 0)
            _ = FetchCargoDescriptionAsync(model);
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
            if (proxyUrl.Length > 0)
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxyUrl);
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
        UpdateCounters();
        RebuildDetail(SelectedPackage);
        StatusMessage = Loc.T("Ready");
    }

    private void LoadBackendSettings()
    {
        _pacmanEnabled = _config.GetBool("General", "EnablePacman", true);
        _flatpakEnabled = _config.GetBool("General", "EnableFlatpak", true);
        _cargoEnabled = _config.GetBool("General", "EnableCargo", true);
        _miseEnabled = _config.GetBool("General", "EnableMise", false);
        _rustupEnabled = _config.GetBool("General", "EnableRustup", false);
        _fvmEnabled = _config.GetBool("General", "EnableFvm", false);
    }

    private void ApplyBackendSettings()
    {
        _manager.SetBackendEnabled(Backend.Pacman, _pacmanEnabled);
        _manager.SetBackendEnabled(Backend.Flatpak, _flatpakEnabled);
        _manager.SetBackendEnabled(Backend.Cargo, _cargoEnabled);
        _manager.SetBackendEnabled(Backend.Mise, _miseEnabled);
        _manager.SetBackendEnabled(Backend.Rustup, _rustupEnabled);
        _manager.SetBackendEnabled(Backend.Fvm, _fvmEnabled);
        _manager.SetProxy(_proxy);
    }
}
