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
using ZLauncher.Models;

namespace ZLauncher.Services;

/// <summary>Поиск и загрузка данных модов с Modrinth API v2.</summary>
public sealed class ModrinthService
{
    public const string ApiBase = "https://api.modrinth.com/v2";

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // User-Agent обязателен: https://docs.modrinth.com
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ZLauncher/1.0 (https://github.com/zlauncher; contact@local)");
        return client;
    }

    /// <param name="query">Поисковая строка (пустая = популярные).</param>
    /// <param name="gameVersion">Фильтр по версии MC, напр. 1.20.1; null = все.</param>
    /// <param name="index">relevance | downloads | follows | newest | updated</param>
    public Task<ModrinthSearchResult> SearchModsAsync(
        string? query = null,
        string? gameVersion = null,
        string index = "downloads",
        int offset = 0,
        int limit = 24,
        CancellationToken ct = default) =>
        SearchProjectsAsync("mod", query, gameVersion, index, offset, limit, ct);

    /// <summary>Поиск ресурспаков на Modrinth (project_type:resourcepack).</summary>
    public Task<ModrinthSearchResult> SearchResourcePacksAsync(
        string? query = null,
        string? gameVersion = null,
        string index = "downloads",
        int offset = 0,
        int limit = 24,
        CancellationToken ct = default) =>
        SearchProjectsAsync("resourcepack", query, gameVersion, index, offset, limit, ct);

    /// <summary>Поиск шейдеров на Modrinth (project_type:shader).</summary>
    public Task<ModrinthSearchResult> SearchShadersAsync(
        string? query = null,
        string? gameVersion = null,
        string index = "downloads",
        int offset = 0,
        int limit = 24,
        CancellationToken ct = default) =>
        SearchProjectsAsync("shader", query, gameVersion, index, offset, limit, ct);

    public async Task<ModrinthSearchResult> SearchProjectsAsync(
        string projectType,
        string? query = null,
        string? gameVersion = null,
        string index = "downloads",
        int offset = 0,
        int limit = 24,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);

        var type = string.IsNullOrWhiteSpace(projectType) ? "mod" : projectType.Trim().ToLowerInvariant();
        var facets = new List<string[]>
        {
            new[] { $"project_type:{type}" }
        };
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add(new[] { $"versions:{gameVersion.Trim()}" });

        var facetsJson = JsonSerializer.Serialize(facets);
        var q = string.IsNullOrWhiteSpace(query) ? "" : query.Trim();

