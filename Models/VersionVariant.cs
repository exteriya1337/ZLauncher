namespace ZLauncher.Models;

public enum LoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    OptiFine,
    /// <summary>Пользовательская сборка из папки versions/.</summary>
    Custom
}

public sealed class VersionVariant
{
    public required string Key { get; init; }
    public required string GameVersionId { get; init; }
    public required LoaderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? LoaderVersion { get; init; }

    public string KindLabel => Kind switch
    {
        LoaderKind.Vanilla => "Vanilla",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.OptiFine => "OptiFine",
        LoaderKind.Custom => "Custom",
        _ => Kind.ToString()
    };
}
