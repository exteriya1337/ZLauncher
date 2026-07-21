using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ZLauncher.Models;

namespace ZLauncher.Services;

/// <summary>
/// Проверки перед запуском: Java, совместимость модов, автоустановка зависимостей.
/// </summary>
public sealed class PreLaunchService
{
    private readonly JavaRuntimeService _java;
    private readonly ModrinthService _modrinth;

    public PreLaunchService(JavaRuntimeService java, ModrinthService modrinth)
    {
        _java = java;
        _modrinth = modrinth;
    }

    public static string GetModsDirectory() => ModrinthService.GetModsDirectory();

    public static string GetDisabledModsDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher", "game", "mods-disabled");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<PreLaunchReport> PrepareAsync(
        string gameVersionId,
        LoaderKind loaderKind,
        string? mainClass,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var quarantined = new List<string>();
        var restored = new List<string>();
        var installedDeps = new List<string>();

        // ── 1. Java ──────────────────────────────────────────────────────
        Report(progress, 0.02, "Проверка Java…");
        var component = JavaRuntimeService.SelectComponentForGame(gameVersionId, mainClass);
        var javaLabel = ComponentLabel(component);

        Report(progress, 0.05, $"Java · {javaLabel}");
        var javaPath = await _java
            .EnsureJavaAsync(component, progress is null
                ? null
                : new Progress<InstallProgress>(p =>
                {
                    progress.Report(new InstallProgress
                    {
                        Progress = 0.05 + p.Progress * 0.25,
                        Stage = string.IsNullOrEmpty(p.Stage) ? $"Java · {javaLabel}" : p.Stage,
                        CurrentFile = p.CurrentFile,
                        BytesDownloaded = p.BytesDownloaded,
                        TotalBytes = p.TotalBytes,
                        SpeedBytesPerSecond = p.SpeedBytesPerSecond
                    });
                }), ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            throw new InvalidOperationException(
                $"Не удалось подготовить {javaLabel} для Minecraft {gameVersionId}.");

        var verified = await VerifyJavaAsync(javaPath, component, ct).ConfigureAwait(false);
        if (!verified.Ok)
            throw new InvalidOperationException(
                $"Установленная Java не подходит ({javaLabel}): {verified.Detail}");

        Report(progress, 0.32, $"Java OK · {javaLabel}");

        // ── 2. Моды: вернуть quarantine + проверить совместимость ────────
        var loader = ToModrinthLoader(loaderKind);
        var modsDir = GetModsDirectory();
        var disabledDir = GetDisabledModsDirectory();

        Report(progress, 0.35, "Моды · восстановление…");
        // Только совместимые с ТЕКУЩЕЙ MC — иначе 1.16-jar снова попадает в 1.12
        restored.AddRange(RestoreDisabledMods(disabledDir, modsDir, gameVersionId));

        Report(progress, 0.4, "Моды · проверка совместимости…");
        var jars = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        // Vanilla / OptiFine — модлоадера нет: все моды временно убираем
        if (loaderKind is LoaderKind.Vanilla or LoaderKind.OptiFine)
        {
            foreach (var jar in jars)
            {
                var name = Path.GetFileName(jar);
                if (MoveToDisabled(jar, disabledDir, out var movedName))
                    quarantined.Add($"{movedName} (нужен модлоадер, выбрано {loaderKind})");
            }

            return new PreLaunchReport
            {
                JavaPath = javaPath,
                JavaComponent = component,
                JavaLabel = javaLabel,
                QuarantinedMods = quarantined,
                RestoredMods = restored,
                InstalledDependencies = installedDeps,
                Warnings = warnings
            };
        }

        // SHA1 → Modrinth version
        var hashByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jar in jars)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                hashByPath[jar] = ComputeSha1Hex(jar);
            }
            catch
            {
                warnings.Add($"Не удалось прочитать {Path.GetFileName(jar)}");
            }
        }

        Report(progress, 0.5, "Моды · Modrinth…");
        var versionsByHash = await _modrinth
            .GetVersionsByHashesAsync(hashByPath.Values, ct)
            .ConfigureAwait(false);

        var activeVersions = new List<(string Path, ModrinthVersionInfo? Version, LocalModMeta? Local)>();

        foreach (var jar in jars)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(jar);
            versionsByHash.TryGetValue(
                hashByPath.TryGetValue(jar, out var h) ? h : "",
                out var mrVersion);

            LocalModMeta? local = null;
            try { local = ReadLocalModMeta(jar); }
            catch { /* ignore */ }

            var compatible = IsCompatible(
                gameVersionId, loader, mrVersion, local, out var reason, fileName);
            if (!compatible)
            {
                if (MoveToDisabled(jar, disabledDir, out var moved))
                    quarantined.Add($"{moved} ({reason})");
                continue;
            }

            activeVersions.Add((jar, mrVersion, local));
        }

        Report(progress, 0.62,
            quarantined.Count > 0
                ? $"Моды · отключено {quarantined.Count}"
                : "Моды · совместимость OK");

        // ── 3. Зависимости (required) ────────────────────────────────────
        Report(progress, 0.65, "Моды · зависимости…");
        var installedProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, ver, _) in activeVersions)
        {
            if (!string.IsNullOrWhiteSpace(ver?.ProjectId))
                installedProjectIds.Add(ver!.ProjectId);
        }

        // Индекс ZLauncher: projectId → file
        foreach (var kv in ReadModIndex())
        {
            if (File.Exists(Path.Combine(modsDir, kv.Value)))
                installedProjectIds.Add(kv.Key);
        }

        var pendingDeps = new Queue<string>();
        foreach (var (_, ver, local) in activeVersions)
        {
            if (ver is not null)
            {
                foreach (var dep in ver.Dependencies)
                {
                    if (!string.Equals(dep.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrWhiteSpace(dep.ProjectId))
                        continue;
                    if (!installedProjectIds.Contains(dep.ProjectId))
                        pendingDeps.Enqueue(dep.ProjectId!);
                }
            }

            // fabric.mod.json depends: fabric-api и т.п. (slug-like keys)
            if (local?.RequiredModIds is { Count: > 0 } reqs)
            {
                foreach (var id in reqs)
                {
                    // minecraft / java / fabricloader — не моды
                    if (IsBuiltInDependency(id))
                        continue;
                    // Если в папке уже есть jar с таким id в fabric.mod.json — ок
                    if (activeVersions.Any(a =>
                            string.Equals(a.Local?.ModId, id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    pendingDeps.Enqueue(id);
                }
            }
        }

        var depth = 0;
        var seenDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pendingDeps.Count > 0 && depth < 40)
        {
            ct.ThrowIfCancellationRequested();
            var depId = pendingDeps.Dequeue();
            if (string.IsNullOrWhiteSpace(depId) || !seenDeps.Add(depId))
                continue;
            if (installedProjectIds.Contains(depId))
                continue;

            depth++;
            Report(progress, 0.65 + Math.Min(0.3, depth * 0.02),
                $"Зависимость · {depId}");

            try
            {
                var result = await _modrinth
                    .InstallModAsync(depId, gameVersionId, loader, null, ct)
                    .ConfigureAwait(false);

                installedDeps.Add($"{depId} → {result.FileName}");
                installedProjectIds.Add(depId);

                // Рекурсивно: зависимости только что установленного
                var installedHash = ComputeSha1Hex(result.FilePath);
                var verMap = await _modrinth
                    .GetVersionsByHashesAsync(new[] { installedHash }, ct)
                    .ConfigureAwait(false);
                if (verMap.TryGetValue(installedHash, out var depVer))
                {
                    if (!string.IsNullOrWhiteSpace(depVer.ProjectId))
                        installedProjectIds.Add(depVer.ProjectId);

                    foreach (var d in depVer.Dependencies)
                    {
                        if (!string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrWhiteSpace(d.ProjectId) &&
                            !installedProjectIds.Contains(d.ProjectId))
                            pendingDeps.Enqueue(d.ProjectId!);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Не удалось установить зависимость «{depId}»: {ex.Message}");
            }
        }

        Report(progress, 1, "Проверки завершены");

        return new PreLaunchReport
        {
            JavaPath = javaPath,
            JavaComponent = component,
            JavaLabel = javaLabel,
            QuarantinedMods = quarantined,
            RestoredMods = restored,
            InstalledDependencies = installedDeps,
            Warnings = warnings
        };
    }

    // ───────────────────────── helpers ─────────────────────────

    private static void Report(IProgress<InstallProgress>? progress, double p, string stage) =>
        progress?.Report(new InstallProgress { Progress = p, Stage = stage });

    public static string? ToModrinthLoader(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null
    };

    private static string ComponentLabel(string component) => component switch
    {
        JavaRuntimeService.ComponentJava8 => "Java 8",
        JavaRuntimeService.ComponentJava17 => "Java 17",
        JavaRuntimeService.ComponentJava21 => "Java 21",
        _ => component
    };

    private static async Task<(bool Ok, string Detail)> VerifyJavaAsync(
        string javaPath,
        string component,
        CancellationToken ct)
    {
        try
        {
            // javaw не печатает version в stdout — берём java.exe рядом
            var probe = javaPath;
            if (OperatingSystem.IsWindows() &&
                Path.GetFileName(javaPath).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            {
                var console = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
                if (File.Exists(console))
                    probe = console;
            }

            var psi = new ProcessStartInfo
            {
                FileName = probe,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "не удалось запустить java -version");

            var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var text = (err + "\n" + stdout).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return (true, "ok"); // javaw без вывода — считаем ок, файл есть

            // version "1.8.0_51"  /  version "17.0.15"  /  version "21.0.2"
            var m = Regex.Match(text, @"version\s+""(?<v>[\d._]+)""", RegexOptions.IgnoreCase);
            if (!m.Success)
                return (true, text.Split('\n').FirstOrDefault()?.Trim() ?? "ok");

            var ver = m.Groups["v"].Value;
            var major = ParseJavaMajor(ver);
            var expected = component switch
            {
                JavaRuntimeService.ComponentJava8 => 8,
                JavaRuntimeService.ComponentJava17 => 17,
                JavaRuntimeService.ComponentJava21 => 21,
                _ => 0
            };

            if (expected > 0 && major > 0 && major != expected)
                return (false, $"нужна {expected}, найдена {major} ({ver})");

            return (true, ver);
        }
        catch (Exception ex)
        {
            // Не блокируем запуск из‑за сбоя -version, если exe существует
            return (true, ex.Message);
        }
    }

    private static int ParseJavaMajor(string ver)
    {
        // 1.8.0_xxx → 8; 17.0.x → 17
        var parts = ver.Split('.', '_');
        if (parts.Length >= 2 && parts[0] == "1" && int.TryParse(parts[1], out var legacy))
            return legacy;
        if (int.TryParse(parts[0], out var modern))
            return modern;
        return 0;
    }

    private static List<string> RestoreDisabledMods(
        string disabledDir,
        string modsDir,
        string gameVersionId)
    {
        var restored = new List<string>();
        if (!Directory.Exists(disabledDir))
            return restored;

        Directory.CreateDirectory(modsDir);
        foreach (var file in Directory.GetFiles(disabledDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);

            // Не возвращаем jar, явно собранный под другую MC
            var hints = ExtractMcVersionsFromText(name);
            if (hints.Count > 0 &&
                !hints.Any(h => VersionHintMatches(h, gameVersionId)))
            {
                continue;
            }

            var dest = Path.Combine(modsDir, name);
            try
            {
                if (File.Exists(dest))
                {
                    File.Delete(file);
                    continue;
                }

                File.Move(file, dest);
                restored.Add(name);
            }
            catch
            {
                // ignore locked
            }
        }

        return restored;
    }

    private static bool MoveToDisabled(string jarPath, string disabledDir, out string movedName)
    {
        movedName = Path.GetFileName(jarPath);
        try
        {
            Directory.CreateDirectory(disabledDir);
            var dest = Path.Combine(disabledDir, movedName);
            if (File.Exists(dest))
            {
                var baseName = Path.GetFileNameWithoutExtension(movedName);
                var ext = Path.GetExtension(movedName);
                dest = Path.Combine(disabledDir, $"{baseName}_{DateTime.UtcNow:HHmmss}{ext}");
                movedName = Path.GetFileName(dest);
            }

            File.Move(jarPath, dest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCompatible(
        string gameVersionId,
        string? loader,
        ModrinthVersionInfo? mr,
        LocalModMeta? local,
        out string reason,
        string? fileName = null)
    {
        reason = "несовместим";

        // 0) Имя файла / version_number важнее API (у Modrinth бывают кривые game_versions)
        var nameHints = ExtractMcVersionsFromText(fileName);
        if (mr is not null)
        {
            foreach (var h in ExtractMcVersionsFromText(mr.VersionNumber))
                if (!nameHints.Contains(h, StringComparer.OrdinalIgnoreCase))
                    nameHints.Add(h);
            foreach (var h in ExtractMcVersionsFromText(mr.Name))
                if (!nameHints.Contains(h, StringComparer.OrdinalIgnoreCase))
                    nameHints.Add(h);
        }

        if (nameHints.Count > 0 &&
            !nameHints.Any(h => VersionHintMatches(h, gameVersionId)))
        {
            reason = $"файл MC {string.Join("/", nameHints.Take(3))}≠{gameVersionId}";
            return false;
        }

        if (mr is not null)
        {
            // API game_versions — только если имя файла не указало совместимость явно
            if (mr.GameVersions.Count > 0 && nameHints.Count == 0)
            {
                var gvOk = mr.GameVersions.Any(v =>
                    string.Equals(v, gameVersionId, StringComparison.OrdinalIgnoreCase) ||
                    VersionHintMatches(v, gameVersionId));
                if (!gvOk)
                {
                    reason = $"MC {string.Join("/", mr.GameVersions.Take(3))}≠{gameVersionId}";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(loader) && mr.Loaders.Count > 0)
            {
                var loaders = mr.Loaders.Select(l => l.ToLowerInvariant()).ToHashSet();
                var ok = loaders.Contains(loader!) ||
                         (loader == "quilt" && loaders.Contains("fabric")) ||
                         loaders.Contains("minecraft");
                if (!ok)
                {
                    reason = $"loader {string.Join("/", mr.Loaders)}≠{loader}";
                    return false;
                }
            }

            return true;
        }

        if (local is not null)
        {
            if (local.MinecraftVersions is { Count: > 0 } mcv)
            {
                var ok = mcv.Any(v => VersionHintMatches(v, gameVersionId));
                if (!ok)
                {
                    reason = $"MC {string.Join("/", mcv.Take(2))}≠{gameVersionId}";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(loader) && local.Loaders is { Count: > 0 } ll)
            {
                var set = ll.Select(x => x.ToLowerInvariant()).ToHashSet();
                var ok = set.Contains(loader!) ||
                         (loader == "quilt" && set.Contains("fabric"));
                if (!ok)
                {
                    reason = $"loader {string.Join("/", ll)}≠{loader}";
                    return false;
                }
            }

            return true;
        }

        reason = "unknown";
        return true;
    }

    /// <summary>
    /// Достаёт версии MC из имени файла / version_number:
    /// forge-1.16.5, mc1.12.2, fabric-0.14+1.20.1 и т.п.
    /// </summary>
    private static List<string> ExtractMcVersionsFromText(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        // Сильные маркеры: mc / forge / fabric / quilt / neoforge + версия
        foreach (Match m in Regex.Matches(
                     text,
                     @"(?:^|[^a-z0-9])(?:mc|forge|fabric|quilt|neoforge)[-_]?([1]\.\d{1,2}(?:\.\d{1,2})?)",
                     RegexOptions.IgnoreCase))
        {
            var v = m.Groups[1].Value;
            if (!result.Contains(v, StringComparer.OrdinalIgnoreCase))
                result.Add(v);
        }

        if (result.Count > 0)
            return result;

        // Полные 1.x.y (без ложного 26.4.2 у Xaero)
        foreach (Match m in Regex.Matches(text, @"\b(1\.\d{1,2}\.\d{1,2})\b"))
        {
            var v = m.Groups[1].Value;
            if (!result.Contains(v, StringComparer.OrdinalIgnoreCase))
                result.Add(v);
        }

        return result;
    }

    private static bool VersionHintMatches(string hint, string gameVersionId)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return true;
        hint = hint.Trim();
        if (string.Equals(hint, gameVersionId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(hint, "*", StringComparison.OrdinalIgnoreCase))
            return true;
        // ~1.20.1 / >=1.20 / 1.20.x — грубое совпадение по major.minor
        if (hint.Contains(gameVersionId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (TryParseMc(gameVersionId, out var maj, out var min, out _) &&
            TryParseMc(hint.TrimStart('>', '<', '=', '~', '^'), out var hMaj, out var hMin, out _))
        {
            return maj == hMaj && min == hMin;
        }

        return false;
    }

    private static bool TryParseMc(string id, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        var m = Regex.Match(id, @"^(?<a>\d+)\.(?<b>\d+)(?:\.(?<c>\d+))?");
        if (!m.Success) return false;
        major = int.Parse(m.Groups["a"].Value);
        minor = int.Parse(m.Groups["b"].Value);
        if (m.Groups["c"].Success)
            patch = int.Parse(m.Groups["c"].Value);
        return true;
    }

    private static bool IsBuiltInDependency(string id)
    {
        id = id.ToLowerInvariant();
        return id is "minecraft" or "java" or "fabricloader" or "quilt_loader"
            or "forge" or "neoforge" or "fabric-loader" or "quilt-loader";
    }

    private static string ComputeSha1Hex(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA1.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, string> ReadModIndex()
    {
        try
        {
            var path = Path.Combine(GetModsDirectory(), ".zlauncher-mods.json");
            if (!File.Exists(path))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return dict is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class LocalModMeta
    {
        public string? ModId { get; init; }
        public List<string>? MinecraftVersions { get; init; }
        public List<string>? Loaders { get; init; }
        public List<string>? RequiredModIds { get; init; }
    }

    private static LocalModMeta? ReadLocalModMeta(string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);

        // Fabric / Quilt
        var fabric = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
        if (fabric is not null)
        {
            using var s = fabric.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var loaders = new List<string> { fabric.FullName.StartsWith("quilt") ? "quilt" : "fabric" };
            var mc = new List<string>();
            var req = new List<string>();

            if (root.TryGetProperty("depends", out var depends) &&
                depends.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in depends.EnumerateObject())
                {
                    if (p.NameEquals("minecraft"))
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                            mc.Add(p.Value.GetString() ?? "");
                        else if (p.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var x in p.Value.EnumerateArray())
                                if (x.ValueKind == JsonValueKind.String)
                                    mc.Add(x.GetString() ?? "");
                        }
                    }
                    else if (!IsBuiltInDependency(p.Name))
                    {
                        req.Add(p.Name);
                    }
                }
            }

            return new LocalModMeta
            {
                ModId = id,
                MinecraftVersions = mc.Count > 0 ? mc : null,
                Loaders = loaders,
                RequiredModIds = req.Count > 0 ? req : null
            };
        }

        // Forge / NeoForge mods.toml
        var modsToml = zip.GetEntry("META-INF/mods.toml") ?? zip.GetEntry("META-INF/neoforge.mods.toml");
        if (modsToml is not null)
        {
            using var s = modsToml.Open();
            using var reader = new StreamReader(s);
            var text = reader.ReadToEnd();
            var loaders = new List<string>();
            if (text.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
                loaders.Add("neoforge");
            if (text.Contains("forge", StringComparison.OrdinalIgnoreCase))
                loaders.Add("forge");
            if (loaders.Count == 0)
                loaders.Add("forge");

            var mc = new List<string>();
            // versionRange="[1.20.1,1.21)" near minecraft
            foreach (Match m in Regex.Matches(text,
                         @"modId\s*=\s*""minecraft""[\s\S]*?versionRange\s*=\s*""([^""]+)""",
                         RegexOptions.IgnoreCase))
            {
                mc.Add(m.Groups[1].Value);
            }

            string? modId = null;
            var idMatch = Regex.Match(text, @"\[\[mods\]\][\s\S]*?modId\s*=\s*""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (idMatch.Success)
                modId = idMatch.Groups[1].Value;

            return new LocalModMeta
            {
                ModId = modId,
                MinecraftVersions = mc.Count > 0 ? mc : null,
                Loaders = loaders
            };
        }

        // Legacy mcmod.info
        var mcmod = zip.GetEntry("mcmod.info");
        if (mcmod is not null)
        {
            using var s = mcmod.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var el = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;
            var mc = new List<string>();
            if (el.TryGetProperty("mcversion", out var mcv))
                mc.Add(mcv.GetString() ?? "");
            var id = el.TryGetProperty("modid", out var mid) ? mid.GetString() : null;
            return new LocalModMeta
            {
                ModId = id,
                MinecraftVersions = mc.Count > 0 ? mc : null,
                Loaders = new List<string> { "forge" }
            };
        }

        return null;
    }
}
