using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly Config _config;
    private readonly string _initialLanguage;
    private readonly string _initialTheme;
    private readonly bool _initialPacman;
    private readonly bool _initialFlatpak;
    private readonly bool _initialCargo;
    private readonly bool _initialMise;
    private readonly bool _initialRustup;
    private readonly bool _initialFvm;

    public SettingsViewModel(Config config)
    {
        _config = config;

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

        _initialLanguage = config.GetString("General", "Language", "system");
        _initialTheme = config.GetString("General", "Theme", "system");
        SelectedLanguage = Languages.FirstOrDefault(option => option.Value == _initialLanguage) ?? Languages[0];
        SelectedTheme = Themes.FirstOrDefault(option => option.Value == _initialTheme) ?? Themes[0];
        CheckUpdatesOnStart = config.GetBool("General", "CheckUpdatesOnStart", true);

        EnablePacman = _initialPacman = config.GetBool("General", "EnablePacman", true);
        EnableFlatpak = _initialFlatpak = config.GetBool("General", "EnableFlatpak", true);
        EnableCargo = _initialCargo = config.GetBool("General", "EnableCargo", true);
        EnableMise = _initialMise = config.GetBool("General", "EnableMise", false);
        EnableRustup = _initialRustup = config.GetBool("General", "EnableRustup", false);
        EnableFvm = _initialFvm = config.GetBool("General", "EnableFvm", false);
    }

    public ObservableCollection<OptionItem> Languages { get; }

    public ObservableCollection<OptionItem> Themes { get; }

    public string LanguageLabel => Loc.T("Language:");
    public string ThemeLabel => Loc.T("Theme:");
    public string StartupCheckText => Loc.T("Check for updates on startup");
    public string BackendsGroup => Loc.T("Package backends");
    public string EnablePacmanText => Loc.T("Enable pacman");
    public string EnableFlatpakText => Loc.T("Enable Flatpak");
    public string EnableCargoText => Loc.T("Enable Cargo");
    public string DevGroup => Loc.T("Developer tools");
    public string EnableMiseText => Loc.T("Enable mise");
    public string EnableRustupText => Loc.T("Enable rustup");
    public string EnableFvmText => Loc.T("Enable fvm");
    public string RestartNote => Loc.T("Language changes apply after restart.");
    public string OkText => Loc.T("OK");
    public string CancelText => Loc.T("Cancel");

    [ObservableProperty]
    private OptionItem? _selectedLanguage;

    [ObservableProperty]
    private OptionItem? _selectedTheme;

    [ObservableProperty]
    private bool _checkUpdatesOnStart;

    [ObservableProperty]
    private bool _enablePacman;

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

    public bool BackendsChanged =>
        EnablePacman != _initialPacman
        || EnableFlatpak != _initialFlatpak
        || EnableCargo != _initialCargo
        || EnableMise != _initialMise
        || EnableRustup != _initialRustup
        || EnableFvm != _initialFvm;

    public void Save()
    {
        _config.Set("General", "Language", SelectedLanguage?.Value ?? "system");
        _config.Set("General", "Theme", SelectedTheme?.Value ?? "system");
        _config.Set("General", "CheckUpdatesOnStart", CheckUpdatesOnStart);
        _config.Set("General", "EnablePacman", EnablePacman);
        _config.Set("General", "EnableFlatpak", EnableFlatpak);
        _config.Set("General", "EnableCargo", EnableCargo);
        _config.Set("General", "EnableMise", EnableMise);
        _config.Set("General", "EnableRustup", EnableRustup);
        _config.Set("General", "EnableFvm", EnableFvm);
        _config.Save();
        // The colour scheme applies immediately; the language needs a restart.
        App.ApplyTheme(SelectedTheme?.Value ?? "system");
    }
}
