using System;

namespace AvaloniaVS;

internal static class Constants
{
    public const string PackageGuidString = "04d3d704-cfaf-4352-b557-0528cbbc5b33";
    public static readonly Guid PackageGuid = new (PackageGuidString);
    public const string PackageName = "Avalonia XAML Editor (Community)";
    public const string axaml = nameof(axaml);
    public const string xaml = nameof(xaml);
    public const string paml = nameof(paml);

    public const string AvaloviaFactoryEditorGuidString = @"863DE69F-2F7B-4F43-A349-699FA3581A65";
    public static readonly Guid AvaloviaFactoryEditorGuid = new (AvaloviaFactoryEditorGuidString);

    public const string AvaloniaCapability = nameof(Avalonia);
}
