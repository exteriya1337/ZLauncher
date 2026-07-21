using System;

namespace ZLauncher.Models;

public sealed class MinecraftVersion
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }

    public string DisplayName => Id;

    public string TypeLabel => Type switch
    {
        "release" => "release",
        "snapshot" => "snapshot",
        "old_beta" => "old beta",
        "old_alpha" => "old alpha",
        "custom" => "custom",
        _ => Type
    };

    /// <summary>Имя папки в versions/ для кастомных сборок (если отличается от Id).</summary>
    public string? CustomFolderName { get; init; }
}
