using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ZLauncher.Models;

namespace ZLauncher.Services;

public sealed class MojangVersionService
{
    public const string ManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZLauncher/1.0");
        return client;
    }

    public async Task<IReadOnlyList<MinecraftVersion>> GetVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await Http
            .GetFromJsonAsync<VersionManifest>(ManifestUrl, cancellationToken)
            .ConfigureAwait(false);

        if (manifest?.Versions is null || manifest.Versions.Count == 0)
            throw new InvalidOperationException("Mojang API вернул пустой список версий.");

        return manifest.Versions
            .Select(v => new MinecraftVersion
            {
                Id = v.Id,
                Type = v.Type,
                Url = v.Url,
                ReleaseTime = v.ReleaseTime
            })
            .ToList();
    }

    private sealed class VersionManifest
    {
        [JsonPropertyName("versions")]
        public List<VersionEntry>? Versions { get; set; }
    }

    private sealed class VersionEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("releaseTime")]
        public DateTimeOffset? ReleaseTime { get; set; }
    }
}
