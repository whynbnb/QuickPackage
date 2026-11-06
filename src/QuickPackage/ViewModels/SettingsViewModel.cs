using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly Config _config;
    private readonly ProxySettings _proxy;
    private readonly string _initialLanguage;
    private readonly string _initialTheme;
    private readonly bool _initialPacman;
    private readonly bool _initialAur;
    private readonly bool _initialFlatpak;
    private readonly bool _initialCargo;
    private readonly bool _initialMise;
    private readonly bool _initialRustup;
    private readonly bool _initialFvm;

    public SettingsViewModel(Config config, ProxySettings proxy)
    {
        _config = config;
        _proxy = proxy;

        Languages =
        [
            new OptionItem { Value = "system", Label = Loc.T("System") },
            new OptionItem { Value = "en", Label = Loc.T("English") },
            new OptionItem { Value = "zh_CN", Label = "简体中文" },
        ];
        Themes =
        [
            new OptionItem { Value = "system", Label = Loc.T("System") },
            new OptionItem { Value = "light", Label = Loc.T("Light") },
            new OptionItem { Value = "dark", Label = Loc.T("Dark") },
        ];
        ProxyModes =
        [
            new OptionItem { Value = "0", Label = Loc.T("No proxy") },
            new OptionItem { Value = "1", Label = "HTTP" },
            new OptionItem { Value = "2", Label = "SOCKS5" },
        ];
        AurHelpers =
        [
            new OptionItem
            {
                Value = "paru",
                Label = "paru",
                IsEnabled = CommandRunner.HasExecutable("paru"),
            },
            new OptionItem
            {
                Value = "yay",
                Label = "yay",
                IsEnabled = CommandRunner.HasExecutable("yay"),
            },
        ];
        var savedHelper = config.GetString("General", "AurHelper");
        SelectedAurHelper =
            AurHelpers.FirstOrDefault(item => item.IsEnabled && item.Value == savedHelper)
            ?? AurHelpers.FirstOrDefault(item => item.IsEnabled);

        _initialLanguage = config.GetString("General", "Language", "system");
        _initialTheme = config.GetString("General", "Theme", "system");
        SelectedLanguage = Languages.FirstOrDefault(option => option.Value == _initialLanguage) ?? Languages[0];
        SelectedTheme = Themes.FirstOrDefault(option => option.Value == _initialTheme) ?? Themes[0];
        CheckUpdatesOnStart = config.GetBool("General", "CheckUpdatesOnStart", true);
        QueryTimeout = Math.Clamp(config.GetInt("General", "CommandTimeout", 30), 5, 600);

        EnablePacman = _initialPacman = config.GetBool("General", "EnablePacman", true);
        EnableAur = _initialAur = config.GetBool("General", "EnableAur", true);
        EnableFlatpak = _initialFlatpak = config.GetBool("General", "EnableFlatpak", true);
        EnableCargo = _initialCargo = config.GetBool("General", "EnableCargo", true);
        EnableMise = _initialMise = config.GetBool("General", "EnableMise", false);
        EnableRustup = _initialRustup = config.GetBool("General", "EnableRustup", false);
        EnableFvm = _initialFvm = config.GetBool("General", "EnableFvm", false);

        SelectedProxyMode = ProxyModes.FirstOrDefault(
            option => option.Value == ((int)proxy.Mode).ToString()) ?? ProxyModes[0];
        ProxyHost = proxy.Host;
        ProxyPort = proxy.Port;
        ProxyPacman = proxy.IsEnabledFor(Backend.Pacman);
        ProxyAur = proxy.IsEnabledFor(Backend.Aur);
        ProxyFlatpak = proxy.IsEnabledFor(Backend.Flatpak);
        ProxyCargo = proxy.IsEnabledFor(Backend.Cargo);
        ProxyMise = proxy.IsEnabledFor(Backend.Mise);
        ProxyRustup = proxy.IsEnabledFor(Backend.Rustup);
        ProxyFvm = proxy.IsEnabledFor(Backend.Fvm);
    }

    public ObservableCollection<OptionItem> Languages { get; }

    public ObservableCollection<OptionItem> Themes { get; }

    public ObservableCollection<OptionItem> ProxyModes { get; }

    public ObservableCollection<OptionItem> AurHelpers { get; }

    public string LanguageLabel => Loc.T("Language:");
    public string ThemeLabel => Loc.T("Theme:");
    public string StartupCheckText => Loc.T("Check for updates on startup");
    public string QueryTimeoutLabel => Loc.T("Query timeout (s):");
    public string BackendsGroup => Loc.T("Package backends");
    public string EnablePacmanText => Loc.T("Enable pacman");
    public string EnableAurText => Loc.T("Enable AUR");
    public string EnableFlatpakText => Loc.T("Enable Flatpak");
    public string EnableCargoText => Loc.T("Enable Cargo");
    public string DevGroup => Loc.T("Developer tools");
    public string EnableMiseText => Loc.T("Enable mise");
    public string EnableRustupText => Loc.T("Enable rustup");
    public string EnableFvmText => Loc.T("Enable fvm");
    public string OkText => Loc.T("OK");
    public string CancelText => Loc.T("Cancel");

    public string AurHelperLabel => Loc.T("AUR helper:");
    public string AurHelperHint => Loc.T("No AUR helper (paru or yay) found");
    public bool HasAurHelper => AurHelpers.Any(item => item.IsEnabled);

    public string ProxyGroup => Loc.T("Proxy");
    public string ProxyModeLabel => Loc.T("Mode:");
    public string ProxyHostLabel => Loc.T("Host:");
    public string ProxyPortLabel => Loc.T("Port:");
    public string ProxyTestText => Loc.T("Test");
    public string ProxyPerBackendLabel => Loc.T("Use the proxy for:");
    public string ProxyPacmanText => "Pacman";
    public string ProxyAurText => "AUR";
    public string ProxyFlatpakText => Loc.T("Flatpak");
    public string ProxyCargoText => Loc.T("Cargo");
    public string ProxyMiseText => "mise";
    public string ProxyRustupText => "rustup";
    public string ProxyFvmText => "fvm";

    [ObservableProperty]
    private OptionItem? _selectedLanguage;

    [ObservableProperty]
    private OptionItem? _selectedTheme;

    [ObservableProperty]
    private bool _checkUpdatesOnStart;

    [ObservableProperty]
    private decimal _queryTimeout = 30;

    [ObservableProperty]
    private bool _enablePacman;

    [ObservableProperty]
    private bool _enableAur;

    [ObservableProperty]
    private bool _enableFlatpak;

    [ObservableProperty]
    private bool _enableCargo;

    [ObservableProperty]
    private bool _enableMise;

    [ObservableProperty]
    private bool _enableRustup;

    [ObservableProperty]
    private bool _enableFvm;

    [ObservableProperty]
    private OptionItem? _selectedAurHelper;

    [ObservableProperty]
    private OptionItem? _selectedProxyMode;

    [ObservableProperty]
    private string _proxyHost = string.Empty;

    [ObservableProperty]
    private decimal _proxyPort = 1080;

    [ObservableProperty]
    private string _proxyTestResult = string.Empty;

    [ObservableProperty]
    private bool _isProxyTesting;

    [ObservableProperty]
    private bool _proxyPacman;

    [ObservableProperty]
    private bool _proxyAur;

    [ObservableProperty]
    private bool _proxyFlatpak;

    [ObservableProperty]
    private bool _proxyCargo;

    [ObservableProperty]
    private bool _proxyMise;

    [ObservableProperty]
    private bool _proxyRustup;

    [ObservableProperty]
    private bool _proxyFvm;

    public bool IsProxyEnabled => SelectedProxyMode?.Value != "0";

    public bool IsProxyNotTesting => !IsProxyTesting;

    public bool BackendsChanged =>
        EnablePacman != _initialPacman
        || EnableAur != _initialAur
        || EnableFlatpak != _initialFlatpak
        || EnableCargo != _initialCargo
        || EnableMise != _initialMise
        || EnableRustup != _initialRustup
        || EnableFvm != _initialFvm;

    partial void OnSelectedProxyModeChanged(OptionItem? value)
        => OnPropertyChanged(nameof(IsProxyEnabled));

    partial void OnIsProxyTestingChanged(bool value)
        => OnPropertyChanged(nameof(IsProxyNotTesting));

    [RelayCommand]
    private async Task TestProxyAsync()
    {
        var mode = (ProxyMode)int.Parse(SelectedProxyMode?.Value ?? "0");
        var url = ProxySettings.MakeUrl(mode, ProxyHost.Trim(), (int)ProxyPort);
        if (url.Length == 0)
        {
            ProxyTestResult = Loc.T("Invalid proxy configuration.");
            return;
        }

        IsProxyTesting = true;
        ProxyTestResult = Loc.T("Testing connection…");
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy(url),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("https://archlinux.org");
            ProxyTestResult = response.IsSuccessStatusCode || (int)response.StatusCode < 500
                ? Loc.T("Connection successful.")
                : Loc.T("Connection failed: %1", $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            ProxyTestResult = Loc.T("Connection failed: %1", exception.Message);
        }
        finally
        {
            IsProxyTesting = false;
        }
    }

    public void Save()
    {
        _config.Set("General", "Language", SelectedLanguage?.Value ?? "system");
        _config.Set("General", "Theme", SelectedTheme?.Value ?? "system");
        _config.Set("General", "CheckUpdatesOnStart", CheckUpdatesOnStart);
        _config.Set("General", "CommandTimeout", Math.Clamp((int)QueryTimeout, 5, 600));
        _config.Set("General", "EnablePacman", EnablePacman);
        _config.Set("General", "EnableAur", EnableAur);
        _config.Set("General", "EnableFlatpak", EnableFlatpak);
        _config.Set("General", "EnableCargo", EnableCargo);
        _config.Set("General", "EnableMise", EnableMise);
        _config.Set("General", "EnableRustup", EnableRustup);
        _config.Set("General", "EnableFvm", EnableFvm);
        _config.Set("General", "AurHelper", SelectedAurHelper?.Value ?? string.Empty);

        _proxy.Mode = (ProxyMode)int.Parse(SelectedProxyMode?.Value ?? "0");
        _proxy.Host = ProxyHost.Trim();
        _proxy.Port = (int)ProxyPort;
        _proxy.SetEnabled(Backend.Pacman, ProxyPacman);
        _proxy.SetEnabled(Backend.Aur, ProxyAur);
        _proxy.SetEnabled(Backend.Flatpak, ProxyFlatpak);
        _proxy.SetEnabled(Backend.Cargo, ProxyCargo);
        _proxy.SetEnabled(Backend.Mise, ProxyMise);
        _proxy.SetEnabled(Backend.Rustup, ProxyRustup);
        _proxy.SetEnabled(Backend.Fvm, ProxyFvm);
        // Writes the proxy keys and persists the general changes made above.
        _proxy.Save(_config);

        App.ApplyTheme(SelectedTheme?.Value ?? "system");
    }
}
