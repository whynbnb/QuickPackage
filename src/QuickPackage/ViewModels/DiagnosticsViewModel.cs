using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public sealed class DiagnosticRowViewModel
{
    public required string BackendText { get; init; }
    public required string Tool { get; init; }
    public required string RequirementText { get; init; }
    public required string StatusText { get; init; }
    public required string Purpose { get; init; }
    public IBrush? StatusForeground { get; init; }
}

/// <summary>Shows which tools the enabled backends are missing.</summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    public DiagnosticsViewModel(IReadOnlyList<BackendDiagnostic> diagnostics)
    {
        foreach (var item in diagnostics)
        {
            Rows.Add(new DiagnosticRowViewModel
            {
                BackendText = BackendInfo.DisplayName(item.Backend),
                Tool = item.Tool,
                RequirementText = item.Required ? Loc.T("Required") : Loc.T("Optional"),
                StatusText = item.Available ? Loc.T("Available") : Loc.T("Missing"),
                Purpose = Loc.T(item.PurposeKey),
                StatusForeground = item.Available ? OkBrush : (item.Required ? MissingBrush : OptionalBrush),
            });
        }

        var missing = diagnostics.Count(item => item.Required && !item.Available);
        SummaryText = missing == 0
            ? Loc.T("Everything needed by the enabled backends is installed.")
            : Loc.Tn("%1 required tool is missing", "%1 required tools are missing", missing, missing);
        HasIssues = missing > 0;
    }

    public ObservableCollection<DiagnosticRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _hasIssues;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public string Title => Loc.T("Backend diagnostics");
    public string Subtitle => Loc.T("Required tools and helpers for the enabled package backends.");
    public string BackendHeader => Loc.T("Backend");
    public string ToolHeader => Loc.T("Tool");
    public string RequirementHeader => Loc.T("Requirement");
    public string StatusHeader => Loc.T("Status");
    public string PurposeHeader => Loc.T("Purpose");
    public string CloseText => Loc.T("Close");

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    private static IBrush OkBrush => Brush("#57C99A", "#2E9E6B");
    private static IBrush MissingBrush => Brush("#E5534B", "#C0392B");
    private static IBrush OptionalBrush => Brush("#F0A44A", "#D97706");

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
