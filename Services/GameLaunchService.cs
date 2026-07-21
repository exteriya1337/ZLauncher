using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ZLauncher.Helpers;
using ZLauncher.Models;

namespace ZLauncher.Services;

/// <summary>
/// Запуск установленной версии Minecraft и отслеживание процесса.
/// </summary>
public sealed class GameLaunchService
{
    private readonly GameInstallService _installs;
    private readonly JavaRuntimeService _java;
    private readonly SkinService _skins;
    private Process? _gameProcess;

    public GameLaunchService(
        GameInstallService installs,
        JavaRuntimeService java,
        SkinService skins)
    {
        _installs = installs;
        _java = java;
        _skins = skins;
    }

    public bool IsRunning =>
        _gameProcess is { HasExited: false };

    public event EventHandler? ProcessExited;

    public async Task<Process> LaunchAsync(
        MinecraftVersion gameVersion,
        VersionVariant variant,
        string nickname,
        int memoryMinMb,
        int memoryMaxMb,
        string? jvmArguments,
        int windowWidth,
        int windowHeight,
        bool fullscreen,
        CancellationToken cancellationToken = default,
        bool offlineAccount = true,
        string? offlineUuidCompact = null)
    {
        if (IsRunning)
            throw new InvalidOperationException("Игра уже запущена.");

        if (!_installs.IsInstalled(variant))
            throw new InvalidOperationException("Версия не установлена.");

        var versionDir = _installs.GetInstallDirectory(variant);
        var root = _installs.GameRoot;

        var versionJsonPath = Directory.GetFiles(versionDir, "*.json")
            .FirstOrDefault(f => !Path.GetFileName(f).Equals(".installed", StringComparison.OrdinalIgnoreCase));

        if (versionJsonPath is null || !File.Exists(versionJsonPath))
            throw new InvalidOperationException("Не найден profile JSON версии.");

        var profileText = await File.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false);
        var profile = JsonNode.Parse(profileText)?.AsObject()
                      ?? throw new InvalidOperationException("Повреждён profile JSON.");

        // Старые установки OptiFine: launchwrapper-of + патченый client jar
        await RepairOptiFineProfileAsync(profile, versionDir, gameVersion.Id, cancellationToken)
            .ConfigureAwait(false);

        // merges inheritsFrom chain
        profile = await MergeInheritsAsync(profile, cancellationToken).ConfigureAwait(false);

        var mainClass = profile["mainClass"]?.GetValue<string>()
                        ?? "net.minecraft.client.main.Main";

        // LaunchWrapper (OptiFine 1.12–1.16) НЕ работает на Java 9+ → берём jre-legacy (Java 8)
        var javaComponent = JavaRuntimeService.SelectComponentForGame(gameVersion.Id, mainClass);
        var java = await _java
            .EnsureJavaAsync(javaComponent, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var nativesDir = Path.Combine(versionDir, "natives");
        Directory.CreateDirectory(nativesDir);
        await ExtractNativesAsync(profile, nativesDir, cancellationToken).ConfigureAwait(false);

        var classpath = BuildClasspath(profile, versionDir, gameVersion.Id);
        if (string.IsNullOrWhiteSpace(classpath) || !classpath.Contains(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Classpath пуст — установка версии повреждена.");

        // Клиентский jar: vanilla {id}.jar или патченый OptiFine {profileId}.jar (>5 MB)
        var hasClientJar = classpath.Split(Path.PathSeparator).Any(p =>
        {
            if (!File.Exists(p)) return false;
            var name = Path.GetFileName(p);
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase)) return false;
            try { return new FileInfo(p).Length > 5_000_000; }
            catch { return false; }
        });
        if (!hasClientJar)
        {
            throw new InvalidOperationException(
                $"Не найден клиентский jar Minecraft ({gameVersion.Id}.jar). " +
                "Переустановите версию (сначала Vanilla, затем OptiFine/модлоадер).");
        }

        var assetIndex = profile["assets"]?.GetValue<string>()
                         ?? profile["assetIndex"]?["id"]?.GetValue<string>()
                         ?? gameVersion.Id;

        // ── Offline Mode (автономный режим) ──────────────────────────────
        // Ник из UI + offline UUID (MD5 OfflinePlayer:name) + непустой fake token.
        // Пустой accessToken / userProperties / versionType=demo → Multiplayer серый в меню.
        var playerName = string.IsNullOrWhiteSpace(nickname) ? "Player" : nickname.Trim();
        var uuidDashed = BuildOfflineUuidDashed(playerName, offlineUuidCompact);
        // 1.16.x: token "0" = auth error → Multiplayer серый. Нужен ровно 32 hex-символа.
        var accessToken = Guid.NewGuid().ToString("N"); // 32 hex, без дефисов

        var gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDir);

        // Скин — только косметика, не блокирует запуск и не ходит в auth
        try
        {
            var skinInfo = await _skins
                .ResolveAndFetchAsync(playerName, cancellationToken, forceOffline: true)
                .ConfigureAwait(false);
            if (skinInfo.SkinFilePath is not null && File.Exists(skinInfo.SkinFilePath))
            {
                var skinsDir = Path.Combine(gameDir, "ZLauncherSkins");
                Directory.CreateDirectory(skinsDir);
                File.Copy(
                    skinInfo.SkinFilePath,
                    Path.Combine(skinsDir, $"{playerName}.png"),
                    overwrite: true);
            }
        }
        catch
        {
            // offline: скин необязателен
        }

