using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ZLauncher.Models;

namespace ZLauncher.Services;

/// <summary>
/// Полноценная установка Minecraft + подверсий (Vanilla / Fabric / Quilt / Forge / NeoForge / OptiFine)
/// из публичных meta/maven API.
/// </summary>
public sealed class GameInstallService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _root;
    private readonly string _versionsRoot;
    private readonly string _librariesRoot;
    private readonly string _assetsRoot;
    private readonly string _tempRoot;
    private readonly JavaRuntimeService _javaRuntime = new();

    public GameInstallService()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher");

        _versionsRoot = Path.Combine(_root, "versions");
        _librariesRoot = Path.Combine(_root, "libraries");
        _assetsRoot = Path.Combine(_root, "assets");
        _tempRoot = Path.Combine(_root, "temp");

        Directory.CreateDirectory(_versionsRoot);
        Directory.CreateDirectory(_librariesRoot);
        Directory.CreateDirectory(_assetsRoot);
        Directory.CreateDirectory(_tempRoot);
    }

    public JavaRuntimeService JavaRuntime => _javaRuntime;

    public string GameRoot => _root;
    public string VersionsRoot => _versionsRoot;

    public string GetInstallDirectory(VersionVariant variant) =>
        Path.Combine(_versionsRoot, Sanitize(GetVersionFolderName(variant)));

    /// <summary>
    /// Сканирует %AppData%/ZLauncher/versions: папки с profile JSON, которых нет в списке Mojang,
    /// показываются как кастомные версии (пользователь «закинул» свою сборку).
    /// </summary>
    public IReadOnlyList<MinecraftVersion> DiscoverCustomVersions(ISet<string>? knownMojangIds = null)
    {
        var list = new List<MinecraftVersion>();
        knownMojangIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(_versionsRoot))
                return list;

            foreach (var folder in Directory.EnumerateDirectories(_versionsRoot))
            {
                try
                {
                    var folderName = Path.GetFileName(folder);
                    if (string.IsNullOrWhiteSpace(folderName))
                        continue;

                    var jsonPath = FindVersionProfileJson(folder);
                    if (jsonPath is null)
                        continue;

                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var root = doc.RootElement;

                    // Нужен mainClass или libraries / downloads — иначе это не profile MC
                    var hasMain = root.TryGetProperty("mainClass", out _);
                    var hasLibs = root.TryGetProperty("libraries", out var libs) &&
                                  libs.ValueKind == JsonValueKind.Array &&
                                  libs.GetArrayLength() > 0;
                    var hasClient = root.TryGetProperty("downloads", out var dl) &&
                                    dl.ValueKind == JsonValueKind.Object &&
                                    dl.TryGetProperty("client", out _);
                    if (!hasMain && !hasLibs && !hasClient)
                        continue;

                    var id = root.TryGetProperty("id", out var idEl)
                        ? idEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(id))
                        id = folderName;

                    // Папки наших лоадеров / vanilla Mojang — не top-level custom
                    var markerKind = TryReadInstalledKind(folder);
                    var isExplicitCustom = string.Equals(markerKind, "Custom", StringComparison.OrdinalIgnoreCase);

                    if (!isExplicitCustom)
                    {
                        if (IsKnownLoaderFolderName(folderName))
                            continue;

                        // vanilla: versions/1.20.1/1.20.1.json
                        if (knownMojangIds.Contains(folderName) &&
                            string.Equals(folderName, id, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // id из Mojang, папка = id
                        if (knownMojangIds.Contains(id!) &&
                            string.Equals(folderName, id, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    DateTimeOffset? release = null;
                    try
                    {
                        release = Directory.GetLastWriteTimeUtc(folder);
                    }
                    catch
                    {
                        // ignore
                    }

                    list.Add(new MinecraftVersion
                    {
                        Id = id!,
                        Type = "custom",
                        Url = null,
                        ReleaseTime = release,
                        CustomFolderName = folderName
                    });

                    // Чтобы «Играть» работал — дописываем .installed, если есть jar/json
                    EnsureCustomInstalledMarker(folder, id!, folderName);
                }
                catch
                {
                    // skip broken folder
                }
            }
        }
        catch
        {
            // ignore scan errors
        }

        return list
            .OrderByDescending(v => v.ReleaseTime)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Единственный вариант для кастомной папки.</summary>
    public static VersionVariant CreateCustomVariant(MinecraftVersion version)
    {
        var folder = version.CustomFolderName ?? version.Id;
        return new VersionVariant
        {
            Key = $"custom:{folder}",
            GameVersionId = version.Id,
            Kind = LoaderKind.Custom,
            DisplayName = folder,
            LoaderVersion = folder
        };
    }

    private static string? FindVersionProfileJson(string folder)
    {
        var folderName = Path.GetFileName(folder);
        var preferred = Path.Combine(folder, folderName + ".json");
        if (File.Exists(preferred))
            return preferred;

        foreach (var f in Directory.EnumerateFiles(folder, "*.json"))
        {
            var name = Path.GetFileName(f);
            if (name.Equals(".installed", StringComparison.OrdinalIgnoreCase))
                continue;
            // отсекаем явно служебные
            if (name.StartsWith(".", StringComparison.Ordinal))
                continue;
            return f;
        }

        return null;
    }

    private static string? TryReadInstalledKind(string folder)
    {
        try
        {
            var marker = Path.Combine(folder, ".installed");
            if (!File.Exists(marker))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            if (doc.RootElement.TryGetProperty("Kind", out var k))
                return k.GetString();
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool IsKnownLoaderFolderName(string folderName)
    {
        return folderName.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase)
               || folderName.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase)
               || folderName.Contains("-forge-", StringComparison.OrdinalIgnoreCase)
               || folderName.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)
               || folderName.Contains("-OptiFine-", StringComparison.OrdinalIgnoreCase)
               || folderName.Contains("-optifine-", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureCustomInstalledMarker(string folder, string gameId, string folderName)
    {
        try
        {
            var hasJar = Directory.EnumerateFiles(folder, "*.jar").Any();
            var hasJson = FindVersionProfileJson(folder) is not null;
            if (!hasJar && !hasJson)
                return;

            var marker = Path.Combine(folder, ".installed");
            if (File.Exists(marker))
            {
                // не перезаписываем чужие Kind
                var kind = TryReadInstalledKind(folder);
                if (!string.IsNullOrEmpty(kind) &&
                    !string.Equals(kind, "Custom", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            var meta = new
            {
                Key = $"custom:{folderName}",
                GameVersionId = gameId,
                Kind = "Custom",
                DisplayName = folderName,
                LoaderVersion = folderName,
                VersionId = folderName,
                InstalledAt = DateTimeOffset.UtcNow
            };
            File.WriteAllText(marker, JsonSerializer.Serialize(meta, JsonOpts));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Установленные сборки из маркеров .installed (для быстрого выбора на главной).
    /// </summary>
    public IReadOnlyList<InstalledBuildInfo> ListInstalledBuilds()
    {
        var list = new List<InstalledBuildInfo>();
        try
        {
            if (!Directory.Exists(_versionsRoot))
                return list;

            foreach (var folder in Directory.EnumerateDirectories(_versionsRoot))
            {
                var marker = Path.Combine(folder, ".installed");
                if (!File.Exists(marker))
                    continue;

                try
                {
                    var json = File.ReadAllText(marker);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var key = root.TryGetProperty("Key", out var k) ? k.GetString() : null;
                    var gameId = root.TryGetProperty("GameVersionId", out var g) ? g.GetString() : null;
                    var kind = root.TryGetProperty("Kind", out var ki) ? ki.GetString() : null;
                    var display = root.TryGetProperty("DisplayName", out var d) ? d.GetString() : null;
                    var loader = root.TryGetProperty("LoaderVersion", out var l) ? l.GetString() : null;

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(gameId))
                        continue;

                    // Vanilla-база без отдельного выбора лоадера — всё равно в списке
                    if (!Directory.EnumerateFiles(folder, "*.json").Any())
                        continue;

                    list.Add(new InstalledBuildInfo
                    {
                        Key = key!,
                        GameVersionId = gameId!,
                        Kind = kind ?? "Vanilla",
                        DisplayName = display ?? kind ?? "Сборка",
                        LoaderVersion = loader,
                        FolderName = Path.GetFileName(folder)
                    });
                }
                catch
                {
                    // skip broken marker
                }
            }
        }
        catch
        {
            // ignore
        }

        return list
            .OrderByDescending(b => b.GameVersionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Kind, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsInstalled(VersionVariant? variant)
    {
        if (variant is null)
            return false;

        var dir = GetInstallDirectory(variant);
        if (Directory.Exists(dir) &&
            File.Exists(Path.Combine(dir, ".installed")) &&
            (File.Exists(Path.Combine(dir, $"{Sanitize(GetVersionFolderName(variant))}.json"))
             || Directory.EnumerateFiles(dir, "*.json").Any()))
            return true;

        // Fallback: искать по Key в любом .installed (если папка/label чуть отличается)
        try
        {
            if (!Directory.Exists(_versionsRoot))
                return false;

            foreach (var folder in Directory.EnumerateDirectories(_versionsRoot))
            {
                var marker = Path.Combine(folder, ".installed");
                if (!File.Exists(marker))
                    continue;

                var text = File.ReadAllText(marker);
                if (text.Contains($"\"Key\":\"{variant.Key}\"", StringComparison.Ordinal) ||
                    text.Contains($"\"Key\": \"{variant.Key}\"", StringComparison.Ordinal))
                    return Directory.EnumerateFiles(folder, "*.json").Any();
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public async Task InstallAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        DebugLogService.Instance.Log(
            $"InstallAsync start · {variant.Kind} · {gameVersion.Id} · {variant.DisplayName} · key={variant.Key}");
        DebugLogService.Instance.Log($"Game root: {_root}");

        // Кастом: пользователь уже положил файлы в versions/ — только проверяем и пишем маркер
        if (variant.Kind == LoaderKind.Custom)
        {
            await InstallCustomAsync(gameVersion, variant, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(gameVersion.Url))
            throw new InvalidOperationException("У версии нет URL манифеста Mojang.");

        // Java нужна для Forge installer / будущего запуска — ставим автоматически, если нет
        await EnsureJavaForInstallAsync(progress, cancellationToken).ConfigureAwait(false);

        switch (variant.Kind)
        {
            case LoaderKind.Vanilla:
                await InstallVanillaAsync(gameVersion, variant, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case LoaderKind.Fabric:
                await InstallFabricLikeAsync(gameVersion, variant, isQuilt: false, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case LoaderKind.Quilt:
                await InstallFabricLikeAsync(gameVersion, variant, isQuilt: true, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case LoaderKind.Forge:
                await InstallForgeFamilyAsync(gameVersion, variant, neo: false, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case LoaderKind.NeoForge:
                await InstallForgeFamilyAsync(gameVersion, variant, neo: true, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case LoaderKind.OptiFine:
                await InstallOptiFineAsync(gameVersion, variant, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Установка {variant.Kind} пока не поддерживается.");
        }
    }

    private async Task InstallCustomAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        Report(progress, 0.1, 0, 0, 0, "Custom · проверка", variant.DisplayName);
        var dir = GetInstallDirectory(variant);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException(
                $"Папка версии не найдена: {dir}. Закиньте сборку в %AppData%\\ZLauncher\\versions\\");

        var json = FindVersionProfileJson(dir);
        if (json is null)
            throw new InvalidOperationException("В папке нет version profile (.json).");

        var hasJar = Directory.EnumerateFiles(dir, "*.jar").Any();
        if (!hasJar)
            DebugLogService.Instance.Log("Custom: jar не найден — запуск может потребовать inheritsFrom.");

        var folderName = Path.GetFileName(dir);
        EnsureCustomInstalledMarker(dir, gameVersion.Id, folderName);
        await WriteInstalledMarkerAsync(dir, variant, folderName, ct).ConfigureAwait(false);

        Report(progress, 1, 0, 0, 0, "Готово", null);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ───────────────────────────── Vanilla ─────────────────────────────

    private async Task InstallVanillaAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var folder = GetVersionFolderName(variant);
        var installDir = Path.Combine(_versionsRoot, Sanitize(folder));
        Directory.CreateDirectory(installDir);

        Report(progress, 0.02, 0, 0, 0, "Vanilla · манифест", gameVersion.Id);

        var versionJsonPath = Path.Combine(installDir, $"{Sanitize(folder)}.json");
        await DownloadFileAsync(gameVersion.Url!, versionJsonPath, ct).ConfigureAwait(false);

        await using var fs = File.OpenRead(versionJsonPath);
        var meta = await JsonSerializer.DeserializeAsync<VersionJson>(fs, JsonOpts, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Не удалось разобрать version.json.");

        var jobs = BuildVanillaJobs(gameVersion.Id, installDir, meta, out var assetIndexUrl, out var assetIndexId);

        if (!string.IsNullOrEmpty(assetIndexUrl) && !string.IsNullOrEmpty(assetIndexId))
        {
            Report(progress, 0.05, 0, 0, 0, "Vanilla · индекс ресурсов", assetIndexId);
            var indexPath = Path.Combine(_assetsRoot, "indexes", $"{assetIndexId}.json");
            await DownloadFileAsync(assetIndexUrl, indexPath, ct).ConfigureAwait(false);
            jobs.AddRange(BuildAssetJobs(indexPath));
        }

        await DownloadJobsAsync(jobs, progress, "Vanilla", ct).ConfigureAwait(false);
        await WriteInstalledMarkerAsync(installDir, variant, folder, ct).ConfigureAwait(false);
        Report(progress, 1, 0, 0, 0, "Готово", null);
    }

    /// <summary>Гарантирует, что vanilla-версия лежит в versions/{id}/ (для inheritsFrom).</summary>
    private async Task EnsureVanillaBaseAsync(
        MinecraftVersion gameVersion,
        IProgress<InstallProgress>? progress,
        CancellationToken ct,
        bool forceRedownload = false)
    {
        var id = gameVersion.Id;
        var dir = Path.Combine(_versionsRoot, Sanitize(id));
        var jar = Path.Combine(dir, $"{Sanitize(id)}.jar");
        var json = Path.Combine(dir, $"{Sanitize(id)}.json");
        var marker = Path.Combine(dir, ".installed");

        var intact = !forceRedownload &&
                     File.Exists(jar) &&
                     File.Exists(json) &&
                     await IsVanillaClientIntactAsync(jar, json, ct).ConfigureAwait(false);

        // jar+json целы — дописываем маркер и не качаем заново
        if (intact)
        {
            if (!File.Exists(marker))
            {
                var vanillaVariant = new VersionVariant
                {
                    Key = $"vanilla:{id}",
                    GameVersionId = id,
                    Kind = LoaderKind.Vanilla,
                    DisplayName = "Vanilla",
                    LoaderVersion = id
                };
                await WriteInstalledMarkerAsync(dir, vanillaVariant, id, ct).ConfigureAwait(false);
            }

            Report(progress, 0.55, 0, 0, 0, "Vanilla уже установлена", id);
            return;
        }

        if (forceRedownload || File.Exists(jar))
        {
            Report(progress, 0.08, 0, 0, 0,
                forceRedownload ? "Vanilla · переустановка" : "Vanilla · повреждена, качаем заново",
                id);
            try
            {
                if (File.Exists(jar)) File.Delete(jar);
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch
            {
                // ignore locked files
            }
        }

        var installVariant = new VersionVariant
        {
            Key = $"vanilla:{id}",
            GameVersionId = id,
            Kind = LoaderKind.Vanilla,
            DisplayName = "Vanilla",
            LoaderVersion = id
        };

        var mapped = new Progress<InstallProgress>(p =>
        {
            progress?.Report(new InstallProgress
            {
                Progress = p.Progress * 0.55,
                BytesDownloaded = p.BytesDownloaded,
                TotalBytes = p.TotalBytes,
                SpeedBytesPerSecond = p.SpeedBytesPerSecond,
                Stage = "Vanilla · " + p.Stage,
                CurrentFile = p.CurrentFile
            });
        });

        await InstallVanillaAsync(gameVersion, installVariant, mapped, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Проверка client jar: размер из version.json (если есть) + минимум 1 МБ.
    /// </summary>
    private static async Task<bool> IsVanillaClientIntactAsync(
        string jarPath,
        string jsonPath,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(jarPath) || !File.Exists(jsonPath))
                return false;

            var fi = new FileInfo(jarPath);
            if (fi.Length < 1_000_000)
                return false;

            await using var fs = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct)
                .ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("downloads", out var dls) &&
                dls.TryGetProperty("client", out var client) &&
                client.TryGetProperty("size", out var sizeEl) &&
                sizeEl.TryGetInt64(out var expected) &&
                expected > 0)
            {
                // допуск 0 — точное совпадение (Mojang size)
                if (fi.Length != expected)
                    return false;
            }

            return true;
        }
        catch
        {
            return File.Exists(jarPath) && new FileInfo(jarPath).Length > 1_000_000;
        }
    }

    // ───────────────────────────── Fabric / Quilt ─────────────────────────────

    private async Task InstallFabricLikeAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        bool isQuilt,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var loaderVer = variant.LoaderVersion
                        ?? throw new InvalidOperationException("Не указана версия загрузчика.");

        await EnsureVanillaBaseAsync(gameVersion, progress, ct).ConfigureAwait(false);

        var name = isQuilt ? "Quilt" : "Fabric";
        Report(progress, 0.58, 0, 0, 0, $"{name} · профиль", loaderVer);

        var profileUrl = isQuilt
            ? $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(gameVersion.Id)}/{Uri.EscapeDataString(loaderVer)}/profile/json"
            : $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameVersion.Id)}/{Uri.EscapeDataString(loaderVer)}/profile/json";

        var profileJson = await Http.GetStringAsync(profileUrl, ct).ConfigureAwait(false);
        var profileNode = JsonNode.Parse(profileJson)
                          ?? throw new InvalidOperationException($"Пустой профиль {name}.");

        // id профиля, например "fabric-loader-0.15.11-1.20.1"
        var profileId = profileNode["id"]?.GetValue<string>()
                        ?? GetVersionFolderName(variant);

        // inheritsFrom должен указывать на vanilla id
        profileNode["inheritsFrom"] = gameVersion.Id;

        var folder = Sanitize(GetVersionFolderName(variant));
        var installDir = Path.Combine(_versionsRoot, folder);
        Directory.CreateDirectory(installDir);

        var profilePath = Path.Combine(installDir, $"{folder}.json");
        await File.WriteAllTextAsync(profilePath, profileNode.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }), ct).ConfigureAwait(false);

        // Библиотеки из профиля
        var jobs = new List<DownloadJob>();
        if (profileNode["libraries"] is JsonArray libs)
        {
            foreach (var lib in libs)
            {
                if (lib is null) continue;
                CollectLibraryJobs(lib, jobs, name);
            }
        }

        await DownloadJobsAsync(jobs, progress, name, ct, progressOffset: 0.58, progressScale: 0.40)
            .ConfigureAwait(false);

        await WriteInstalledMarkerAsync(installDir, variant, profileId, ct).ConfigureAwait(false);
        Report(progress, 1, 0, 0, 0, "Готово", null);
    }

    // ───────────────────────────── Forge / NeoForge ─────────────────────────────

    private async Task InstallForgeFamilyAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        bool neo,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var name = neo ? "NeoForge" : "Forge";
        await EnsureVanillaBaseAsync(gameVersion, progress, ct).ConfigureAwait(false);

        var fullVer = ResolveForgeFullVersion(gameVersion.Id, variant);
        Report(progress, 0.58, 0, 0, 0, $"{name} · installer", fullVer);

        string installerUrl;
        string installerFileName;
        if (neo)
        {
            // neoforge version is like 20.4.237 or 21.1.x (without mc prefix in artifact)
            var neoVer = variant.LoaderVersion ?? fullVer;
            installerUrl =
                $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{Uri.EscapeDataString(neoVer)}/neoforge-{neoVer}-installer.jar";
            installerFileName = $"neoforge-{neoVer}-installer.jar";
        }
        else
        {
            installerUrl =
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{Uri.EscapeDataString(fullVer)}/forge-{fullVer}-installer.jar";
            installerFileName = $"forge-{fullVer}-installer.jar";
        }

        var installerPath = Path.Combine(_tempRoot, installerFileName);
        await DownloadFileAsync(installerUrl, installerPath, ct).ConfigureAwait(false);

        Report(progress, 0.62, 0, 0, 0, $"{name} · разбор installer", installerFileName);

        var extractDir = Path.Combine(_tempRoot, Sanitize(fullVer) + "_extract");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        ZipFile.ExtractToDirectory(installerPath, extractDir);

        var installProfilePath = Path.Combine(extractDir, "install_profile.json");
        var versionJsonInJar = Path.Combine(extractDir, "version.json");

        if (!File.Exists(installProfilePath))
            throw new InvalidOperationException($"{name}: в installer нет install_profile.json.");

        await using var profileFs = File.OpenRead(installProfilePath);
        var installProfile = await JsonSerializer.DeserializeAsync<ForgeInstallProfile>(profileFs, JsonOpts, ct)
                                 .ConfigureAwait(false)
                             ?? throw new InvalidOperationException($"{name}: пустой install_profile.json.");

        // version.json может быть отдельным файлом или вложен в profile
        JsonNode versionNode;
        if (File.Exists(versionJsonInJar))
        {
            versionNode = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonInJar, ct).ConfigureAwait(false))
                          ?? throw new InvalidOperationException($"{name}: пустой version.json.");
        }
        else if (installProfile.VersionJsonPath is not null)
        {
            var p = Path.Combine(extractDir, installProfile.VersionJsonPath.TrimStart('/', '\\'));
            versionNode = JsonNode.Parse(await File.ReadAllTextAsync(p, ct).ConfigureAwait(false))
                          ?? throw new InvalidOperationException($"{name}: не найден version json.");
        }
        else
        {
            throw new InvalidOperationException($"{name}: не найден version.json в installer.");
        }

        versionNode["inheritsFrom"] ??= gameVersion.Id;

        var folder = Sanitize(GetVersionFolderName(variant));
        var installDir = Path.Combine(_versionsRoot, folder);
        Directory.CreateDirectory(installDir);

        var outJson = Path.Combine(installDir, $"{folder}.json");
        await File.WriteAllTextAsync(outJson, versionNode.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }), ct).ConfigureAwait(false);

        // jar Forge/NeoForge без URL — лежат внутри installer (папка maven/)
        Report(progress, 0.63, 0, 0, 0, $"{name} · maven из installer", null);
        CopyInstallerMavenToLibraries(extractDir);

        var jobs = new List<DownloadJob>();

        // Библиотеки из install_profile
        if (installProfile.Libraries is not null)
        {
            foreach (var lib in installProfile.Libraries)
                CollectForgeLibraryJobs(lib, jobs, name);
        }

        // Библиотеки из version.json
        if (versionNode["libraries"] is JsonArray vLibs)
        {
            foreach (var lib in vLibs)
            {
                if (lib is not null)
                    CollectLibraryJobs(lib, jobs, name);
            }
        }

        // Скачиваем только то, чего ещё нет (forge.jar уже из maven/)
        await DownloadJobsAsync(jobs, progress, name, ct, progressOffset: 0.64, progressScale: 0.26)
            .ConfigureAwait(false);

        // Официальный installer: нужен launcher_profiles.json, иначе «run the launcher first»
        EnsureLauncherProfilesJson();

        Report(progress, 0.92, 0, 0, 0, $"{name} · processors", null);
        await TryRunForgeInstallerClientAsync(installerPath, gameVersion.Id, progress, ct)
            .ConfigureAwait(false);

        // Если installer положил версию в стандартное имя — скопируем json/jar при необходимости
        await TryCopyForgeOutputAsync(gameVersion.Id, fullVer, neo, installDir, folder, ct)
            .ConfigureAwait(false);

        // Повторно подтянуть maven (installer иногда кладёт артефакты только в processors)
        CopyInstallerMavenToLibraries(extractDir);

        // Без forge.jar запуск даёт ClassNotFoundException: FMLTweaker — не помечаем «установлено»
        EnsureForgeLibrariesPresent(versionNode, name);

        await WriteInstalledMarkerAsync(installDir, variant, folder, ct).ConfigureAwait(false);
        Report(progress, 1, 0, 0, 0, "Готово", null);
    }

    /// <summary>
    /// Forge installer отказывается ставить client без launcher_profiles.json в корне.
    /// </summary>
    private void EnsureLauncherProfilesJson()
    {
        var path = Path.Combine(_root, "launcher_profiles.json");
        if (File.Exists(path))
        {
            try
            {
                // Пустой/битый файл тоже ломает installer
                var text = File.ReadAllText(path);
                if (text.Contains("\"profiles\"", StringComparison.Ordinal))
                    return;
            }
            catch
            {
                // перезапишем
            }
        }

        var json = """
            {
              "profiles": {
                "ZLauncher": {
                  "name": "ZLauncher",
                  "type": "custom",
                  "created": "1970-01-01T00:00:00.000Z",
                  "lastUsed": "1970-01-01T00:00:00.000Z",
                  "icon": "Furnace",
                  "lastVersionId": "latest-release"
                }
              },
              "selectedProfile": "ZLauncher",
              "clientToken": "00000000-0000-0000-0000-000000000000",
              "launcherVersion": {
                "name": "ZLauncher",
                "format": 21,
                "profilesFormat": 2
              }
            }
            """;
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Копирует встроенные maven-артефакты из installer.jar в libraries/
    /// (forge-…jar без URL на maven.minecraftforge.net).
    /// </summary>
    private void CopyInstallerMavenToLibraries(string extractDir)
    {
        var mavenRoot = Path.Combine(extractDir, "maven");
        if (!Directory.Exists(mavenRoot))
            return;

        foreach (var file in Directory.EnumerateFiles(mavenRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(mavenRoot, file);
            var dest = Path.Combine(_librariesRoot, rel);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            try
            {
                if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(file).Length)
                    File.Copy(file, dest, overwrite: true);
            }
            catch
            {
                // locked — пропускаем
            }
        }
    }

    /// <summary>
    /// Проверяет, что ключевые jar (forge/neoforge) реально лежат в libraries.
    /// </summary>
    private void EnsureForgeLibrariesPresent(JsonNode versionNode, string name)
    {
        if (versionNode["libraries"] is not JsonArray libs)
            throw new InvalidOperationException($"{name}: в version.json нет libraries.");

        var missing = new List<string>();
        foreach (var lib in libs)
        {
            if (lib is null) continue;
            var libName = lib["name"]?.GetValue<string>() ?? "";
            // Только сам loader — остальные могли бы скачаться позже
            var isLoader =
                libName.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase) ||
                libName.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase) ||
                libName.StartsWith("net.neoforged:forge:", StringComparison.OrdinalIgnoreCase);
            if (!isLoader)
                continue;

            var path = ResolveLibraryDiskPath(lib);
            if (path is null || !File.Exists(path) || new FileInfo(path).Length < 10_000)
                missing.Add(libName + (path is null ? "" : $" → {path}"));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{name}: не удалось установить loader jar (FMLTweaker не найдётся при запуске):\n" +
                string.Join("\n", missing) +
                "\nПереустановите сборку. Если ошибка повторится — проверьте интернет / maven.minecraftforge.net.");
        }
    }

    private string? ResolveLibraryDiskPath(JsonNode lib)
    {
        var artifactPath = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(artifactPath))
            return Path.Combine(_librariesRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));

        var name = lib["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            return null;

        var parts = name.Split(':');
        if (parts.Length < 3)
            return null;

        var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? $"-{parts[3]}" : "";
        var file = $"{artifact}-{version}{classifier}.jar";
        return Path.Combine(_librariesRoot, group, artifact, version, file);
    }

    private async Task EnsureJavaForInstallAsync(
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        // Для installer'ов Forge достаточно Java 17+
        if (_javaRuntime.FindJavaExecutable(JavaRuntimeService.ComponentJava21) is not null ||
            _javaRuntime.FindJavaExecutable(JavaRuntimeService.ComponentJava17) is not null ||
            _javaRuntime.FindJavaExecutable() is not null)
            return;

        var mapped = new Progress<InstallProgress>(p =>
        {
            progress?.Report(new InstallProgress
            {
                Progress = p.Progress * 0.12,
                BytesDownloaded = p.BytesDownloaded,
                TotalBytes = p.TotalBytes,
                SpeedBytesPerSecond = p.SpeedBytesPerSecond,
                Stage = p.Stage,
                CurrentFile = p.CurrentFile
            });
        });

        await _javaRuntime
            .EnsureJavaAsync(JavaRuntimeService.ComponentJava21, mapped, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Запуск официального Forge/NeoForge installer (--installClient).
    /// Важно: stdout/stderr ОБЯЗАТЕЛЬНО читать — иначе pipe заполняется и process зависает на 92%.
    /// </summary>
    private async Task TryRunForgeInstallerClientAsync(
        string installerPath,
        string gameVersionId,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        string java;
        try
        {
            // Installer-tools Forge: Java 17 ок для 1.12–1.20.4; 1.20.5+/NeoForge → 21.
            // Игра может требовать Java 8, но сам .jar installer на 17 работает.
            // javaw + RedirectStandard* без чтения = pipe deadlock на 92% → только java.exe.
            var gameComp = JavaRuntimeService.SelectComponentForGame(gameVersionId, mainClass: null);
            var component = gameComp == JavaRuntimeService.ComponentJava21
                ? JavaRuntimeService.ComponentJava21
                : JavaRuntimeService.ComponentJava17;

            java = PreferConsoleJava(
                await _javaRuntime.EnsureJavaAsync(component, null, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Report(progress, 0.93, 0, 0, 0, "Java: " + ex.Message, null);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = java,
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Без GUI / без зависания на AWT
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // headless + IPv4 (как в официальных логах forge)
            psi.ArgumentList.Add("-Djava.awt.headless=true");
            psi.ArgumentList.Add("-Djava.net.preferIPv4Stack=true");
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(installerPath);
            psi.ArgumentList.Add("--installClient");
            psi.ArgumentList.Add(_root);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Report(progress, 0.93, 0, 0, 0, "Installer: не удалось запустить", null);
                return;
            }

            // Таймаут: processors на старых CPU могут идти 5–10 мин
            const int timeoutMs = 12 * 60 * 1000;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            var lastStage = "processors";

            async Task DrainAsync(StreamReader reader, StringBuilder sink)
            {
                try
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                        if (line is null)
                            break;

                        lock (sink)
                            sink.AppendLine(line);

                        // Полный stdout installer'а в Debug-консоль
                        try
                        {
                            DebugLogService.Instance.Log("forge-installer", line);
                        }
                        catch
                        {
                            // ignore
                        }

                        // Прогресс по ключевым строкам installer'а
                        var stage = ClassifyForgeInstallerLine(line);
                        if (stage is not null)
                        {
                            lastStage = stage;
                            Report(progress, 0.94, 0, 0, 0, $"Forge · {stage}", Truncate(line, 80));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // timeout / cancel
                }
            }

            var stdoutTask = DrainAsync(proc.StandardOutput, stdoutBuf);
            var stderrTask = DrainAsync(proc.StandardError, stderrBuf);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Таймаут — убиваем зависший installer
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                Report(progress, 0.95, 0, 0, 0,
                    "Installer: таймаут (12 мин). Продолжаем с уже скачанными библиотеками…",
                    null);
                await Task.WhenAny(stdoutTask, Task.Delay(500)).ConfigureAwait(false);
                return;
            }

            // Дочитываем остатки логов
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            var exit = proc.ExitCode;
            if (exit == 0)
            {
                Report(progress, 0.97, 0, 0, 0, "Installer OK", lastStage);
            }
            else
            {
                var tail = GetLogTail(stdoutBuf, stderrBuf, 6);
                Report(progress, 0.95, 0, 0, 0,
                    $"Installer код {exit}" + (string.IsNullOrWhiteSpace(tail) ? "" : $": {tail}"),
                    null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(progress, 0.95, 0, 0, 0, "Installer: " + ex.Message, null);
        }
    }

    private static string? ClassifyForgeInstallerLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        if (line.Contains("Considering library", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Downloading libraries", StringComparison.OrdinalIgnoreCase))
            return "библиотеки";
        if (line.Contains("processor", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("MainClass:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Running processor", StringComparison.OrdinalIgnoreCase))
            return "processors";
        if (line.Contains("Extracting", StringComparison.OrdinalIgnoreCase))
            return "распаковка";
        if (line.Contains("Patching", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("binarypatcher", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("SpecialSource", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("jarsplitter", StringComparison.OrdinalIgnoreCase))
            return "патчи";
        if (line.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("The server installed successfully", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("true", StringComparison.Ordinal) && line.Contains("Client", StringComparison.OrdinalIgnoreCase))
            return "готово";
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception", StringComparison.Ordinal))
            return "ошибка";
        return null;
    }

    private static string Truncate(string s, int max)
    {
        s = s.Trim();
        if (s.Length <= max)
            return s;
        return s[..(max - 1)] + "…";
    }

    private static string GetLogTail(StringBuilder stdout, StringBuilder stderr, int lines)
    {
        string text;
        lock (stderr)
        lock (stdout)
            text = stderr.ToString() + "\n" + stdout.ToString();

        var arr = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        if (arr.Length == 0)
            return "";
        return string.Join(" | ", arr.TakeLast(lines).Select(l => Truncate(l, 100)));
    }

    private async Task TryCopyForgeOutputAsync(
        string gameId,
        string fullVer,
        bool neo,
        string installDir,
        string folder,
        CancellationToken ct)
    {
        // Типичные id после installer
        var candidates = neo
            ? new[]
            {
                $"neoforge-{fullVer}",
                fullVer,
                $"{gameId}-neoforge-{fullVer}"
            }
            : new[]
            {
                $"{gameId}-forge-{fullVer.Split('-').LastOrDefault()}",
                $"{gameId}-forge-{fullVer}",
                fullVer,
                $"forge-{fullVer}"
            };

        foreach (var id in candidates.Distinct())
        {
            var srcDir = Path.Combine(_versionsRoot, id);
            if (!Directory.Exists(srcDir))
                continue;

            foreach (var file in Directory.GetFiles(srcDir))
            {
                var name = Path.GetFileName(file);
                var destName = name.StartsWith(id, StringComparison.OrdinalIgnoreCase)
                    ? folder + Path.GetExtension(name)
                    : name;
                var dest = Path.Combine(installDir, destName);
                if (!File.Exists(dest))
                    File.Copy(file, dest, overwrite: false);
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }
    }

    // ───────────────────────────── OptiFine ─────────────────────────────

    private async Task InstallOptiFineAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        await EnsureVanillaBaseAsync(gameVersion, progress, ct).ConfigureAwait(false);

        var label = variant.LoaderVersion ?? variant.DisplayName.Replace("OptiFine ", "", StringComparison.Ordinal);
        // label like "HD_U_G8" or "HD U G8"
        var parts = label.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
        string type;
        string patch;
        if (parts.Length >= 2)
        {
            // HD_U_G8 → type=HD_U patch=G8  OR HD U G8
            if (parts.Length >= 3 && parts[0] == "HD" && parts[1] == "U")
            {
                type = "HD_U";
                patch = parts[2];
            }
            else if (parts.Length >= 2)
            {
                type = string.Join("_", parts.Take(parts.Length - 1));
                patch = parts[^1];
            }
            else
            {
                type = "HD_U";
                patch = parts[0];
            }
        }
        else
        {
            type = "HD_U";
            patch = label;
        }

        Report(progress, 0.60, 0, 0, 0, "OptiFine · загрузка", $"{type} {patch}");

        var jarName = $"OptiFine_{gameVersion.Id}_{type}_{patch}.jar";
        var libDir = Path.Combine(
            _librariesRoot,
            "optifine",
            "OptiFine",
            $"{gameVersion.Id}_{type}_{patch}");
        var libPath = Path.Combine(libDir, jarName);
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(_tempRoot);

        // Скачиваем installer (diff) во временный файл — в libraries кладём уже результат Patcher
        var installerPath = Path.Combine(_tempRoot, $"of_installer_{gameVersion.Id}_{type}_{patch}_{Guid.NewGuid():N}.jar");
        var usedUrl = await DownloadOptiFineJarAsync(
                gameVersion.Id, type, patch, jarName, installerPath, ct)
            .ConfigureAwait(false);

        var len = new FileInfo(installerPath).Length;
        if (len < 100_000)
            throw new InvalidOperationException(
                $"OptiFine скачался повреждённым ({len} байт). Попробуй ещё раз.");

        Report(progress, 0.78, 0, 0, 0, "OptiFine · Patcher", $"{type} {patch}");

        var vanillaJar = Path.Combine(
            _versionsRoot, Sanitize(gameVersion.Id), $"{Sanitize(gameVersion.Id)}.jar");
        if (!File.Exists(vanillaJar))
            throw new InvalidOperationException($"Нет vanilla jar: {vanillaJar}");

        // Официальный установщик: Patcher(vanilla, installer) → libraries/.../OptiFine-*.jar
        // 1.17+: notch/… ; 1.8–1.12: Config.class + net/optifine/…
        Exception? lastPatchError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (File.Exists(libPath))
                {
                    try { File.Delete(libPath); } catch { /* ignore */ }
                }

                await RunOptiFinePatcherAsync(vanillaJar, installerPath, libPath, gameVersion.Id, ct)
                    .ConfigureAwait(false);

                if (IsOptiFineModLibrary(libPath))
                {
                    lastPatchError = null;
                    break;
                }

                lastPatchError = new InvalidOperationException(
                    "OptiFine Patcher завершился, но library-jar невалиден " +
                    $"(нет net/optifine / Config). Попытка {attempt}/2.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastPatchError = ex;
            }

            // 1-я неудача: переустановить vanilla (целостность) и повторить Patcher
            if (attempt == 1)
            {
                Report(progress, 0.70, 0, 0, 0,
                    "OptiFine · vanilla повреждена? Переустанавливаем…", gameVersion.Id);
                await EnsureVanillaBaseAsync(gameVersion, progress, ct, forceRedownload: true)
                    .ConfigureAwait(false);
                vanillaJar = Path.Combine(
                    _versionsRoot, Sanitize(gameVersion.Id), $"{Sanitize(gameVersion.Id)}.jar");
                Report(progress, 0.78, 0, 0, 0, "OptiFine · Patcher (повтор)", $"{type} {patch}");
            }
        }

        if (lastPatchError is not null || !IsOptiFineModLibrary(libPath))
        {
            var detail = lastPatchError?.Message ?? "неизвестная ошибка";
            throw new InvalidOperationException(
                "OptiFine Patcher не смог собрать library для " + gameVersion.Id + ". " +
                "Vanilla переустановлена, но сборка всё ещё не удалась. " +
                "Попробуй другую сборку OptiFine или проверь интернет.\n" + detail);
        }

        // launchwrapper-of достаём из installer (до удаления)
        var lwOf = ExtractLaunchwrapperOf(installerPath)
                   ?? ExtractLaunchwrapperOf(libPath);

        try { File.Delete(installerPath); } catch { /* ignore */ }

        Report(progress, 0.90, 0, 0, 0, "OptiFine · профиль", null);

        var folder = Sanitize(GetVersionFolderName(variant));
        var installDir = Path.Combine(_versionsRoot, folder);
        Directory.CreateDirectory(installDir);

        var libraries = new JsonArray
        {
            new JsonObject
            {
                ["name"] = $"optifine:OptiFine:{gameVersion.Id}_{type}_{patch}",
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] =
                            $"optifine/OptiFine/{gameVersion.Id}_{type}_{patch}/{jarName}",
                        ["url"] = usedUrl,
                        ["size"] = new FileInfo(libPath).Length
                    }
                }
            }
        };

        if (lwOf is not null)
        {
            libraries.Add(new JsonObject
            {
                ["name"] = $"optifine:launchwrapper-of:{lwOf.Version}",
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] = lwOf.RelativePath.Replace('\\', '/'),
                        ["size"] = new FileInfo(lwOf.FullPath).Length
                    }
                }
            });
        }
        else
        {
            // Старые OptiFine: обычный launchwrapper 1.12
            libraries.Add(new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" });
            var lwPath = Path.Combine(_librariesRoot, "net", "minecraft", "launchwrapper", "1.12",
                "launchwrapper-1.12.jar");
            if (!File.Exists(lwPath))
            {
                try
                {
                    await DownloadFileAsync(
                            "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
                            lwPath, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // profile still saved
                }
            }
        }

        // inheritsFrom + tweakClass — как у официального installer
        var profile = new JsonObject
        {
            ["id"] = folder,
            ["inheritsFrom"] = gameVersion.Id,
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["releaseTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray
                {
                    "--tweakClass",
                    "optifine.OptiFineTweaker"
                }
            },
            ["libraries"] = libraries
        };

        var profilePath = Path.Combine(installDir, $"{folder}.json");
        await File.WriteAllTextAsync(profilePath, profile.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }), ct).ConfigureAwait(false);

        // Копия library рядом с версией (удобно для repair)
        var localJar = Path.Combine(installDir, jarName);
        File.Copy(libPath, localJar, overwrite: true);

        // Client jar = копия vanilla (официальный installer). Патчи — через ClassTransformer + library.
        Report(progress, 0.95, 0, 0, 0, "OptiFine · client jar", null);
        var clientJar = Path.Combine(installDir, $"{folder}.jar");
        File.Copy(vanillaJar, clientJar, overwrite: true);

        await WriteInstalledMarkerAsync(installDir, variant, folder, ct).ConfigureAwait(false);
        Report(progress, 1, 0, 0, 0, "Готово", null);
    }

    /// <summary>
    /// Готовит OptiFine к запуску (как официальный installer):
    /// 1) library = выход optifine.Patcher (notch/net/optifine + патчи классов);
    /// 2) client jar = копия vanilla (ClassTransformer патчит в runtime).
    /// </summary>
    public async Task BuildOptiFineClientJarAsync(
        string vanillaJarPath,
        string optiFineJarPath,
        string outputClientJarPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(vanillaJarPath))
            throw new InvalidOperationException($"Нет vanilla jar: {vanillaJarPath}");
        if (!File.Exists(optiFineJarPath))
            throw new InvalidOperationException($"Нет OptiFine jar: {optiFineJarPath}");

        // Если library ещё installer (patch/*.xdelta) — прогоняем Patcher in-place.
        if (IsOptiFineInstallerJar(optiFineJarPath) && !IsOptiFineModLibrary(optiFineJarPath))
        {
            var mcId = GuessMcIdFromPath(vanillaJarPath);
            var tempMod = Path.Combine(_tempRoot, $"of_mod_{Guid.NewGuid():N}.jar");
            Directory.CreateDirectory(_tempRoot);
            try
            {
                await RunOptiFinePatcherAsync(vanillaJarPath, optiFineJarPath, tempMod, mcId, ct)
                    .ConfigureAwait(false);
                if (!IsOptiFineModLibrary(tempMod))
                    throw new InvalidOperationException(
                        "OptiFine Patcher не создал библиотеку с net/optifine.");

                File.Copy(tempMod, optiFineJarPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempMod)) File.Delete(tempMod); } catch { /* ignore */ }
            }
        }

        // Client = vanilla (MD5 для ClassTransformer должны совпасть с notch-патчами)
        var dir = Path.GetDirectoryName(outputClientJarPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(outputClientJarPath) ||
            new FileInfo(outputClientJarPath).Length != new FileInfo(vanillaJarPath).Length)
        {
            File.Copy(vanillaJarPath, outputClientJarPath, overwrite: true);
        }
    }

    /// <summary>Запуск optifine.Patcher: base + diff → mod library.</summary>
    private async Task RunOptiFinePatcherAsync(
        string vanillaJarPath,
        string installerJarPath,
        string outputModJarPath,
        string gameVersionId,
        CancellationToken ct)
    {
        // Patcher на Java 8 для старых OF; для 1.17+ — runtime версии игры (17/21)
        var component = JavaRuntimeService.SelectComponentForGame(gameVersionId, mainClass: null);
        // Для самого Patcher (не игры) launchwrapper не важен; но Java 8 всё ещё ок для OF≤1.16
        var java = await _javaRuntime.EnsureJavaAsync(component, cancellationToken: ct)
            .ConfigureAwait(false);
        java = PreferConsoleJava(java);

        var outDir = Path.GetDirectoryName(outputModJarPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        // Patcher не любит писать поверх существующего
        var tempOut = outputModJarPath + $".building_{Guid.NewGuid():N}.jar";
        try
        {
            if (File.Exists(tempOut))
                File.Delete(tempOut);

            var psi = new ProcessStartInfo
            {
                FileName = java,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // -cp installer; main optifine.Patcher base diff out
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(installerJarPath);
            psi.ArgumentList.Add("optifine.Patcher");
            psi.ArgumentList.Add(vanillaJarPath);
            psi.ArgumentList.Add(installerJarPath);
            psi.ArgumentList.Add(tempOut);

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("Не удалось запустить OptiFine Patcher.");

            // Таймаут + drain stdout/stderr (иначе pipe deadlock, как у Forge на 92%)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(8));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new InvalidOperationException(
                    "OptiFine Patcher завис (таймаут 8 мин). Попробуй ещё раз.");
            }

            string stdout, stderr;
            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                stdout = "";
                stderr = "";
            }

            if (proc.ExitCode != 0 || !File.Exists(tempOut) || new FileInfo(tempOut).Length < 1000)
            {
                throw new InvalidOperationException(
                    "OptiFine Patcher не смог собрать library-jar.\n" +
                    stdout + "\n" + stderr);
            }

            if (File.Exists(outputModJarPath))
                File.Delete(outputModJarPath);
            File.Move(tempOut, outputModJarPath);
        }
        finally
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { /* ignore */ }
        }
    }

    private static string PreferConsoleJava(string javaPath)
    {
        // javaw плохо подходит для CLI Patcher; берём java.exe рядом
        if (javaPath.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var console = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
            if (File.Exists(console))
                return console;
        }

        return javaPath;
    }

    private static string GuessMcIdFromPath(string vanillaJarPath)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(vanillaJarPath);
        }
        catch
        {
            return "1.21";
        }
    }

    /// <summary>
    /// Library после Patcher.
    /// Новые OF: notch/net/optifine/Config.class или srg/...
    /// Старые (1.8–1.12): Config.class в корне + package net/optifine/.
    /// </summary>
    private static bool IsOptiFineModLibrary(string jarPath)
    {
        try
        {
            if (!File.Exists(jarPath) || new FileInfo(jarPath).Length < 50_000)
                return false;

            using var zip = ZipFile.OpenRead(jarPath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasXdelta = false;
            var hasOptiPackage = false;

            foreach (var e in zip.Entries)
            {
                var n = e.FullName.Replace('\\', '/');
                names.Add(n);

                if (n.StartsWith("patch/", StringComparison.OrdinalIgnoreCase) &&
                    n.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
                    hasXdelta = true;

                if (n.StartsWith("notch/net/optifine/", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("srg/net/optifine/", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("net/optifine/", StringComparison.OrdinalIgnoreCase))
                    hasOptiPackage = true;
            }

            // Сырой installer (ещё не Patcher) — не library
            if (hasXdelta)
                return false;

            // 1.17+ / modern
            if (names.Contains("notch/net/optifine/Config.class") ||
                names.Contains("srg/net/optifine/Config.class") ||
                names.Contains("net/optifine/Config.class"))
                return true;

            // 1.8–1.16 style: root Config.class + net/optifine/*
            if (names.Contains("Config.class") && hasOptiPackage)
                return true;

            // Запасной: есть package optifine и jar достаточно большой (после Patcher)
            if (hasOptiPackage && new FileInfo(jarPath).Length > 200_000)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Сырой installer с xdelta-патчами (ещё не Patcher).</summary>
    private static bool IsOptiFineInstallerJar(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            return zip.Entries.Any(e =>
                e.FullName.Replace('\\', '/')
                    .StartsWith("patch/", StringComparison.OrdinalIgnoreCase) &&
                e.Name.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public sealed record LaunchwrapperOfInfo(string Version, string FullPath, string RelativePath);

    /// <summary>
    /// Достаёт launchwrapper-of-X.Y.jar из OptiFine jar в libraries/optifine/launchwrapper-of/...
    /// </summary>
    public LaunchwrapperOfInfo? ExtractLaunchwrapperOf(string optiFineJarPath)
    {
        if (!File.Exists(optiFineJarPath))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(optiFineJarPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.StartsWith("launchwrapper-of-", StringComparison.OrdinalIgnoreCase) &&
                e.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
                return null;

            // launchwrapper-of-2.2.jar → 2.2
            var ver = entry.Name
                .Replace("launchwrapper-of-", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".jar", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (string.IsNullOrWhiteSpace(ver))
                ver = "2.2";

            var rel = Path.Combine("optifine", "launchwrapper-of", ver, $"launchwrapper-of-{ver}.jar");
            var dest = Path.Combine(_librariesRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (!File.Exists(dest) || new FileInfo(dest).Length != entry.Length)
            {
                entry.ExtractToFile(dest, overwrite: true);
            }

            return new LaunchwrapperOfInfo(ver, dest, rel);
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── shared helpers ─────────────────────────────

    private List<DownloadJob> BuildVanillaJobs(
        string gameId,
        string installDir,
        VersionJson meta,
        out string? assetIndexUrl,
        out string? assetIndexId)
    {
        assetIndexUrl = meta.AssetIndex?.Url;
        assetIndexId = meta.AssetIndex?.Id;
        var jobs = new List<DownloadJob>();

        if (meta.Downloads?.Client is { } client && !string.IsNullOrEmpty(client.Url))
        {
            var clientPath = Path.Combine(installDir, $"{Sanitize(gameId)}.jar");
            jobs.Add(new DownloadJob(client.Url, clientPath, client.Size, "Клиент"));
        }

        if (meta.Libraries is null)
            return jobs;

        foreach (var lib in meta.Libraries)
        {
            if (!LibraryAllowedOnWindows(lib.Rules))
                continue;

            if (lib.Downloads?.Artifact is { } art && !string.IsNullOrEmpty(art.Url))
            {
                var path = Path.Combine(_librariesRoot, art.Path.Replace('/', Path.DirectorySeparatorChar));
                jobs.Add(new DownloadJob(art.Url, path, art.Size, "Библиотеки"));
            }

            if (lib.Downloads?.Classifiers is not null &&
                lib.Downloads.Classifiers.TryGetValue("natives-windows", out var natives) &&
                !string.IsNullOrEmpty(natives.Url))
            {
                var path = Path.Combine(_librariesRoot, natives.Path.Replace('/', Path.DirectorySeparatorChar));
                jobs.Add(new DownloadJob(natives.Url, path, natives.Size, "Natives"));
            }
        }

        return jobs;
    }

    private List<DownloadJob> BuildAssetJobs(string indexPath)
    {
        var jobs = new List<DownloadJob>();
        using var fs = File.OpenRead(indexPath);
        var index = JsonSerializer.Deserialize<AssetIndexJson>(fs, JsonOpts);
        if (index?.Objects is null)
            return jobs;

        var objectsDir = Path.Combine(_assetsRoot, "objects");
        foreach (var (_, obj) in index.Objects)
        {
            if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2)
                continue;

            var prefix = obj.Hash[..2];
            var objPath = Path.Combine(objectsDir, prefix, obj.Hash);
            var url = $"https://resources.download.minecraft.net/{prefix}/{obj.Hash}";
            jobs.Add(new DownloadJob(url, objPath, obj.Size, "Ресурсы"));
        }

        return jobs;
    }

    private void CollectLibraryJobs(JsonNode lib, List<DownloadJob> jobs, string stage)
    {
        // Формат Mojang / Fabric: downloads.artifact
        var artifact = lib["downloads"]?["artifact"];
        if (artifact is not null)
        {
            var url = artifact["url"]?.GetValue<string>();
            var pathRel = artifact["path"]?.GetValue<string>();
            var size = artifact["size"]?.GetValue<long>() ?? 0;
            if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(pathRel))
            {
                var path = Path.Combine(_librariesRoot, pathRel.Replace('/', Path.DirectorySeparatorChar));
                jobs.Add(new DownloadJob(url, path, size, stage));
                return;
            }
        }

        // Формат Maven name: group:artifact:version
        var name = lib["name"]?.GetValue<string>();
        var urlBase = lib["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/";
        if (string.IsNullOrEmpty(name))
            return;

        var parts = name.Split(':');
        if (parts.Length < 3)
            return;

        var group = parts[0].Replace('.', '/');
        var artifactId = parts[1];
        var version = parts[2];
        var fileName = $"{artifactId}-{version}.jar";
        var rel = $"{group}/{artifactId}/{version}/{fileName}";
        var fullUrl = urlBase.TrimEnd('/') + "/" + rel;
        var local = Path.Combine(_librariesRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        jobs.Add(new DownloadJob(fullUrl, local, 0, stage));
    }

    private void CollectForgeLibraryJobs(ForgeLibrary lib, List<DownloadJob> jobs, string stage)
    {
        if (lib.Downloads?.Artifact is { } art && !string.IsNullOrEmpty(art.Url))
        {
            var path = Path.Combine(_librariesRoot, art.Path.Replace('/', Path.DirectorySeparatorChar));
            jobs.Add(new DownloadJob(art.Url, path, art.Size, stage));
            return;
        }

        if (string.IsNullOrEmpty(lib.Name))
            return;

        // maven name without downloads block
        var parts = lib.Name.Split(':');
        if (parts.Length < 3)
            return;

        var group = parts[0].Replace('.', '/');
        var artifactId = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? $"-{parts[3]}" : "";
        var fileName = $"{artifactId}-{version}{classifier}.jar";
        var rel = $"{group}/{artifactId}/{version}/{fileName}";
        var url = (lib.Url ?? "https://libraries.minecraft.net/").TrimEnd('/') + "/" + rel;
        var local = Path.Combine(_librariesRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        jobs.Add(new DownloadJob(url, local, 0, stage));
    }

    private async Task DownloadJobsAsync(
        List<DownloadJob> jobs,
        IProgress<InstallProgress>? progress,
        string stagePrefix,
        CancellationToken ct,
        double progressOffset = 0,
        double progressScale = 1)
    {
        var pending = new List<DownloadJob>();
        long already = 0;
        long totalKnown = 0;

        foreach (var job in jobs)
        {
            totalKnown += Math.Max(job.Size, 0);
            if (File.Exists(job.Path))
            {
                var len = new FileInfo(job.Path).Length;
                if (job.Size <= 0 || len == job.Size)
                {
                    already += job.Size > 0 ? job.Size : len;
                    continue;
                }
            }

            pending.Add(job);
        }

        if (pending.Count == 0)
        {
            Report(progress, progressOffset + progressScale, already, Math.Max(totalKnown, 1), 0,
                stagePrefix, null);
            return;
        }

        if (totalKnown <= 0)
            totalKnown = Math.Max(pending.Sum(j => Math.Max(j.Size, 1)), 1);

        long sessionDownloaded = 0;
        var sw = Stopwatch.StartNew();
        var speedLock = new object();
        long speedWindowBytes = 0;
        var speedWindowStart = sw.Elapsed;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var requiredFailures = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var gate = new SemaphoreSlim(6);
        var tasks = pending.Select(async job =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DownloadFileAsync(job.Url, job.Path, ct).ConfigureAwait(false);

                var size = job.Size > 0
                    ? job.Size
                    : (File.Exists(job.Path) ? new FileInfo(job.Path).Length : 0);

                var session = Interlocked.Add(ref sessionDownloaded, size);
                var totalDone = already + session;

                double speed;
                lock (speedLock)
                {
                    speedWindowBytes += size;
                    var elapsed = (sw.Elapsed - speedWindowStart).TotalSeconds;
                    if (elapsed >= 0.35)
                    {
                        speed = speedWindowBytes / elapsed;
                        speedWindowBytes = 0;
                        speedWindowStart = sw.Elapsed;
                    }
                    else
                    {
                        speed = sw.Elapsed.TotalSeconds > 0 ? session / sw.Elapsed.TotalSeconds : 0;
                    }
                }

                var localP = Math.Clamp((double)totalDone / totalKnown, 0, 1);
                var globalP = progressOffset + localP * progressScale;
                Report(progress, Math.Min(globalP, progressOffset + progressScale * 0.99),
                    totalDone, totalKnown, speed, $"{stagePrefix} · {job.Stage}", Path.GetFileName(job.Path));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = $"{Path.GetFileName(job.Path)}: {ex.Message}";
                failures.Add(msg);
                DebugLogService.Instance.Log("download-fail", $"{job.Stage} · {msg} · {job.Url}");
                // Клиент обязателен. Ресурсы/часть libs — допускаем частичные сбои (см. порог ниже).
                if (job.Stage is "Клиент")
                    requiredFailures.Add(msg);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (!requiredFailures.IsEmpty)
        {
            var sample = string.Join("; ", requiredFailures.Take(3));
            throw new InvalidOperationException(
                $"Не удалось скачать обязательные файлы ({requiredFailures.Count}): {sample}");
        }

        // Если слишком много ресурсов упало — тоже ошибка
        if (failures.Count > 0 && failures.Count > pending.Count * 0.25)
        {
            throw new InvalidOperationException(
                $"Слишком много ошибок загрузки ({failures.Count}/{pending.Count}). {failures.First()}");
        }

        var finalDone = already + Interlocked.Read(ref sessionDownloaded);
        Report(progress, progressOffset + progressScale, finalDone, Math.Max(totalKnown, finalDone), 0,
            stagePrefix, null);
    }

    private static async Task<string> DownloadFileFromAnyAsync(
        IEnumerable<string> urls,
        string path,
        CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadFileAsync(url, path, ct).ConfigureAwait(false);
                if (File.Exists(path) && new FileInfo(path).Length > 50_000)
                    return url;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Не удалось скачать файл ни с одного зеркала: {path}. {last?.Message}");
    }

    /// <summary>
    /// OptiFine: BMCLAPI (если доступен) → официальный adloadx+downloadx с токеном.
    /// </summary>
    private static async Task<string> DownloadOptiFineJarAsync(
        string mcVersion,
        string type,
        string patch,
        string jarName,
        string path,
        CancellationToken ct)
    {
        // 1) BMCLAPI (быстро, но часто 403 при rate-limit)
        var mirrors = new[]
        {
            $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}",
            $"https://bmclapi2.bangbang93.com/maven/com/optifine/{Uri.EscapeDataString(mcVersion)}/{jarName}",
            $"https://bmclapi.bangbang93.com/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}"
        };

        foreach (var url in mirrors)
        {
            try
            {
                await DownloadFileAsync(url, path, ct).ConfigureAwait(false);
                if (File.Exists(path) && new FileInfo(path).Length > 100_000)
                    return url;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // next mirror
            }
        }

        // 2) Официальный сайт: adloadx → downloadx?f=...&x=token
        var adUrl = $"https://optifine.net/adloadx?f={Uri.EscapeDataString(jarName)}";
        string html;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, adUrl);
            req.Headers.TryAddWithoutValidation("Referer", "https://optifine.net/");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось открыть страницу OptiFine: {ex.Message}", ex);
        }

        var match = Regex.Match(
            html,
            @"downloadx\?f=(?<f>[^""'\s&]+)&x=(?<x>[a-fA-F0-9]+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            throw new InvalidOperationException(
                "Не удалось найти ссылку скачивания OptiFine на optifine.net.");

        var dlUrl =
            $"https://optifine.net/downloadx?f={match.Groups["f"].Value}&x={match.Groups["x"].Value}";

        try
        {
            // Referer важен для optifine.net
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = path + ".part";
            using (var req = new HttpRequestMessage(HttpMethod.Get, dlUrl))
            {
                req.Headers.TryAddWithoutValidation("Referer", adUrl);
                using var resp = await Http.SendAsync(
                        req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);

            if (!File.Exists(path) || new FileInfo(path).Length < 100_000)
                throw new InvalidOperationException("Файл OptiFine слишком маленький после скачивания.");

            return dlUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Скачивание OptiFine с optifine.net не удалось: {ex.Message}", ex);
        }
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var temp = path + $".part{attempt}";
            try
            {
                using var response = await Http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} для {url}");
                }

                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                           .ConfigureAwait(false)) > 0)
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                await output.FlushAsync(ct).ConfigureAwait(false);
                await output.DisposeAsync().ConfigureAwait(false);

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { /* ignore */ }
                }

                if (attempt < 3)
                    await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Скачивание не удалось: {url}. {last?.Message}", last);
    }

    private static async Task WriteInstalledMarkerAsync(
        string installDir,
        VersionVariant variant,
        string versionId,
        CancellationToken ct)
    {
        Directory.CreateDirectory(installDir);
        var marker = Path.Combine(installDir, ".installed");
        var meta = new
        {
            variant.Key,
            variant.GameVersionId,
            Kind = variant.Kind.ToString(),
            variant.DisplayName,
            variant.LoaderVersion,
            VersionId = versionId,
            InstalledAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(meta, JsonOpts), ct)
            .ConfigureAwait(false);
    }

    private static string GetVersionFolderName(VersionVariant variant) => variant.Kind switch
    {
        LoaderKind.Vanilla => variant.GameVersionId,
        LoaderKind.Fabric => $"fabric-loader-{variant.LoaderVersion}-{variant.GameVersionId}",
        LoaderKind.Quilt => $"quilt-loader-{variant.LoaderVersion}-{variant.GameVersionId}",
        LoaderKind.Forge => $"{variant.GameVersionId}-forge-{variant.LoaderVersion}",
        LoaderKind.NeoForge => $"neoforge-{variant.LoaderVersion}",
        LoaderKind.OptiFine => $"{variant.GameVersionId}-OptiFine-{Sanitize(variant.LoaderVersion ?? "OF")}",
        LoaderKind.Custom => Sanitize(variant.LoaderVersion ?? variant.GameVersionId),
        _ => Sanitize(variant.Key)
    };

    private static string ResolveForgeFullVersion(string gameId, VersionVariant variant)
    {
        // Key: forge:1.20.1-47.2.0  or LoaderVersion is 47.2.0
        if (variant.Key.StartsWith("forge:", StringComparison.OrdinalIgnoreCase))
            return variant.Key["forge:".Length..];

        if (variant.Key.StartsWith("neoforge:", StringComparison.OrdinalIgnoreCase))
            return variant.Key["neoforge:".Length..];

        if (!string.IsNullOrEmpty(variant.LoaderVersion) &&
            variant.LoaderVersion.Contains(gameId, StringComparison.Ordinal))
            return variant.LoaderVersion;

        return $"{gameId}-{variant.LoaderVersion}";
    }

    private static bool LibraryAllowedOnWindows(List<RuleJson>? rules)
    {
        if (rules is null || rules.Count == 0)
            return true;

        bool? allowed = null;
        foreach (var rule in rules)
        {
            var match = true;
            if (rule.Os is not null)
                match = string.Equals(rule.Os.Name, "windows", StringComparison.OrdinalIgnoreCase);

            if (match)
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed ?? true;
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        double p,
        long done,
        long total,
        double speed,
        string stage,
        string? file)
    {
        var clamped = Math.Clamp(p, 0, 1);
        progress?.Report(new InstallProgress
        {
            Progress = clamped,
            BytesDownloaded = done,
            TotalBytes = total,
            SpeedBytesPerSecond = speed,
            Stage = stage,
            CurrentFile = file
        });

        // Debug-консоль: все стадии/файлы установки
        try
        {
            var pct = $"{clamped * 100:0.0}%";
            if (!string.IsNullOrEmpty(file))
            {
                if (total > 0)
                    DebugLogService.Instance.Log(
                        $"{stage} · {file} · {pct} · {done}/{total}" +
                        (speed > 0 ? $" · {speed / 1024:0} KB/s" : ""));
                else
                    DebugLogService.Instance.Log($"{stage} · {file} · {pct}");
            }
            else if (total > 0)
            {
                DebugLogService.Instance.Log(
                    $"{stage} · {pct} · {done}/{total}" +
                    (speed > 0 ? $" · {speed / 1024:0} KB/s" : ""));
            }
            else
            {
                DebugLogService.Instance.Log($"{stage} · {pct}");
            }
        }
        catch
        {
            // лог не должен ломать установку
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", value.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(45) };
        // Браузерный UA — некоторые зеркала (BMCLAPI) режут «голые» клиенты
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZLauncher/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    private sealed record DownloadJob(string Url, string Path, long Size, string Stage);

    // ── JSON DTOs ──

    private sealed class VersionJson
    {
        [JsonPropertyName("downloads")]
        public DownloadsJson? Downloads { get; set; }

        [JsonPropertyName("libraries")]
        public List<MojangLibrary>? Libraries { get; set; }

        [JsonPropertyName("assetIndex")]
        public AssetIndexRef? AssetIndex { get; set; }
    }

    private sealed class DownloadsJson
    {
        [JsonPropertyName("client")]
        public FileDownload? Client { get; set; }
    }

    private sealed class FileDownload
    {
        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    private sealed class MojangLibrary
    {
        [JsonPropertyName("downloads")]
        public LibraryDownloads? Downloads { get; set; }

        [JsonPropertyName("rules")]
        public List<RuleJson>? Rules { get; set; }
    }

    private sealed class LibraryDownloads
    {
        [JsonPropertyName("artifact")]
        public LibraryArtifact? Artifact { get; set; }

        [JsonPropertyName("classifiers")]
        public Dictionary<string, LibraryArtifact>? Classifiers { get; set; }
    }

    private sealed class LibraryArtifact
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    private sealed class RuleJson
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "allow";

        [JsonPropertyName("os")]
        public OsRule? Os { get; set; }
    }

    private sealed class OsRule
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class AssetIndexRef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    private sealed class AssetIndexJson
    {
        [JsonPropertyName("objects")]
        public Dictionary<string, AssetObject>? Objects { get; set; }
    }

    private sealed class AssetObject
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    private sealed class ForgeInstallProfile
    {
        [JsonPropertyName("libraries")]
        public List<ForgeLibrary>? Libraries { get; set; }

        [JsonPropertyName("json")]
        public string? VersionJsonPath { get; set; }
    }

    private sealed class ForgeLibrary
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("downloads")]
        public LibraryDownloads? Downloads { get; set; }
    }
}
