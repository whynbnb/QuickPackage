using CommunityToolkit.Mvvm.Input;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

/// <summary>One "label: value" row in the detail panel.</summary>
public sealed class DetailFieldViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public bool IsLink { get; init; }
    public IRelayCommand? OpenCommand { get; init; }

    public static DetailFieldViewModel Plain(string label, string value)
        => new() { Label = label, Value = value };

    public static DetailFieldViewModel Link(string label, string url)
        => new()
        {
            Label = label,
            Value = url,
            IsLink = true,
            OpenCommand = new RelayCommand(() => Loc.OpenUrl(url)),
        };
}
