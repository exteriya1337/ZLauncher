using System;
using System.Collections.Generic;

namespace ZLauncher.Models;

/// <summary>Краткая карточка мода с Modrinth (результат поиска).</summary>
public sealed class ModrinthModInfo
{
    public required string ProjectId { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? IconUrl { get; init; }
    public required string Author { get; init; }
    public long Downloads { get; init; }
    public long Follows { get; init; }
    public string ProjectType { get; init; } = "mod";
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();
    public string? LatestVersion { get; init; }

    public bool IsResourcePack =>
        string.Equals(ProjectType, "resourcepack", StringComparison.OrdinalIgnoreCase);

    public bool IsShader =>
        string.Equals(ProjectType, "shader", StringComparison.OrdinalIgnoreCase);

    public string PageUrl
    {
        get
        {
            var kind = IsResourcePack ? "resourcepack"
                : IsShader ? "shader"
                : string.Equals(ProjectType, "modpack", StringComparison.OrdinalIgnoreCase) ? "modpack"
                : "mod";
            return $"https://modrinth.com/{kind}/{Slug}";
        }
    }
}