        // Не дублируем -cp из profile: собираем JVM-аргументы вручную
        var maxMb = Math.Clamp(memoryMaxMb, memoryMinMb, 32768);
        var minMb = Math.Clamp(memoryMinMb, 256, maxMb);

        // Всегда оконный режим (не exclusive fullscreen — он «ломает» картинку при старте).
        // «Полный экран» в UI = окно на весь монитор (borderless-like), без смены режима ОС.
        var (screenW, screenH) = GetPrimaryScreenSize();
        int winW, winH;
        if (fullscreen)
        {
            winW = screenW;
            winH = screenH;
        }
        else
        {
            winW = Math.Clamp(windowWidth, 640, 7680);
            winH = Math.Clamp(windowHeight, 480, 4320);
        }

        // Без консоли: javaw.exe на Windows
        java = PreferWindowedJava(java);

        // options.txt: чистый профиль под эту MC (1.13+ ключи ломают 1.12)
        EnsureGameOptions(gameDir, winW, winH, borderlessWindow: fullscreen, gameVersion.Id);

        // JVM до mainClass.
        // authlib 2.1.x (MC 1.16.x): любой accessToken ходит в api.minecraftservices.com/privileges
        // и возвращает multiplayer=false → серая кнопка «Сетевая игра».
        // Нужны ВСЕ host-свойства (EnvironmentParser), иначе custom env игнорируется.
        // Недоступные hosts → AuthenticationUnavailableException → OfflineSocialInteractions
        // (serversAllowed=true).
        var args = new List<string>
        {
            $"-Xms{minMb}M",
            $"-Xmx{maxMb}M",
            $"-Djava.library.path={nativesDir}",
            $"-Dminecraft.launcher.brand=ZLauncher",
            $"-Dminecraft.launcher.version=1.0"
        };

        AppendOfflineAuthJvmFlags(args);

        if (!string.IsNullOrWhiteSpace(jvmArguments))
            args.AddRange(SplitArgs(jvmArguments));

        // Доп. JVM из profile, но без -cp / ${classpath}
        AppendJvmArgsFromProfile(profile, args, nativesDir, classpath, root);

        // Повторно зафиксировать offline-hosts (profile/user args не должны перебить)
        AppendOfflineAuthJvmFlags(args);

        args.Add("-cp");
        args.Add(classpath);
        args.Add(mainClass);

        // exclusiveFullscreen = false: никогда не передаём --fullscreen
        AppendGameArgs(
            profile,
            args,
            playerName,
            uuidDashed,
            accessToken,
            gameVersion.Id,
            gameDir,
            Path.Combine(root, "assets"),
            assetIndex,
            winW,
            winH,
            exclusiveFullscreen: false);

        // LaunchWrapper без --tweakClass грузит VanillaTweaker → ClassNotFound
        EnsureLaunchWrapperTweakClass(profile, args, mainClass);

