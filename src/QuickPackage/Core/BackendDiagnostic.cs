namespace QuickPackage.Core;

/// <summary>Availability of one executable an enabled backend depends on.</summary>
public sealed record BackendDiagnostic(
    Backend Backend,
    string Tool,
    bool Required,
    bool Available,
    string PurposeKey);
