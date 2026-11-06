namespace QuickPackage.ViewModels;

/// <summary>Simple value/label pair for combo boxes.</summary>
public sealed class OptionItem
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    public bool IsEnabled { get; init; } = true;

    public override string ToString() => Label;
}
