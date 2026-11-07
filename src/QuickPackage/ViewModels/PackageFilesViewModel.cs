using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

/// <summary>Shows the files owned by a package and its config remnants.</summary>
public partial class PackageFilesViewModel : ObservableObject
{
    public PackageFilesViewModel(string packageName, PackageManager.PackageFilesResult result)
    {
        PackageName = packageName;
        FilesText = result.Files.Count == 0
            ? Loc.T("No files reported")
            : string.Join(Environment.NewLine, result.Files);
        SummaryText = Loc.T("%1 files · %2 modified backup files · %3 config remnants",
            result.Files.Count,
            result.BackupFiles.Count(file => file.Status.Contains("modified", StringComparison.OrdinalIgnoreCase)),
            result.Remnants.Count);

        var backupLines = new List<string>();
        foreach (var file in result.BackupFiles)
            backupLines.Add(file.Status.Length > 0 ? $"{file.Path}  [{file.Status}]" : file.Path);
        if (result.Remnants.Count > 0)
        {
            if (backupLines.Count > 0)
                backupLines.Add(string.Empty);
            backupLines.Add(Loc.T("Config remnants:"));
            backupLines.AddRange(result.Remnants.Select(path => "  " + path));
        }
        BackupText = backupLines.Count == 0
            ? Loc.T("No modified backup files")
            : string.Join(Environment.NewLine, backupLines);
        HasRemnants = result.Remnants.Count > 0;
    }

    public string PackageName { get; }

    public string Title => Loc.T("Package files");
    public string Subtitle => Loc.T("Files installed by %1", PackageName);
    public string FilesTab => Loc.T("Files");
    public string BackupTab => Loc.T("Backup files");
    public string CloseText => Loc.T("Close");

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _filesText = string.Empty;

    [ObservableProperty]
    private string _backupText = string.Empty;

    [ObservableProperty]
    private bool _hasRemnants;

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
}
