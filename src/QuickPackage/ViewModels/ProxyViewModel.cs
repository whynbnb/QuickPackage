using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public partial class ProxyViewModel : ObservableObject
{
    private readonly ProxySettings _proxy;

    public ProxyViewModel(ProxySettings proxy)
    {
        _proxy = proxy;
        Modes =
        [
            new OptionItem { Value = "0", Label = Loc.T("No proxy") },
            new OptionItem { Value = "1", Label = "HTTP" },
            new OptionItem { Value = "2", Label = "SOCKS5" },
        ];
        SelectedMode = Modes.FirstOrDefault(option => option.Value == ((int)proxy.Mode).ToString()) ?? Modes[0];
        Host = proxy.Host;
        Port = proxy.Port;
    }

    public ObservableCollection<OptionItem> Modes { get; }

    public string ModeLabel => Loc.T("Mode:");
    public string HostLabel => Loc.T("Host:");
    public string PortLabel => Loc.T("Port:");
    public string TestText => Loc.T("Test");
    public string OkText => Loc.T("OK");
    public string CancelText => Loc.T("Cancel");

    [ObservableProperty]
    private OptionItem? _selectedMode;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private decimal _port = 1080;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    public bool IsProxyEnabled => SelectedMode?.Value != "0";

    public bool IsNotTesting => !IsTesting;

    partial void OnSelectedModeChanged(OptionItem? value)
    {
        OnPropertyChanged(nameof(IsProxyEnabled));
    }

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(IsNotTesting));

    [RelayCommand]
    private async Task TestAsync()
    {
        var mode = (ProxyMode)int.Parse(SelectedMode?.Value ?? "0");
        var url = ProxySettings.MakeUrl(mode, Host.Trim(), (int)Port);
        if (url.Length == 0)
        {
            TestResult = Loc.T("Invalid proxy configuration.");
            return;
        }

        IsTesting = true;
        TestResult = Loc.T("Testing connection…");
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy(url),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync("https://archlinux.org");
            TestResult = response.IsSuccessStatusCode || (int)response.StatusCode < 500
                ? Loc.T("Connection successful.")
                : Loc.T("Connection failed: %1", $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            TestResult = Loc.T("Connection failed: %1", exception.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void Apply()
    {
        _proxy.Mode = (ProxyMode)int.Parse(SelectedMode?.Value ?? "0");
        _proxy.Host = Host.Trim();
        _proxy.Port = (int)Port;
        _proxy.Save(Config.Load());
    }
}
