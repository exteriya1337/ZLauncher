using System;

namespace ZLauncher.Models;

/// <summary>Установленная сборка (из маркера .installed) для быстрого выбора.</summary>
public sealed class InstalledBuildInfo
{
    public required string Key { get; init; }
    public required string GameVersionId { get; init; }
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? LoaderVersion { get; init; }
    public string FolderName { get; init; } = "";

    /// <summary>Короткая строка для UI: «1.12.2 · OptiFine HD_U C9».</summary>
    public string Summary => Kind.Equals("Vanilla", StringComparison.OrdinalIgnoreCase)
        ? $"{GameVersionId} · Vanilla"
        : $"{GameVersionId} · {DisplayName}";
}
