using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public sealed partial class NewsItemViewModel
{
    public required string Title { get; init; }
    public required string DateText { get; init; }
    public required string Summary { get; init; }
    public required string Url { get; init; }

    public string OpenText => Loc.T("Open");

    [RelayCommand]
    private void Open() => Loc.OpenUrl(Url);
}

public sealed class MirrorRowViewModel
{
    public required string Url { get; init; }
    public required string Country { get; init; }
    public required string LastSync { get; init; }
    public required string Completion { get; init; }
    public required string Delay { get; init; }
    public required string Score { get; init; }
}

/// <summary>Arch news and the status of the mirrors in pacman's mirrorlist.</summary>
public partial class NewsViewModel : ObservableObject
{
    private readonly OnlineService _online;

    public NewsViewModel(OnlineService online)
    {
        _online = online;
        _ = RefreshAsync();
    }

    public ObservableCollection<NewsItemViewModel> News { get; } = [];

    public ObservableCollection<MirrorRowViewModel> Mirrors { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasNews;

    [ObservableProperty]
    private bool _hasMirrors;

    public string Title => Loc.T("Arch news and mirrors");
    public string Subtitle => Loc.T("Announcements that may require manual intervention, and the health of the configured mirrors");
    public string NewsTab => Loc.T("News");
    public string MirrorsTab => Loc.T("Mirrors");
    public string NewsEmptyText => Loc.T("No news available");
    public string MirrorsEmptyText => Loc.T("No configured mirror was found in the status feed");
    public string RefreshText => Loc.T("Refresh");
    public string CloseText => Loc.T("Close");
    public string OpenText => Loc.T("Open");
    public string MirrorUrlHeader => Loc.T("Mirror");
    public string CountryHeader => Loc.T("Country");
    public string LastSyncHeader => Loc.T("Last sync");
    public string CompletionHeader => Loc.T("Completion");
    public string DelayHeader => Loc.T("Delay");
    public string ScoreHeader => Loc.T("Score");

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusMessage = Loc.T("Loading Arch news and mirror status…");
        try
        {
            var news = await _online.GetArchNewsAsync(CancellationToken.None);
            News.Clear();
            foreach (var item in news)
            {
                News.Add(new NewsItemViewModel
                {
                    Title = item.Title,
                    DateText = item.Date?.LocalDateTime.ToString("yyyy-MM-dd") ?? string.Empty,
                    Summary = item.Summary,
                    Url = item.Url,
                });
            }
            HasNews = News.Count > 0;

            var mirrors = await _online.GetMirrorStatusAsync(CancellationToken.None);
            Mirrors.Clear();
            foreach (var mirror in mirrors)
            {
                Mirrors.Add(new MirrorRowViewModel
                {
                    Url = mirror.Url,
                    Country = mirror.Country,
                    LastSync = mirror.LastSync.Length > 0 ? mirror.LastSync : "—",
                    Completion = mirror.Completion > 0 ? $"{mirror.Completion:0.#}%" : "—",
                    Delay = mirror.Delay > 0 ? $"{mirror.Delay:0} h" : "—",
                    Score = mirror.Score > 0 ? mirror.Score.ToString("0.##") : "—",
                });
            }
            HasMirrors = Mirrors.Count > 0;
            StatusMessage = Loc.T("Loaded %1 news items and %2 mirrors", News.Count, Mirrors.Count);
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
}