        var url =
            $"{ApiBase}/search?limit={limit}&offset={offset}&index={Uri.EscapeDataString(index)}"
            + $"&facets={Uri.EscapeDataString(facetsJson)}";
        if (!string.IsNullOrEmpty(q))
            url += $"&query={Uri.EscapeDataString(q)}";

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var raw = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonOpts, ct)
            .ConfigureAwait(false);

        if (raw?.Hits is null)
            return new ModrinthSearchResult([], 0, offset, offset);

        var mods = raw.Hits.Select(MapHit).ToList();
        return new ModrinthSearchResult(mods, raw.TotalHits, raw.Offset, raw.Offset + mods.Count);
    }

    /// <summary>Скачать иконку мода в Bitmap (или null).</summary>
    public async Task<Bitmap?> LoadIconAsync(string? iconUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return null;

        try
        {
            using var resp = await Http.GetAsync(iconUrl, ct).ConfigureAwait(false);
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

    /// <summary>Папка mods: %AppData%/ZLauncher/game/mods</summary>
    public static string GetModsDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher", "game", "mods");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Папка resourcepacks: %AppData%/ZLauncher/game/resourcepacks</summary>
    public static string GetResourcePacksDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher", "game", "resourcepacks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Папка shaderpacks: %AppData%/ZLauncher/game/shaderpacks</summary>
    public static string GetShaderPacksDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher", "game", "shaderpacks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ModsIndexPath =>
        Path.Combine(GetModsDirectory(), ".zlauncher-mods.json");

    private static string ResourcePacksIndexPath =>
        Path.Combine(GetResourcePacksDirectory(), ".zlauncher-resourcepacks.json");

    private static string ShaderPacksIndexPath =>
        Path.Combine(GetShaderPacksDirectory(), ".zlauncher-shaderpacks.json");

    /// <summary>Уже установлен ли мод (по индексу ZLauncher).</summary>
    public bool IsModInstalled(string projectId) =>
        IsProjectInstalled(projectId, GetModsDirectory(), ModsIndexPath);

    /// <summary>Уже установлен ли ресурспак.</summary>
    public bool IsResourcePackInstalled(string projectId) =>
        IsProjectInstalled(projectId, GetResourcePacksDirectory(), ResourcePacksIndexPath);

    /// <summary>Уже установлен ли шейдер.</summary>
    public bool IsShaderInstalled(string projectId) =>
        IsProjectInstalled(projectId, GetShaderPacksDirectory(), ShaderPacksIndexPath);

    private static bool IsProjectInstalled(string projectId, string contentDir, string indexPath)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return false;

        try
        {
            var index = ReadIndex(indexPath);
            if (!index.TryGetValue(projectId, out var fileName) || string.IsNullOrWhiteSpace(fileName))
                return false;
            return File.Exists(Path.Combine(contentDir, fileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Скачать подходящую версию мода в game/mods.
    /// Подбирает файл под MC-версию и лоадер (если заданы).
    /// </summary>
    public Task<ModInstallResult> InstallModAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        InstallProjectAsync(
            projectIdOrSlug,
            GetModsDirectory(),
            ModsIndexPath,
            gameVersion,
            loader,
            preferZip: false,
            progress,
            ct);

    /// <summary>Скачать ресурспак в game/resourcepacks (zip, без лоадера).</summary>
    public Task<ModInstallResult> InstallResourcePackAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        InstallProjectAsync(
            projectIdOrSlug,
            GetResourcePacksDirectory(),
            ResourcePacksIndexPath,
            gameVersion,
            loader: null,
            preferZip: true,
            progress,
            ct);

    /// <summary>Скачать шейдер в game/shaderpacks (zip, без лоадера).</summary>
    public Task<ModInstallResult> InstallShaderAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        InstallProjectAsync(
            projectIdOrSlug,
            GetShaderPacksDirectory(),
            ShaderPacksIndexPath,
            gameVersion,
            loader: null,
            preferZip: true,
            progress,
            ct);

    private async Task<ModInstallResult> InstallProjectAsync(
        string projectIdOrSlug,
        string contentDir,
        string indexPath,
        string? gameVersion,
        string? loader,
        bool preferZip,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectIdOrSlug))
            throw new ArgumentException("Не указан проект.", nameof(projectIdOrSlug));

        var version = await ResolveBestVersionAsync(projectIdOrSlug, gameVersion, loader, ct)
            .ConfigureAwait(false);

        ModrinthVersionFile? file = null;
        if (preferZip)
        {
            file = version.Files.FirstOrDefault(f => f.Primary && f.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? version.Files.FirstOrDefault(f => f.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? version.Files.FirstOrDefault(f => f.Primary)
                   ?? version.Files.FirstOrDefault();
        }
        else
        {
            file = version.Files.FirstOrDefault(f => f.Primary)
                   ?? version.Files.FirstOrDefault(f =>
                       f.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                   ?? version.Files.FirstOrDefault();
        }

        if (file is null || string.IsNullOrWhiteSpace(file.Url))
            throw new InvalidOperationException("У версии нет файла для скачивания.");

        Directory.CreateDirectory(contentDir);
        var safeName = SanitizeFileName(file.Filename);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = preferZip ? $"{projectIdOrSlug}.zip" : $"{projectIdOrSlug}.jar";

        var dest = Path.Combine(contentDir, safeName);
        var temp = dest + ".partial";

        try
        {
            using var resp = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using (var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        progress?.Report(Math.Clamp(done / (double)total, 0, 1));
                }
            }

            if (File.Exists(dest))
                File.Delete(dest);
            File.Move(temp, dest);

            var index = ReadIndex(indexPath);
            if (index.TryGetValue(projectIdOrSlug, out var oldName)
                && !string.Equals(oldName, safeName, StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = Path.Combine(contentDir, oldName);
                try
                {
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);
                }
                catch
                {
                    // ignore
                }
            }

            index[projectIdOrSlug] = safeName;
            WriteIndex(indexPath, index);
            progress?.Report(1);

            return new ModInstallResult(dest, safeName, version.VersionNumber, version.Name);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    public async Task<ModrinthVersionInfo> ResolveBestVersionAsync(
        string projectIdOrSlug,
        string? gameVersion,
        string? loader,
        CancellationToken ct = default)
    {
        // 1) С фильтрами game + loader
        var versions = await FetchVersionsAsync(projectIdOrSlug, gameVersion, loader, ct)
            .ConfigureAwait(false);

        // 2) Только game version
        if (versions.Count == 0 && !string.IsNullOrWhiteSpace(loader))
            versions = await FetchVersionsAsync(projectIdOrSlug, gameVersion, null, ct)
                .ConfigureAwait(false);

        // 3) Без фильтров
        if (versions.Count == 0)
            versions = await FetchVersionsAsync(projectIdOrSlug, null, null, ct)
                .ConfigureAwait(false);

        if (versions.Count == 0)
            throw new InvalidOperationException(
                "Не найдена подходящая версия для выбранной сборки Minecraft.");

        // release > beta > alpha; затем по дате
        static int Rank(string? t) => t?.ToLowerInvariant() switch
        {
            "release" => 0,
            "beta" => 1,
            "alpha" => 2,
            _ => 3
        };

        // Предпочитаем версии, где имя файла / version_number совпадает с MC
        // (у XaeroLib API иногда врёт: forge-1.16.5-*.jar с game_versions=["1.12.2"])
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            var matched = versions
                .Where(v => VersionTextMatchesGame(v, gameVersion))
                .ToList();
            if (matched.Count > 0)
                versions = matched;
        }

        return versions
            .OrderBy(v => Rank(v.VersionType))
            .ThenByDescending(v => v.DatePublished)
            .First();
    }

    /// <summary>
    /// true, если version_number / filename явно указывают на gameVersion
    /// (или не указывают другую 1.x — тогда не отсекаем).
    /// </summary>
    private static bool VersionTextMatchesGame(ModrinthVersionInfo v, string gameVersion)
    {
        var texts = new List<string>();
        if (!string.IsNullOrWhiteSpace(v.VersionNumber))
            texts.Add(v.VersionNumber);
        if (!string.IsNullOrWhiteSpace(v.Name))
            texts.Add(v.Name);
        foreach (var f in v.Files)
        {
            if (!string.IsNullOrWhiteSpace(f.Filename))
                texts.Add(f.Filename);
        }

        var hints = new List<string>();
        foreach (var t in texts)
        {
            foreach (var h in ExtractMcVersionHints(t))
            {
                if (!hints.Contains(h, StringComparer.OrdinalIgnoreCase))
                    hints.Add(h);
            }
        }

        // Нет явных MC-версий в имени — оставляем кандидата (решает game_versions API)
        if (hints.Count == 0)
            return true;

        return hints.Any(h => McVersionHintMatches(h, gameVersion));
    }

    private static List<string> ExtractMcVersionHints(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text,
                     @"(?:^|[^a-z0-9])(?:mc|forge|fabric|quilt|neoforge)[-_]?([1]\.\d{1,2}(?:\.\d{1,2})?)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var v = m.Groups[1].Value;
            if (!result.Contains(v, StringComparer.OrdinalIgnoreCase))
                result.Add(v);
        }

        if (result.Count > 0)
            return result;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"\b(1\.\d{1,2}\.\d{1,2})\b"))
        {
            var v = m.Groups[1].Value;
            if (!result.Contains(v, StringComparer.OrdinalIgnoreCase))
                result.Add(v);
        }

        return result;
    }

    private static bool McVersionHintMatches(string hint, string gameVersionId)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return true;
        hint = hint.Trim();
        if (string.Equals(hint, gameVersionId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (hint.Contains(gameVersionId, StringComparison.OrdinalIgnoreCase))
            return true;

        // 1.12 ≈ 1.12.2
        var gm = System.Text.RegularExpressions.Regex.Match(gameVersionId, @"^(?<a>\d+)\.(?<b>\d+)");
        var hm = System.Text.RegularExpressions.Regex.Match(hint, @"^(?<a>\d+)\.(?<b>\d+)");
        if (gm.Success && hm.Success)
            return gm.Groups["a"].Value == hm.Groups["a"].Value &&
                   gm.Groups["b"].Value == hm.Groups["b"].Value;

        return false;
    }

    private async Task<List<ModrinthVersionInfo>> FetchVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion,
        string? loader,
        CancellationToken ct)
    {
        var url = $"{ApiBase}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            qs.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion.Trim() })));
        if (!string.IsNullOrWhiteSpace(loader))
            qs.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.Trim().ToLowerInvariant() })));
        if (qs.Count > 0)
            url += "?" + string.Join("&", qs);

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new List<ModrinthVersionInfo>();
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var raw = await JsonSerializer.DeserializeAsync<List<VersionDto>>(stream, JsonOpts, ct)
            .ConfigureAwait(false);
        if (raw is null || raw.Count == 0)
            return new List<ModrinthVersionInfo>();

        return raw.Select(MapVersion).ToList();
    }

    /// <summary>
    /// Пакетно загрузить проекты по id (до ~100): title, icon, downloads, slug…
    /// GET /v2/projects?ids=["…"]
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModrinthModInfo>> GetProjectsByIdsAsync(
        IEnumerable<string> projectIds,
        CancellationToken ct = default)
    {
        var ids = projectIds
            .Where(id => !string.IsNullOrWhiteSpace(id)
                         && !id.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, ModrinthModInfo>(StringComparer.OrdinalIgnoreCase);

        var idsJson = JsonSerializer.Serialize(ids);
        var url = $"{ApiBase}/projects?ids={Uri.EscapeDataString(idsJson)}";

        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Dictionary<string, ModrinthModInfo>(StringComparer.OrdinalIgnoreCase);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var raw = await JsonSerializer.DeserializeAsync<List<ProjectDto>>(stream, JsonOpts, ct)
                .ConfigureAwait(false);

            var result = new Dictionary<string, ModrinthModInfo>(StringComparer.OrdinalIgnoreCase);
            if (raw is null)
                return result;

            foreach (var p in raw)
            {
                if (string.IsNullOrWhiteSpace(p.Id))
                    continue;

                var info = MapProject(p);
                result[p.Id] = info;
                if (!string.IsNullOrWhiteSpace(p.Slug) &&
                    !result.ContainsKey(p.Slug))
                    result[p.Slug] = info;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, ModrinthModInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static ModrinthModInfo MapProject(ProjectDto p)
    {
        IReadOnlyList<string> categories =
            p.Categories is { Count: > 0 } c ? c : Array.Empty<string>();
        IReadOnlyList<string> versions =
            p.GameVersions is { Count: > 0 } v ? v : Array.Empty<string>();

        return new ModrinthModInfo
        {
            ProjectId = p.Id ?? "",
            Slug = p.Slug ?? p.Id ?? "",
            Title = string.IsNullOrWhiteSpace(p.Title) ? (p.Slug ?? "Project") : p.Title!,
            Description = p.Description?.Trim() ?? "",
            IconUrl = p.IconUrl,
            Author = "—",
            Downloads = p.Downloads,
            Follows = p.Followers,
            ProjectType = p.ProjectType ?? "mod",
            Categories = categories,
            Versions = versions,
            LatestVersion = versions.FirstOrDefault()
        };
    }

    /// <summary>Найти версии модов по SHA1 файлов (пакетно, до 1000).</summary>
    public async Task<IReadOnlyDictionary<string, ModrinthVersionInfo>> GetVersionsByHashesAsync(
        IEnumerable<string> sha1Hashes,
        CancellationToken ct = default)
    {
        var hashes = sha1Hashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant())
            .Distinct()
            .Take(200)
            .ToList();

        if (hashes.Count == 0)
            return new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);

        var body = JsonSerializer.Serialize(new { hashes, algorithm = "sha1" });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"{ApiBase}/version_files", content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var map = await JsonSerializer
            .DeserializeAsync<Dictionary<string, VersionDto>>(stream, JsonOpts, ct)
            .ConfigureAwait(false);

        var result = new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);
        if (map is null)
            return result;

        foreach (var (hash, dto) in map)
        {
            if (dto is null) continue;
            result[hash] = MapVersion(dto);
        }

        return result;
    }

    private static ModrinthVersionInfo MapVersion(VersionDto v) => new()
    {
        Id = v.Id ?? "",
        ProjectId = v.ProjectId ?? "",
        Name = v.Name ?? "",
        VersionNumber = v.VersionNumber ?? "",
        VersionType = v.VersionType ?? "release",
        DatePublished = v.DatePublished ?? DateTimeOffset.MinValue,
        Loaders = v.Loaders ?? new List<string>(),
        GameVersions = v.GameVersions ?? new List<string>(),
        Files = (v.Files ?? new List<VersionFileDto>()).Select(f => new ModrinthVersionFile
        {
            Url = f.Url ?? "",
            Filename = f.Filename ?? "",
            Primary = f.Primary,
            Size = f.Size
        }).ToList(),
        Dependencies = (v.Dependencies ?? new List<VersionDepDto>()).Select(d => new ModrinthDependency
        {
            ProjectId = d.ProjectId,
            VersionId = d.VersionId,
            FileName = d.FileName,
            DependencyType = d.DependencyType ?? "required"
        }).ToList()
    };

    private static Dictionary<string, string> ReadIndex(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dict is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteIndex(string path, Dictionary<string, string> index)
    {
        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static ModrinthModInfo MapHit(SearchHit h)
    {
        IReadOnlyList<string> categories =
            h.DisplayCategories is { Count: > 0 } dc ? dc
            : h.Categories is { Count: > 0 } c ? c
            : Array.Empty<string>();

        IReadOnlyList<string> versions =
            h.Versions is { Count: > 0 } v ? v : Array.Empty<string>();

        return new ModrinthModInfo
        {
            ProjectId = h.ProjectId ?? "",
            Slug = h.Slug ?? h.ProjectId ?? "",
            Title = string.IsNullOrWhiteSpace(h.Title) ? (h.Slug ?? "Mod") : h.Title!,
            Description = h.Description?.Trim() ?? "",
            IconUrl = h.IconUrl,
            Author = h.Author ?? "—",
            Downloads = h.Downloads,
            Follows = h.Follows,
            ProjectType = h.ProjectType ?? "mod",
            Categories = categories,
            Versions = versions,
            LatestVersion = h.LatestVersion
        };
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")]
        public List<SearchHit>? Hits { get; set; }

        [JsonPropertyName("offset")]
        public int Offset { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("total_hits")]
        public int TotalHits { get; set; }
    }

    private sealed class ProjectDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        [JsonPropertyName("followers")]
        public long Followers { get; set; }

        [JsonPropertyName("project_type")]
        public string? ProjectType { get; set; }

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }

        [JsonPropertyName("game_versions")]
        public List<string>? GameVersions { get; set; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        [JsonPropertyName("follows")]
        public long Follows { get; set; }

        [JsonPropertyName("project_type")]
        public string? ProjectType { get; set; }

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }

        [JsonPropertyName("display_categories")]
        public List<string>? DisplayCategories { get; set; }

        [JsonPropertyName("versions")]
        public List<string>? Versions { get; set; }

        [JsonPropertyName("latest_version")]
        public string? LatestVersion { get; set; }
    }

    private sealed class VersionDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }

        [JsonPropertyName("version_type")]
        public string? VersionType { get; set; }

        [JsonPropertyName("date_published")]
        public DateTimeOffset? DatePublished { get; set; }

        [JsonPropertyName("loaders")]
        public List<string>? Loaders { get; set; }

        [JsonPropertyName("game_versions")]
        public List<string>? GameVersions { get; set; }

        [JsonPropertyName("files")]
        public List<VersionFileDto>? Files { get; set; }

        [JsonPropertyName("dependencies")]
        public List<VersionDepDto>? Dependencies { get; set; }
    }

    private sealed class VersionDepDto
    {
        [JsonPropertyName("version_id")]
        public string? VersionId { get; set; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("file_name")]
        public string? FileName { get; set; }

        [JsonPropertyName("dependency_type")]
        public string? DependencyType { get; set; }
    }

    private sealed class VersionFileDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed record ModrinthSearchResult(
    IReadOnlyList<ModrinthModInfo> Mods,
    int TotalHits,
    int Offset,
    int NextOffset);

public sealed class ModrinthVersionInfo
{
    public required string Id { get; init; }
    public string ProjectId { get; init; } = "";
    public required string Name { get; init; }
    public required string VersionNumber { get; init; }
    public required string VersionType { get; init; }
    public DateTimeOffset DatePublished { get; init; }
    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ModrinthVersionFile> Files { get; init; } = Array.Empty<ModrinthVersionFile>();
    public IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = Array.Empty<ModrinthDependency>();
}

public sealed class ModrinthDependency
{
    public string? ProjectId { get; init; }
    public string? VersionId { get; init; }
    public string? FileName { get; init; }
    public string DependencyType { get; init; } = "required";
}

public sealed class ModrinthVersionFile
{
    public required string Url { get; init; }
    public required string Filename { get; init; }
    public bool Primary { get; init; }
    public long Size { get; init; }
}

public sealed record ModInstallResult(
    string FilePath,
    string FileName,
    string VersionNumber,
    string VersionName);
