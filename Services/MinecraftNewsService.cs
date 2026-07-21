using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ZLauncher.Services;

/// <summary>
/// Новости Minecraft из официального API лаунчера Mojang.
/// Источник: https://launchercontent.mojang.com/news.json
/// </summary>
public sealed class MinecraftNewsService
{
    public const string NewsUrl = "https://launchercontent.mojang.com/news.json";
    public const string ImageBase = "https://launchercontent.mojang.com";

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ZLauncher/1.0 (https://github.com/zlauncher; contact@local)");
        return client;
    }

    public async Task<IReadOnlyList<MinecraftNewsItem>> GetNewsAsync(
        int limit = 12,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 40);

        using var resp = await Http.GetAsync(NewsUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var raw = await JsonSerializer.DeserializeAsync<NewsRoot>(stream, JsonOpts, ct)
            .ConfigureAwait(false);

        if (raw?.Entries is null || raw.Entries.Count == 0)
            return Array.Empty<MinecraftNewsItem>();

        return raw.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .OrderByDescending(e => ParseDate(e.Date))
            .Take(limit)
            .Select(Map)
            .ToList();
    }

    public async Task<Bitmap?> LoadImageAsync(string? absoluteOrRelativeUrl, CancellationToken ct = default)
    {
        var url = NormalizeImageUrl(absoluteOrRelativeUrl);
        if (url is null)
            return null;

        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var ms = new MemoryStream();
            await net.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public static string? NormalizeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (!url.StartsWith('/'))
            url = "/" + url;
        return ImageBase + url;
    }

    private static MinecraftNewsItem Map(NewsEntry e)
    {
        var imageRel = e.NewsPageImage?.Url ?? e.PlayPageImage?.Url;
        return new MinecraftNewsItem
        {
            Id = e.Id ?? Guid.NewGuid().ToString("N"),
            Title = e.Title?.Trim() ?? "Новость",
            Tag = string.IsNullOrWhiteSpace(e.Tag) ? (e.Category ?? "Minecraft") : e.Tag!,
            Category = e.Category?.Trim() ?? "",
            Date = e.Date?.Trim() ?? "",
            Text = e.Text?.Trim() ?? "",
            ReadMoreUrl = string.IsNullOrWhiteSpace(e.ReadMoreLink)
                ? "https://www.minecraft.net/en-us/article"
                : e.ReadMoreLink!.Trim(),
            ImageUrl = NormalizeImageUrl(imageRel)
        };
    }

    private static DateTimeOffset ParseDate(string? date)
    {
        if (DateTimeOffset.TryParse(date, out var dto))
            return dto;
        return DateTimeOffset.MinValue;
    }

    private sealed class NewsRoot
    {
        [JsonPropertyName("entries")]
        public List<NewsEntry>? Entries { get; set; }
    }

    private sealed class NewsEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("readMoreLink")]
        public string? ReadMoreLink { get; set; }

        [JsonPropertyName("newsPageImage")]
        public NewsImage? NewsPageImage { get; set; }

        [JsonPropertyName("playPageImage")]
        public NewsImage? PlayPageImage { get; set; }
    }

    private sealed class NewsImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}

public sealed class MinecraftNewsItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Tag { get; init; }
    public required string Category { get; init; }
    public required string Date { get; init; }
    public required string Text { get; init; }
    public required string ReadMoreUrl { get; init; }
    public string? ImageUrl { get; init; }
}
