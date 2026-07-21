using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ZLauncher.Models;

namespace ZLauncher.Services;

/// <summary>
/// Подверсии (лоадеры) для конкретной версии Minecraft.
/// recommendedOnly: Vanilla + по одной актуальной сборке каждого лоадера.
/// full: все доступные сборки (для вкладки «Версии»).
/// </summary>
public sealed class ModLoaderVariantService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly object _forgeLock = new();
    private readonly object _neoLock = new();
    private Task<IReadOnlyList<string>>? _forgeVersionsTask;
    private Task<IReadOnlyList<string>>? _neoVersionsTask;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZLauncher/1.0");
        return client;
    }

    /// <summary>Рекомендуемые (для левого списка).</summary>
    public Task<IReadOnlyList<VersionVariant>> GetVariantsAsync(
        string gameVersionId,
        CancellationToken cancellationToken = default)
        => GetVariantsAsync(gameVersionId, recommendedOnly: true, cancellationToken);

    /// <summary>
    /// <paramref name="recommendedOnly"/> = true → по одной на лоадер;
    /// false → полный список (вкладка «Версии»).
    /// </summary>
    public async Task<IReadOnlyList<VersionVariant>> GetVariantsAsync(
        string gameVersionId,
        bool recommendedOnly,
        CancellationToken cancellationToken = default)
    {
        var results = new List<VersionVariant>
        {
            new()
            {
                Key = $"vanilla:{gameVersionId}",
                GameVersionId = gameVersionId,
                Kind = LoaderKind.Vanilla,
                DisplayName = "Vanilla",
                LoaderVersion = gameVersionId
            }
        };

        var forgeTask = GetForgeVariantsAsync(gameVersionId, recommendedOnly, cancellationToken);
        var neoTask = GetNeoForgeVariantsAsync(gameVersionId, recommendedOnly, cancellationToken);
        var fabricTask = GetFabricVariantsAsync(gameVersionId, recommendedOnly, cancellationToken);
        var quiltTask = GetQuiltVariantsAsync(gameVersionId, recommendedOnly, cancellationToken);
        var optiTask = GetOptiFineVariantsAsync(gameVersionId, recommendedOnly, cancellationToken);

        await Task.WhenAll(forgeTask, neoTask, fabricTask, quiltTask, optiTask).ConfigureAwait(false);

        results.AddRange(await forgeTask.ConfigureAwait(false));
        results.AddRange(await neoTask.ConfigureAwait(false));
        results.AddRange(await fabricTask.ConfigureAwait(false));
        results.AddRange(await quiltTask.ConfigureAwait(false));
        results.AddRange(await optiTask.ConfigureAwait(false));

        return results;
    }

    private async Task<IReadOnlyList<VersionVariant>> GetForgeVariantsAsync(
        string gameVersionId,
        bool recommendedOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var all = await GetForgeMavenVersionsAsync(cancellationToken).ConfigureAwait(false);
            var prefix = gameVersionId + "-";
            // Maven: oldest→newest
            var matched = all
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .Reverse() // newest first
                .ToList();

            if (matched.Count == 0)
                return Array.Empty<VersionVariant>();

            if (recommendedOnly)
                matched = matched.Take(1).ToList();

            return matched.Select(v =>
            {
                var loader = v.Length > prefix.Length ? v[prefix.Length..] : v;
                return new VersionVariant
                {
                    Key = $"forge:{v}",
                    GameVersionId = gameVersionId,
                    Kind = LoaderKind.Forge,
                    DisplayName = $"Forge {loader}",
                    LoaderVersion = loader
                };
            }).ToList();
        }
        catch
        {
            return Array.Empty<VersionVariant>();
        }
    }

    private Task<IReadOnlyList<string>> GetForgeMavenVersionsAsync(CancellationToken cancellationToken)
    {
        lock (_forgeLock)
        {
            _forgeVersionsTask ??= LoadMavenVersionsAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                cancellationToken);
            return _forgeVersionsTask;
        }
    }

    private async Task<IReadOnlyList<VersionVariant>> GetNeoForgeVariantsAsync(
        string gameVersionId,
        bool recommendedOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryGetNeoForgeMcPrefix(gameVersionId, out var neoPrefix))
                return Array.Empty<VersionVariant>();

            var all = await GetNeoMavenVersionsAsync(cancellationToken).ConfigureAwait(false);
            var matched = all
                .Where(v => v.StartsWith(neoPrefix, StringComparison.Ordinal))
                .Reverse()
                .ToList();

            if (matched.Count == 0)
                return Array.Empty<VersionVariant>();

            if (recommendedOnly)
                matched = matched.Take(1).ToList();

            return matched.Select(v => new VersionVariant
            {
                Key = $"neoforge:{v}",
                GameVersionId = gameVersionId,
                Kind = LoaderKind.NeoForge,
                DisplayName = $"NeoForge {v}",
                LoaderVersion = v
            }).ToList();
        }
        catch
        {
            return Array.Empty<VersionVariant>();
        }
    }

    private Task<IReadOnlyList<string>> GetNeoMavenVersionsAsync(CancellationToken cancellationToken)
    {
        lock (_neoLock)
        {
            _neoVersionsTask ??= LoadMavenVersionsAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
                cancellationToken);
            return _neoVersionsTask;
        }
    }

    private static bool TryGetNeoForgeMcPrefix(string gameVersionId, out string prefix)
    {
        prefix = "";
        var parts = gameVersionId.Split('.');
        if (parts.Length < 2 || parts[0] != "1")
            return false;

        if (!int.TryParse(parts[1], out var minor) || minor < 20)
            return false;

        if (minor == 20 && parts.Length >= 3 && int.TryParse(parts[2], out var patch) && patch < 2)
            return false;

        prefix = parts.Length >= 3
            ? $"{parts[1]}.{parts[2]}."
            : $"{parts[1]}.";
        return true;
    }

    private static async Task<IReadOnlyList<VersionVariant>> GetFabricVariantsAsync(
        string gameVersionId,
        bool recommendedOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameVersionId)}";
            var loaders = await Http.GetFromJsonAsync<List<FabricLoaderEntry>>(url, cancellationToken)
                .ConfigureAwait(false);

            if (loaders is null || loaders.Count == 0)
                return Array.Empty<VersionVariant>();

            var list = loaders
                .Where(l => l.Loader?.Version is not null)
                .Select(l => l!)
                .ToList();

            if (recommendedOnly)
                list = list.Take(1).ToList();

            return list.Select(l => new VersionVariant
            {
                Key = $"fabric:{gameVersionId}:{l.Loader!.Version}",
                GameVersionId = gameVersionId,
                Kind = LoaderKind.Fabric,
                DisplayName = $"Fabric {l.Loader.Version}",
                LoaderVersion = l.Loader.Version
            }).ToList();
        }
        catch
        {
            return Array.Empty<VersionVariant>();
        }
    }

    private static async Task<IReadOnlyList<VersionVariant>> GetQuiltVariantsAsync(
        string gameVersionId,
        bool recommendedOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(gameVersionId)}";
            var loaders = await Http.GetFromJsonAsync<List<QuiltLoaderEntry>>(url, cancellationToken)
                .ConfigureAwait(false);

            if (loaders is null || loaders.Count == 0)
                return Array.Empty<VersionVariant>();

            var list = loaders
                .Where(l => l.Loader?.Version is not null)
                .Select(l => l!)
                .ToList();

            if (recommendedOnly)
                list = list.Take(1).ToList();

            return list.Select(l => new VersionVariant
            {
                Key = $"quilt:{gameVersionId}:{l.Loader!.Version}",
                GameVersionId = gameVersionId,
                Kind = LoaderKind.Quilt,
                DisplayName = $"Quilt {l.Loader.Version}",
                LoaderVersion = l.Loader.Version
            }).ToList();
        }
        catch
        {
            return Array.Empty<VersionVariant>();
        }
    }

    private static async Task<IReadOnlyList<VersionVariant>> GetOptiFineVariantsAsync(
        string gameVersionId,
        bool recommendedOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(gameVersionId)}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<VersionVariant>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<VersionVariant>();

            var entries = new List<(string Type, string Patch, string Label, bool IsPreview)>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                var patch = item.TryGetProperty("patch", out var p) ? p.GetString() : null;
                var filename = item.TryGetProperty("filename", out var f) ? f.GetString() : null;

                if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(patch))
                    continue;

                var isPreview =
                    (!string.IsNullOrEmpty(filename) &&
                     filename.StartsWith("preview_", StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(patch) &&
                     patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase));

                var label = string.Join(' ',
                    new[] { type, patch }.Where(s => !string.IsNullOrWhiteSpace(s)));

                entries.Add((type ?? "", patch ?? "", label, isPreview));
            }

            if (entries.Count == 0)
                return Array.Empty<VersionVariant>();

            if (recommendedOnly)
            {
                // Последняя стабильная (не preview)
                var stable = entries.LastOrDefault(e => !e.IsPreview);
                if (stable.Label is null or "")
                    return Array.Empty<VersionVariant>();

                return new[]
                {
                    new VersionVariant
                    {
                        Key = $"optifine:{gameVersionId}:{stable.Label}",
                        GameVersionId = gameVersionId,
                        Kind = LoaderKind.OptiFine,
                        DisplayName = $"OptiFine {stable.Label}".Trim(),
                        LoaderVersion = stable.Label
                    }
                };
            }

            // Полный список: newest last in API often — reverse for newest first
            return entries
                .AsEnumerable()
                .Reverse()
                .Select(e => new VersionVariant
                {
                    Key = $"optifine:{gameVersionId}:{e.Label}",
                    GameVersionId = gameVersionId,
                    Kind = LoaderKind.OptiFine,
                    DisplayName = e.IsPreview
                        ? $"OptiFine {e.Label} (preview)".Trim()
                        : $"OptiFine {e.Label}".Trim(),
                    LoaderVersion = e.Label
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<VersionVariant>();
        }
    }

    private static async Task<IReadOnlyList<string>> LoadMavenVersionsAsync(
        string metadataUrl,
        CancellationToken cancellationToken)
    {
        var xml = await Http.GetStringAsync(metadataUrl, cancellationToken).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        return doc.Root?
                   .Element("versioning")?
                   .Element("versions")?
                   .Elements("version")
                   .Select(e => e.Value)
                   .Where(v => !string.IsNullOrWhiteSpace(v))
                   .ToList()
               ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private sealed class FabricLoaderEntry
    {
        [JsonPropertyName("loader")]
        public FabricLoaderInfo? Loader { get; set; }
    }

    private sealed class FabricLoaderInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class QuiltLoaderEntry
    {
        [JsonPropertyName("loader")]
        public QuiltLoaderInfo? Loader { get; set; }
    }

    private sealed class QuiltLoaderInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