        var logDir = Path.Combine(gameDir, "logs");
        Directory.CreateDirectory(logDir);
        try
        {
            await File.WriteAllTextAsync(
                    Path.Combine(logDir, "zlauncher-last-launch.txt"),
                    java + "\n" + string.Join("\n", args),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        // ВАЖНО: НЕ редиректить stdout/stderr и НЕ CreateNoWindow.
        // Иначе LWJGL 1.12 часто играет звук, а окна нет (MainWindowHandle=0).
        // Логи пишет сам Minecraft в game/logs/latest.log.
        var psi = new ProcessStartInfo
        {
            FileName = java,
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            RedirectStandardInput = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить Java.");

        // Даём LWJGL/Forge подняться; если сразу падает — latest.log
        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            var message = await ReadLaunchFailureMessageAsync(logDir, process.ExitCode, cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        _gameProcess = process;
        process.Exited += (_, _) =>
        {
            _gameProcess = null;
            ProcessExited?.Invoke(this, EventArgs.Empty);
        };

        // Звук без окна: ищем HWND (в т.ч. «невидимый») и тащим на primary monitor
        var focusW = winW;
        var focusH = winH;
        _ = Task.Run(async () =>
        {
            try
            {
                await GameWindowHelper
                    .BringGameWindowAsync(process, focusW, focusH)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        });

        return process;
    }

    private static async Task<string> ReadLaunchFailureMessageAsync(
        string logDir,
        int exitCode,
        CancellationToken cancellationToken)
    {
        string message = "";
        try
        {
            var latest = Path.Combine(logDir, "latest.log");
            if (File.Exists(latest))
            {
                var t = await File.ReadAllTextAsync(latest, cancellationToken).ConfigureAwait(false);
                if (t.Length > 3000)
                    t = t[^3000..];
                message = t;
            }
        }
        catch
        {
            // ignore
        }

        if (string.IsNullOrWhiteSpace(message))
            message = $"Java завершилась с кодом {exitCode} (см. game/logs/latest.log).";

        if (message.Contains("URLClassLoader", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ClassCastException", StringComparison.OrdinalIgnoreCase))
        {
            message =
                "Эта версия (LaunchWrapper/OptiFine) требует Java 8.\n\n" + message;
        }

        if (message.Contains("VanillaTweaker", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("tweak class", StringComparison.OrdinalIgnoreCase))
        {
            message =
                "Не передан --tweakClass (OptiFine/Forge). Переустанови сборку.\n\n" + message;
        }

        if (message.Contains("Missing Mods", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("MissingModsException", StringComparison.OrdinalIgnoreCase))
        {
            message =
                "Несовместимые или недостающие моды.\n\n" + message;
        }

        return message.Trim();
    }

    public void ClearIfExited()
    {
        if (_gameProcess is { HasExited: true })
            _gameProcess = null;
    }

    /// <summary>
    /// Чинит профили OptiFine: launchwrapper-of + патченый client jar.
    /// </summary>
    private async Task RepairOptiFineProfileAsync(
        JsonObject profile,
        string versionDir,
        string gameVersionId,
        CancellationToken ct)
    {
        if (profile["libraries"] is not JsonArray libs)
            return;

        // Ищем OptiFine jar
        string? optiJar = null;
        foreach (var lib in libs)
        {
            if (lib is null) continue;
            var name = lib["name"]?.GetValue<string>() ?? "";
            if (!name.StartsWith("optifine:OptiFine:", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(path))
            {
                var full = Path.Combine(_installs.GameRoot, "libraries",
                    path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    optiJar = full;
                    break;
                }
            }
        }

        if (optiJar is null)
        {
            optiJar = Directory.Exists(versionDir)
                ? Directory.GetFiles(versionDir, "OptiFine*.jar").FirstOrDefault()
                : null;
        }

        if (optiJar is null || !File.Exists(optiJar))
            return;

        var lwOf = _installs.ExtractLaunchwrapperOf(optiJar);
        if (lwOf is not null)
        {
            var hasOf = libs.Any(l =>
                (l?["name"]?.GetValue<string>() ?? "")
                .StartsWith("optifine:launchwrapper-of:", StringComparison.OrdinalIgnoreCase));

            if (!hasOf)
            {
                for (var i = libs.Count - 1; i >= 0; i--)
                {
                    var n = libs[i]?["name"]?.GetValue<string>() ?? "";
                    if (n.StartsWith("net.minecraft:launchwrapper:", StringComparison.OrdinalIgnoreCase))
                        libs.RemoveAt(i);
                }

                libs.Add(new JsonObject
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
        }

        // Старый legacy minecraftArguments → modern arguments.game + tweakClass
        if (profile["minecraftArguments"] is not null)
        {
            profile.Remove("minecraftArguments");
            if (profile["arguments"] is not JsonObject argsObj)
            {
                argsObj = new JsonObject();
                profile["arguments"] = argsObj;
            }

            if (argsObj["game"] is not JsonArray gameArr)
            {
                gameArr = new JsonArray();
                argsObj["game"] = gameArr;
            }

            var hasTweak = gameArr.Any(n =>
                n is JsonValue v &&
                v.TryGetValue<string>(out var s) &&
                s.Contains("OptiFineTweaker", StringComparison.OrdinalIgnoreCase));

            if (!hasTweak)
            {
                gameArr.Add("--tweakClass");
                gameArr.Add("optifine.OptiFineTweaker");
            }
        }

        // Патченый client jar versions/{id}/{id}.jar
        var profileId = profile["id"]?.GetValue<string>()
                        ?? Path.GetFileName(versionDir);
        var clientJar = Path.Combine(versionDir, $"{profileId}.jar");
        var vanillaJar = Path.Combine(_installs.VersionsRoot, gameVersionId, $"{gameVersionId}.jar");

        try
        {
            if (File.Exists(vanillaJar) && File.Exists(optiJar))
            {
                await _installs
                    .BuildOptiFineClientJarAsync(vanillaJar, optiJar, clientJar, ct)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Не блокируем запуск — попробуем как есть
        }
    }

    private async Task<JsonObject> MergeInheritsAsync(JsonObject profile, CancellationToken ct)
    {
        var inherits = profile["inheritsFrom"]?.GetValue<string>();
        if (string.IsNullOrEmpty(inherits))
            return profile;

        var parentPath = Path.Combine(_installs.VersionsRoot, inherits, $"{inherits}.json");
        if (!File.Exists(parentPath))
            return profile;

        var parentText = await File.ReadAllTextAsync(parentPath, ct).ConfigureAwait(false);
        var parent = JsonNode.Parse(parentText)?.AsObject();
        if (parent is null)
            return profile;

        parent = await MergeInheritsAsync(parent, ct).ConfigureAwait(false);

        // child overlays parent: libraries merge, mainClass from child, etc.
        var merged = parent.DeepClone()!.AsObject();

        foreach (var prop in profile)
        {
            if (prop.Key == "libraries" && prop.Value is JsonArray childLibs &&
                merged["libraries"] is JsonArray parentLibs)
            {
                var combined = new JsonArray();
                foreach (var l in parentLibs)
                    combined.Add(l?.DeepClone());
                foreach (var l in childLibs)
                    combined.Add(l?.DeepClone());
                merged["libraries"] = combined;
            }
            else if (prop.Key == "arguments" && prop.Value is JsonObject childArgs)
            {
                // OptiFine/Forge: дописываем game/jvm к родительским (не затираем)
                var parentArgs = merged["arguments"] as JsonObject ?? new JsonObject();
                merged["arguments"] = parentArgs;

                foreach (var argKey in new[] { "game", "jvm" })
                {
                    if (childArgs[argKey] is not JsonArray childArr)
                        continue;

                    var combined = new JsonArray();
                    if (parentArgs[argKey] is JsonArray parentArr)
                    {
                        foreach (var x in parentArr)
                            combined.Add(x?.DeepClone());
                    }

                    foreach (var x in childArr)
                        combined.Add(x?.DeepClone());

                    parentArgs[argKey] = combined;
                }

                foreach (var p in childArgs)
                {
                    if (p.Key is "game" or "jvm")
                        continue;
                    parentArgs[p.Key] = p.Value?.DeepClone();
                }
            }
            else if (prop.Key != "inheritsFrom")
            {
                merged[prop.Key] = prop.Value?.DeepClone();
            }
        }

        return merged;
    }

    private string BuildClasspath(JsonObject profile, string versionDir, string gameId)
    {
        var parts = new List<string>();
        var libRoot = Path.Combine(_installs.GameRoot, "libraries");

        if (profile["libraries"] is JsonArray libs)
        {
            foreach (var lib in libs)
            {
                if (lib is null) continue;
                // Mojang rules: LWJGL 3.2.1 / java-objc-bridge только macOS и т.п.
                if (!IsLibraryAllowed(lib))
                    continue;
                // Natives-only entries (artifact отсутствует) на classpath не нужны
                if (lib["downloads"]?["artifact"] is null && lib["natives"] is not null)
                    continue;

                var path = ResolveLibraryPath(lib, libRoot);
                if (path is not null && File.Exists(path) && !parts.Contains(path))
                    parts.Add(path);
            }
        }

        // Minecraft client jar (net.minecraft.client.main.Main).
        // Нельзя брать любой *.jar из папки версии: у OptiFine там лежит OptiFine_*.jar,
        // а клиент — в versions/{gameId}/{gameId}.jar (inheritsFrom).
        var clientJar = ResolveClientJar(profile, versionDir, gameId);
        if (clientJar is not null && !parts.Contains(clientJar))
            parts.Add(clientJar);

        return string.Join(Path.PathSeparator, parts);
    }

    /// <summary>
    /// Правила библиотек Mojang: при наличии rules по умолчанию disallow,
    /// затем по порядку применяются allow/disallow для совпавших OS.
    /// Без rules — allow.
    /// </summary>
    private static bool IsLibraryAllowed(JsonNode lib)
    {
        if (lib["rules"] is not JsonArray rules || rules.Count == 0)
            return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            if (!RuleAppliesToCurrentOs(rule))
                continue;

            var action = rule["action"]?.GetValue<string>();
            allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool RuleAppliesToCurrentOs(JsonNode rule)
    {
        var os = rule["os"];
        if (os is null)
            return true;

        var name = os["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            return true;

        // Mojang: windows / osx / linux
        if (OperatingSystem.IsWindows())
            return string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS())
            return string.Equals(name, "osx", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "macos", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux())
            return string.Equals(name, "linux", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private string? ResolveClientJar(JsonObject profile, string versionDir, string gameId)
    {
        var candidates = new List<string>();

        var profileId = profile["id"]?.GetValue<string>();
        var mainClass = profile["mainClass"]?.GetValue<string>() ?? "";
        // OptiFine + LaunchWrapper: ClassTransformer патчит VANILLA jar в runtime.
        // Пред-патченый {profileId}.jar даёт MD5 mismatch и ошибки в логе.
        var isOptiFineLw = mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase) &&
                           profile["libraries"] is JsonArray libs &&
                           libs.Any(l =>
                               (l?["name"]?.GetValue<string>() ?? "")
                               .Contains("optifine:OptiFine", StringComparison.OrdinalIgnoreCase));

        if (isOptiFineLw)
        {
            candidates.Add(Path.Combine(_installs.VersionsRoot, gameId, $"{gameId}.jar"));
            candidates.Add(Path.Combine(versionDir, $"{gameId}.jar"));
            var inheritsOf = profile["inheritsFrom"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(inheritsOf))
                candidates.Add(Path.Combine(_installs.VersionsRoot, inheritsOf, $"{inheritsOf}.jar"));
        }

        if (!string.IsNullOrWhiteSpace(profileId) && !isOptiFineLw)
            candidates.Add(Path.Combine(versionDir, $"{profileId}.jar"));

        // Vanilla client рядом с установкой / в папке базовой версии
        candidates.Add(Path.Combine(versionDir, $"{gameId}.jar"));
        candidates.Add(Path.Combine(_installs.VersionsRoot, gameId, $"{gameId}.jar"));

        // inheritsFrom (если merge не снял) и типичные id вида 1.16.5-forge-...
        var inherits = profile["inheritsFrom"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(inherits))
            candidates.Add(Path.Combine(_installs.VersionsRoot, inherits, $"{inherits}.jar"));

        // Из id профиля вытащить базовую версию: "1.16.5-OptiFine-..." → 1.16.5
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var dash = profileId.IndexOf('-');
            if (dash > 0)
            {
                var baseId = profileId[..dash];
                candidates.Add(Path.Combine(_installs.VersionsRoot, baseId, $"{baseId}.jar"));
            }
        }

        // Не брать пред-патченый OptiFine client jar, если это LW-запуск
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(c))
                continue;
            if (isOptiFineLw)
            {
                var fn = Path.GetFileName(c);
                // Патченый jar версии OptiFine обычно называется 1.16.5-OptiFine-....jar
                if (fn.Contains("OptiFine", StringComparison.OrdinalIgnoreCase) &&
                    !fn.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase) &&
                    !fn.Equals($"{gameId}.jar", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return c;
        }

        // Последний шанс: jar в versionDir, но не OptiFine/launchwrapper/моды
        if (Directory.Exists(versionDir))
        {
            foreach (var f in Directory.GetFiles(versionDir, "*.jar"))
            {
                var name = Path.GetFileName(f);
                if (name.Contains("OptiFine", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase)) continue;
                return f;
            }
        }

        return null;
    }

    private static string? ResolveLibraryPath(JsonNode lib, string libRoot)
    {
        var artifactPath = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(artifactPath))
            return Path.Combine(libRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));

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
        return Path.Combine(libRoot, group, artifact, version, file);
    }

    private async Task ExtractNativesAsync(JsonObject profile, string nativesDir, CancellationToken ct)
    {
        if (profile["libraries"] is not JsonArray libs)
            return;

        // Чистая папка natives — без stale DLL от прошлых запусков/версий
        try
        {
            if (Directory.Exists(nativesDir))
            {
                foreach (var f in Directory.EnumerateFiles(nativesDir))
                {
                    try { File.Delete(f); } catch { /* locked */ }
                }
            }
            else
            {
                Directory.CreateDirectory(nativesDir);
            }
        }
        catch
        {
            Directory.CreateDirectory(nativesDir);
        }

        var libRoot = Path.Combine(_installs.GameRoot, "libraries");

        foreach (var lib in libs)
        {
            if (lib is null) continue;
            if (!IsLibraryAllowed(lib))
                continue;

            var classifiers = lib["downloads"]?["classifiers"]?.AsObject();
            if (classifiers is null) continue;

            if (!classifiers.TryGetPropertyValue("natives-windows", out var natives) || natives is null)
                continue;

            var pathRel = natives["path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pathRel))
                continue;

            var jarPath = Path.Combine(libRoot, pathRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jarPath))
                continue;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;
                    if (entry.FullName.Contains("META-INF", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Служебные checksum-файлы из natives jar
                    if (entry.Name.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dest = Path.Combine(nativesDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            catch
            {
                // ignore bad natives jar
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void AppendJvmArgsFromProfile(
        JsonObject profile,
        List<string> args,
        string nativesDir,
        string classpath,
        string root)
    {
        // Modern: arguments.jvm — полностью без -cp / classpath (добавляем сами в конце)
        if (profile["arguments"]?["jvm"] is not JsonArray jvm)
            return;

        var skipNextClasspath = false;
        foreach (var node in jvm)
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
                continue;

            var replaced = ReplacePlaceholders(s, nativesDir, classpath, root);
            if (string.IsNullOrWhiteSpace(replaced))
                continue;

            if (skipNextClasspath)
            {
                skipNextClasspath = false;
                continue;
            }

            // Пропускаем -cp и следующий за ним classpath
            if (replaced is "-cp" or "-classpath")
            {
                skipNextClasspath = true;
                continue;
            }

            // Голый classpath (несколько jar через ; или :)
            if (replaced.Contains(Path.PathSeparator) &&
                replaced.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(replaced, classpath, StringComparison.Ordinal))
                continue;

            if (replaced.StartsWith("-Djava.library.path=", StringComparison.Ordinal))
                continue;
            if (replaced.StartsWith("-Dminecraft.launcher.brand=", StringComparison.Ordinal))
                continue;
            if (replaced.StartsWith("-Dminecraft.launcher.version=", StringComparison.Ordinal))
                continue;

            args.Add(replaced);
        }
    }

    /// <summary>java.exe → javaw.exe (без консольного окна на Windows).</summary>
    private static string PreferWindowedJava(string javaPath)
    {
        if (!OperatingSystem.IsWindows())
            return javaPath;

        var name = Path.GetFileName(javaPath);
        if (name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            return javaPath;

        if (name.Equals("java.exe", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(javaPath);
            if (dir is not null)
            {
                var javaw = Path.Combine(dir, "javaw.exe");
                if (File.Exists(javaw))
                    return javaw;
            }
        }

        return javaPath;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static (int Width, int Height) GetPrimaryScreenSize()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // SM_CXSCREEN=0, SM_CYSCREEN=1 — полный размер основного монитора
                var w = GetSystemMetrics(0);
                var h = GetSystemMetrics(1);
                if (w >= 640 && h >= 480)
                    return (w, h);
            }
        }
        catch
        {
            // ignore
        }

        return (1920, 1080);
    }

    /// <summary>
    /// options.txt под конкретную MC. Смешение 1.13+ ключей с 1.12.2
    /// даёт «игра запустилась, окна нет / чёрный экран / мгновенный выход».
    /// </summary>
    private static void EnsureGameOptions(
        string gameDir,
        int width,
        int height,
        bool borderlessWindow,
        string gameVersionId)
    {
        try
        {
            var path = Path.Combine(gameDir, "options.txt");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var legacyMc = IsLegacyMinecraft(gameVersionId); // ≤ 1.12.x

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line[..idx].Trim();
                    var val = line[(idx + 1)..].Trim();
                    if (key.Length == 0) continue;

                    // 1.13+ бинды (key.keyboard.w) и новые опции — выкидываем на legacy
                    if (legacyMc && IsModernOptionsKey(key, val))
                        continue;

                    map[key] = val;
                }
            }

            // Окно гарантированно на экране (не 0×0 и не off-screen)
            width = Math.Clamp(width, 854, 1920);
            height = Math.Clamp(height, 480, 1080);

            map["fullscreen"] = "false";
            // 1.12: override* иногда рождает Display без нормального HWND — не пишем на legacy
            if (legacyMc)
            {
                map.Remove("overrideWidth");
                map.Remove("overrideHeight");
            }
            else
            {
                map["overrideWidth"] = width.ToString();
                map["overrideHeight"] = height.ToString();
            }

            map.Remove("fullscreenResolution");

            // 1.12 не понимает часть новых ключей — не пишем их
            if (!legacyMc)
            {
                map["multiplayerAllowed"] = "true";
                map["realmsAllowed"] = "true";
            }

            // Базовые опции, если файла не было / всё вычистили
            if (!map.ContainsKey("renderDistance") && !map.ContainsKey("renderDistanceChunks"))
            {
                if (legacyMc)
                    map["renderDistance"] = "8";
                else
                    map["renderDistance"] = "8";
            }

            if (legacyMc)
            {
                // fov:0.0 из 1.16 ломает камеру — нормализуем
                if (!map.TryGetValue("fov", out var fov) ||
                    !double.TryParse(fov, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fovN) ||
                    fovN is < 0.1 or > 1.5)
                {
                    map["fov"] = "0.0"; // 1.12: 0.0 = normal 70°
                }

                map["guiScale"] = map.GetValueOrDefault("guiScale", "0");
                map["maxFps"] = map.GetValueOrDefault("maxFps", "120");
                map["enableVsync"] = map.GetValueOrDefault("enableVsync", "true");
                map["lang"] = map.GetValueOrDefault("lang", "en_us");
            }

            if (borderlessWindow)
                map["ofFullscreenMode"] = "Default";

            var lines = map.Select(kv => $"{kv.Key}:{kv.Value}").OrderBy(s => s, StringComparer.Ordinal);
            File.WriteAllLines(path, lines);
        }
        catch
        {
            // не критично
        }
    }

    private static bool IsLegacyMinecraft(string gameVersionId)
    {
        // 1.12.x и ниже — старый options/key format
        var m = System.Text.RegularExpressions.Regex.Match(
            gameVersionId ?? "", @"^(?<a>\d+)\.(?<b>\d+)");
        if (!m.Success) return false;
        var major = int.Parse(m.Groups["a"].Value);
        var minor = int.Parse(m.Groups["b"].Value);
        return major < 1 || (major == 1 && minor <= 12);
    }

    private static bool IsModernOptionsKey(string key, string value)
    {
        // Бинды 1.13+: key_key.forward:key.keyboard.w
        if (key.StartsWith("key_key.", StringComparison.OrdinalIgnoreCase) &&
            (value.Contains("key.keyboard", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("key.mouse", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Опции, появившиеся в 1.13–1.16 и ломающие/засоряющие 1.12
        return key is
            "attackIndicator" or "autoSuggestions" or "backgroundForChatOnly" or
            "biomeBlendRadius" or "chatDelay" or "chatLineSpacing" or
            "entityDistanceScaling" or "entityShadows" or "forceUnicodeFont" or
            "fovEffectScale" or "glDebugVerbosity" or "graphicsMode" or
            "hideMatchedNames" or "joinedFirstServer" or "mouseWheelSensitivity" or
            "narrator" or "rawMouseInput" or "reducedDebugInfo" or
            "screenEffectScale" or "skipMultiplayerWarning" or
            "syncChunkWrites" or "textBackgroundOpacity" or
            "toggleCrouch" or "toggleSprint" or "tutorialStep" or
            "useNativeTransport" or "multiplayerAllowed" or "realmsAllowed" or
            "discrete_mouse_scroll" or "modelPart_cape" or "modelPart_jacket" or
            "modelPart_left_sleeve" or "modelPart_right_sleeve" or
            "modelPart_left_pants_leg" or "modelPart_right_pants_leg" or
            "modelPart_hat";
    }

    /// <summary>
    /// Offline UUID: MD5("OfflinePlayer:"+name) → 8-4-4-4-12.
    /// Невалидный/пустой compact → пересчёт от ника.
    /// </summary>
    private static string BuildOfflineUuidDashed(string playerName, string? preferredCompact)
    {
        static bool IsHex32(string s)
        {
            if (s.Length != 32) return false;
            foreach (var c in s)
            {
                var ok = (c is >= '0' and <= '9') ||
                         (c is >= 'a' and <= 'f') ||
                         (c is >= 'A' and <= 'F');
                if (!ok) return false;
            }

            return true;
        }

        var compact = preferredCompact?
            .Replace("-", "", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();

        // "xxxx..." / все нули / мусор — пересчитываем
        if (compact is null ||
            !IsHex32(compact) ||
            compact.All(c => c is '0' or 'x'))
        {
            compact = SkinService.OfflineUuidCompact(playerName);
        }

        return SkinService.FormatUuid(compact);
    }

    /// <summary>
    /// Game-аргументы: инфраструктура из profile + ровно один блок offline-сессии.
    /// </summary>
    private static void AppendGameArgs(
        JsonObject profile,
        List<string> args,
        string nickname,
        string uuid,
        string accessToken,
        string versionId,
        string gameDir,
        string assetsDir,
        string assetIndex,
        int width,
        int height,
        bool exclusiveFullscreen)
    {
        // Только инфраструктура profile (без auth/session — их добавим в конце один раз)
        AppendProfileInfrastructureArgs(
            profile, args, nickname, uuid, accessToken,
            versionId, gameDir, assetsDir, assetIndex);

        // Снести ВСЕ session-ключи (в т.ч. дубликаты из profile), потом записать чисто
        RemoveAllArgKeys(args,
            "--username", "--uuid", "--accessToken", "--userType",
            "--userProperties", "--versionType", "--clientId", "--xuid",
            "--demo");

        RemoveAllArgKeys(args, "--width", "--height", "--fullscreen");
        RemoveQuickPlayArgs(args);

        // --- Offline-сессия (один раз) ---
        // token: 32 hex. Реальные privileges API не вызываются при offline JVM hosts.
        var finalUuid = BuildOfflineUuidDashed(nickname, uuid);
        var finalToken = Guid.NewGuid().ToString("N");

        EnsureArg(args, "--username", nickname);
        EnsureArg(args, "--uuid", finalUuid);
        EnsureArg(args, "--accessToken", finalToken);
        EnsureArg(args, "--userType", "legacy");
        EnsureArg(args, "--userProperties", "{}");
        EnsureArg(args, "--versionType", "release");
        // ----------------------------------------------------

        EnsureArg(args, "--width", width.ToString());
        EnsureArg(args, "--height", height.ToString());

        if (exclusiveFullscreen)
            args.Add("--fullscreen");
    }

    /// <summary>
    /// authlib EnvironmentParser: custom env применяется только если заданы ВСЕ 4 host'а.
    /// Недоступные hosts → SocialInteractionsService падает → OfflineSocialInteractions
    /// → serversAllowed=true (кнопка Multiplayer активна).
    /// </summary>
    private static void AppendOfflineAuthJvmFlags(List<string> args)
    {
        // 127.0.0.1:9 — connection refused, без долгого hang
        const string dead = "http://127.0.0.1:9";
        EnsureJvmSystemProperty(args, "-Dminecraft.api.env=custom");
        EnsureJvmSystemProperty(args, $"-Dminecraft.api.auth.host={dead}");
        EnsureJvmSystemProperty(args, $"-Dminecraft.api.account.host={dead}");
        EnsureJvmSystemProperty(args, $"-Dminecraft.api.session.host={dead}");
        EnsureJvmSystemProperty(args, $"-Dminecraft.api.services.host={dead}");
        EnsureJvmSystemProperty(args, "-Dcom.mojang.minecraft.offline=true");
    }

    /// <summary>Задаёт --key value: обновляет существующее или добавляет пару один раз.</summary>
    private static void EnsureArg(List<string> args, string key, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                args[i + 1] = value;
                return;
            }
        }

        args.Add(key);
        args.Add(value);
    }

    /// <summary>gameDir / assets / tweakClass… без session-аргументов.</summary>
    private static void AppendProfileInfrastructureArgs(
        JsonObject profile,
        List<string> args,
        string nickname,
        string uuid,
        string accessToken,
        string versionId,
        string gameDir,
        string assetsDir,
        string assetIndex)
    {
        // Session / окно / demo / quickPlay — задаём сами, из profile не берём
        static bool IsSkipKey(string s) =>
            s is "--username" or "--uuid" or "--accessToken" or "--userType" or
                "--userProperties" or "--versionType" or "--clientId" or "--xuid" or
                "--width" or "--height" or "--fullscreen" or "--demo" ||
            s.StartsWith("--quickPlay", StringComparison.OrdinalIgnoreCase);

        static bool SkipValue(List<string> tokens, int i) =>
            i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal);

        static string Fill(string s, string nick, string uid, string token, string ver,
            string gDir, string aDir, string aIdx) =>
            s.Replace("${auth_player_name}", nick)
                .Replace("${version_name}", ver)
                .Replace("${game_directory}", gDir)
                .Replace("${assets_root}", aDir)
                .Replace("${assets_index_name}", aIdx)
                .Replace("${auth_uuid}", uid)
                .Replace("${auth_access_token}", token)
                .Replace("${user_type}", "legacy")
                .Replace("${version_type}", "release")
                .Replace("${user_properties}", "{}")
                .Replace("${clientid}", "")
                .Replace("${auth_xuid}", "");

        // quickPlay / demo / resolution-only placeholders (не инфраструктура)
        static bool IsJunkPlaceholder(string s) =>
            s.Contains("${is_demo_user}", StringComparison.Ordinal) ||
            s.Contains("${quickPlay", StringComparison.Ordinal) ||
            s.Contains("${resolution_width}", StringComparison.Ordinal) ||
            s.Contains("${resolution_height}", StringComparison.Ordinal);

        void ConsumeTokens(List<string> tokens)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (string.IsNullOrWhiteSpace(t))
                    continue;
                if (IsSkipKey(t))
                {
                    if (SkipValue(tokens, i)) i++;
                    continue;
                }

                // Незаменённый плейсхолдер — пропуск
                if (t.StartsWith("${", StringComparison.Ordinal) && t.EndsWith('}'))
                    continue;

                if (t.Equals("demo", StringComparison.OrdinalIgnoreCase))
                    continue;

                args.Add(t);
            }
        }

        var tokens = new List<string>();
        var hadLegacyOrModern = false;

        // 1.12 и старше: minecraftArguments — основная строка
        if (profile["minecraftArguments"] is JsonValue legacy &&
            legacy.TryGetValue<string>(out var legacyArgs) &&
            !string.IsNullOrWhiteSpace(legacyArgs))
        {
            var replaced = Fill(legacyArgs, nickname, uuid, accessToken, versionId, gameDir, assetsDir, assetIndex);
            tokens.AddRange(SplitArgs(replaced));
            hadLegacyOrModern = true;
        }

        // Современный формат И/ИЛИ дописка OptiFine/Forge (--tweakClass) поверх legacy
        // Важно: НЕ return после minecraftArguments — иначе теряется --tweakClass из child profile
        if (profile["arguments"]?["game"] is JsonArray gameArr)
        {
            foreach (var node in gameArr)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
                    continue;
                if (IsJunkPlaceholder(s))
                    continue;
                tokens.Add(Fill(s, nickname, uuid, accessToken, versionId, gameDir, assetsDir, assetIndex));
            }

            hadLegacyOrModern = true;
        }

        if (hadLegacyOrModern)
        {
            ConsumeTokens(tokens);
        }
        else
        {
            args.AddRange(
            [
                "--version", versionId,
                "--gameDir", gameDir,
                "--assetsDir", assetsDir,
                "--assetIndex", assetIndex
            ]);
        }

        // Инфраструктура должна быть всегда (даже если profile её не дал)
        EnsureArg(args, "--version", versionId);
        EnsureArg(args, "--gameDir", gameDir);
        EnsureArg(args, "--assetsDir", assetsDir);
        EnsureArg(args, "--assetIndex", assetIndex);
    }

    /// <summary>
    /// Для LaunchWrapper обязательно передать --tweakClass (OptiFine/Forge),
    /// иначе Launch грузит VanillaTweaker и падает с ClassNotFoundException.
    /// Vanilla (mainClass без launchwrapper) — no-op.
    /// Fabric/Quilt — no-op (свой mainClass).
    /// </summary>
    private static void EnsureLaunchWrapperTweakClass(
        JsonObject profile,
        List<string> args,
        string mainClass)
    {
        if (!mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase))
            return;

        // Уже есть --tweakClass (один раз; сносим дубликаты)
        string? existing = null;
        for (var i = args.Count - 2; i >= 0; i--)
        {
            if (!string.Equals(args[i], "--tweakClass", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(args[i + 1]))
            {
                args.RemoveAt(i + 1);
                args.RemoveAt(i);
                continue;
            }

            if (existing is null)
            {
                existing = args[i + 1];
            }
            else
            {
                // дубликат
                args.RemoveAt(i + 1);
                args.RemoveAt(i);
            }
        }

        if (existing is not null)
            return;

        // Из profile.arguments.game
        if (profile["arguments"]?["game"] is JsonArray gameArr)
        {
            for (var i = 0; i < gameArr.Count - 1; i++)
            {
                var a = gameArr[i]?.GetValue<string>();
                var b = gameArr[i + 1]?.GetValue<string>();
                if (string.Equals(a, "--tweakClass", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(b))
                {
                    args.Add("--tweakClass");
                    args.Add(b!);
                    return;
                }
            }
        }

        // Из minecraftArguments (редко, но бывает)
        if (profile["minecraftArguments"] is JsonValue leg &&
            leg.TryGetValue<string>(out var legacy) &&
            !string.IsNullOrWhiteSpace(legacy))
        {
            var parts = SplitArgs(legacy).ToList();
            for (var i = 0; i < parts.Count - 1; i++)
            {
                if (string.Equals(parts[i], "--tweakClass", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(parts[i + 1]))
                {
                    args.Add("--tweakClass");
                    args.Add(parts[i + 1]);
                    return;
                }
            }
        }

        // Угадать по libraries (только модлоадеры, не pure vanilla+LW)
        if (profile["libraries"] is JsonArray libs)
        {
            foreach (var lib in libs)
            {
                var name = lib?["name"]?.GetValue<string>() ?? "";
                if (name.Contains("optifine:OptiFine", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("optifine:OptiFine", StringComparison.OrdinalIgnoreCase))
                {
                    args.Add("--tweakClass");
                    args.Add("optifine.OptiFineTweaker");
                    return;
                }

                if (name.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(":forge:", StringComparison.OrdinalIgnoreCase))
                {
                    args.Add("--tweakClass");
                    args.Add("net.minecraftforge.fml.common.launcher.FMLTweaker");
                    return;
                }

                if (name.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
                {
                    // NeoForge 1.20.2+ обычно не LaunchWrapper; если всё же LW — FML
                    args.Add("--tweakClass");
                    args.Add("net.minecraftforge.fml.common.launcher.FMLTweaker");
                    return;
                }
            }
        }
    }

    /// <summary>Удаляет все вхождения --key [value] из списка.</summary>
    private static void RemoveAllArgKeys(List<string> args, params string[] keys)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            var hit = false;
            foreach (var key in keys)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
                continue;

            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                args.RemoveAt(i + 1);
            args.RemoveAt(i);
        }
    }

    private static void RemoveQuickPlayArgs(List<string> args)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (!args[i].StartsWith("--quickPlay", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                args.RemoveAt(i + 1);
            args.RemoveAt(i);
        }
    }

    /// <summary>Добавляет -D… свойство, если такого префикса ещё нет.</summary>
    private static void EnsureJvmSystemProperty(List<string> args, string property)
    {
        var eq = property.IndexOf('=');
        var prefix = eq > 0 ? property[..(eq + 1)] : property;
        // Ищем только среди JVM-части (до -cp / mainClass не трогаем classpath)
        var cpIdx = args.FindIndex(a => a is "-cp" or "-classpath");
        var limit = cpIdx >= 0 ? cpIdx : args.Count;

        for (var i = 0; i < limit; i++)
        {
            if (args[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                args[i] = property;
                return;
            }
        }

        // Вставить перед -cp, если есть
        if (cpIdx >= 0)
            args.Insert(cpIdx, property);
        else
            args.Add(property);
    }

    private static string ReplacePlaceholders(string s, string nativesDir, string classpath, string root) =>
        s.Replace("${natives_directory}", nativesDir)
            .Replace("${launcher_name}", "ZLauncher")
            .Replace("${launcher_version}", "1.0")
            .Replace("${classpath}", classpath)
            .Replace("${library_directory}", Path.Combine(root, "libraries"));

    private static IEnumerable<string> SplitArgs(string raw)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in raw)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }

                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            list.Add(sb.ToString());

        return list;
    }

}
