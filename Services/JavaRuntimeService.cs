using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZLauncher.Services;

/// <summary>
/// Поиск / автоустановка Java runtime от Mojang.
/// Для разных версий MC нужны разные runtime (1.16+LW → Java 8, 1.17+ → 17, 1.20.5+ → 21).
/// </summary>
public sealed class JavaRuntimeService
{
    private const string RuntimeAllUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    public const string ComponentJava8 = "jre-legacy";
    public const string ComponentJava17 = "java-runtime-gamma";
    public const string ComponentJava21 = "java-runtime-delta";

    private static readonly HttpClient Http = CreateClient();

    private readonly string _runtimeRoot;

    public JavaRuntimeService()
    {
        _runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher",
            "runtime");
        Directory.CreateDirectory(_runtimeRoot);
    }

    public string RuntimeRoot => _runtimeRoot;

    /// <summary>
    /// Подбирает компонент Java под версию Minecraft / mainClass.
    /// LaunchWrapper (OptiFine 1.12–1.16) требует Java 8.
    /// </summary>
    public static string SelectComponentForGame(string gameVersionId, string? mainClass)
    {
        if (!string.IsNullOrEmpty(mainClass) &&
            mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase))
            return ComponentJava8;

        if (TryParseMcVersion(gameVersionId, out _, out var minor, out var patch))
        {
            // 1.20.5+ → Java 21
            if (minor > 20 || (minor == 20 && patch >= 5))
                return ComponentJava21;

            // 1.17 – 1.20.4 → Java 17
            if (minor >= 17)
                return ComponentJava17;

            // ≤ 1.16 → Java 8
            return ComponentJava8;
        }

        return ComponentJava17;
    }

    /// <summary>Ищет уже установленный runtime (конкретный или любой).</summary>
    public string? FindJavaExecutable(string? preferredComponent = null)
    {
        if (!string.IsNullOrEmpty(preferredComponent))
        {
            var specific = GetBundledJavaPath(preferredComponent);
            if (specific is not null)
                return specific;
        }

        // Любой bundled (предпочитаем delta → gamma → legacy)
        foreach (var c in new[] { ComponentJava21, ComponentJava17, ComponentJava8 })
        {
            var p = GetBundledJavaPath(c);
            if (p is not null)
                return p;
        }

        return FindSystemJava();
    }

    public string? GetBundledJavaPath(string component, bool windowed = true)
    {
        // javaw.exe — без чёрной консоли; java.exe — с консолью (для отладки)
        var name = OperatingSystem.IsWindows()
            ? (windowed ? "javaw.exe" : "java.exe")
            : "java";
        var exe = Path.Combine(_runtimeRoot, component, "bin", name);
        if (File.Exists(exe))
            return exe;

        // fallback: java.exe если javaw нет
        if (OperatingSystem.IsWindows() && windowed)
        {
            var console = Path.Combine(_runtimeRoot, component, "bin", "java.exe");
            if (File.Exists(console))
                return console;
        }

        return null;
    }

    /// <summary>
    /// Гарантирует наличие runtime. Если нет — скачивает с серверов Mojang.
    /// </summary>
    public async Task<string> EnsureJavaAsync(
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await EnsureJavaAsync(ComponentJava21, progress, cancellationToken).ConfigureAwait(false);

    public async Task<string> EnsureJavaAsync(
        string component,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(component))
            component = ComponentJava21;

        var existing = GetBundledJavaPath(component);
        if (existing is not null)
        {
            progress?.Report(new InstallProgress
            {
                Progress = 1,
                Stage = "Java готова",
                CurrentFile = existing
            });
            return existing;
        }

        // Для Java 17/21 можно взять системную, если версия подходит — но проверка сложная.
        // Надёжнее всегда качать нужный Mojang runtime.

        progress?.Report(new InstallProgress
        {
            Progress = 0.02,
            Stage = "Java · манифест",
            CurrentFile = component
        });

        var platform = GetPlatformKey();
        var allJson = await Http.GetStringAsync(RuntimeAllUrl, cancellationToken).ConfigureAwait(false);
        using var allDoc = JsonDocument.Parse(allJson);

        if (!allDoc.RootElement.TryGetProperty(platform, out var platformNode))
            throw new InvalidOperationException($"Нет Java runtime для платформы {platform}.");

        if (!platformNode.TryGetProperty(component, out var componentNode))
            throw new InvalidOperationException($"Компонент Java «{component}» не найден.");

        // Иногда это массив
        var entry = componentNode.ValueKind == JsonValueKind.Array
            ? componentNode.EnumerateArray().First()
            : componentNode;

        var manifestUrl = entry.GetProperty("manifest").GetProperty("url").GetString()
                          ?? throw new InvalidOperationException("Нет URL манифеста Java.");

        var versionName = entry.GetProperty("version").GetProperty("name").GetString() ?? component;

        progress?.Report(new InstallProgress
        {
            Progress = 0.05,
            Stage = $"Java {versionName} · файлы",
            CurrentFile = component
        });

        var manifestJson = await Http.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        using var manDoc = JsonDocument.Parse(manifestJson);
        var files = manDoc.RootElement.GetProperty("files");

        var installDir = Path.Combine(_runtimeRoot, component);
        if (Directory.Exists(installDir))
        {
            try { Directory.Delete(installDir, recursive: true); }
            catch { /* ignore */ }
        }

        Directory.CreateDirectory(installDir);

        var downloads = new List<(string RelPath, string Url, long Size)>();
        foreach (var prop in files.EnumerateObject())
        {
            var rel = prop.Name.Replace('/', Path.DirectorySeparatorChar);
            var val = prop.Value;
            var type = val.GetProperty("type").GetString();

            if (type == "directory")
            {
                Directory.CreateDirectory(Path.Combine(installDir, rel));
                continue;
            }

            if (type != "file")
                continue;

            if (!val.TryGetProperty("downloads", out var dls) ||
                !dls.TryGetProperty("raw", out var raw))
                continue;

            var url = raw.GetProperty("url").GetString();
            var size = raw.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            if (string.IsNullOrEmpty(url))
                continue;

            downloads.Add((rel, url, size));
        }

        long total = Math.Max(downloads.Sum(d => d.Size > 0 ? d.Size : 1), 1);
        long done = 0;
        var sw = Stopwatch.StartNew();

        using var gate = new SemaphoreSlim(8);
        var tasks = downloads.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var dest = Path.Combine(installDir, item.RelPath);
                await DownloadFileAsync(item.Url, dest, cancellationToken).ConfigureAwait(false);

                if (!OperatingSystem.IsWindows() &&
                    item.RelPath.Replace('\\', '/').Contains("/bin/"))
                {
                    try
                    {
                        File.SetUnixFileMode(dest,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch { /* ignore */ }
                }

                var add = item.Size > 0 ? item.Size : new FileInfo(dest).Length;
                var totalDone = Interlocked.Add(ref done, add);
                var p = 0.05 + 0.93 * Math.Clamp((double)totalDone / total, 0, 1);
                var speed = sw.Elapsed.TotalSeconds > 0 ? totalDone / sw.Elapsed.TotalSeconds : 0;

                progress?.Report(new InstallProgress
                {
                    Progress = p,
                    BytesDownloaded = totalDone,
                    TotalBytes = total,
                    SpeedBytesPerSecond = speed,
                    Stage = $"Java {versionName}",
                    CurrentFile = Path.GetFileName(item.RelPath)
                });
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var javaPath = GetBundledJavaPath(component)
                       ?? throw new InvalidOperationException(
                           $"Java «{component}» скачана, но java.exe не найден.");

        progress?.Report(new InstallProgress
        {
            Progress = 1,
            Stage = "Java готова",
            CurrentFile = javaPath
        });

        return javaPath;
    }

    private static string? FindSystemJava()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var p = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(p))
                return p;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = "java",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var line = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                    return line.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool TryParseMcVersion(string id, out int major, out int minor, out int patch)
    {
        major = 1;
        minor = 0;
        patch = 0;
        // 1.16.5 / 1.20.1 / 24w14a etc.
        var parts = id.Split('.');
        if (parts.Length < 2)
            return false;
        if (!int.TryParse(parts[0], out major))
            return false;
        // minor may have letters for snapshots
        var minorStr = new string(parts[1].TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(minorStr, out minor))
            return false;
        if (parts.Length >= 3)
        {
            var patchStr = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
            int.TryParse(patchStr, out patch);
        }

        return true;
    }

    private static string GetPlatformKey()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "windows-arm64",
                Architecture.X86 => "windows-x86",
                _ => "windows-x64"
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "mac-os-arm64"
                : "mac-os";
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.OSArchitecture == Architecture.X86
                ? "linux-i386"
                : "linux";
        }

        return "windows-x64";
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".part";
        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }

            throw;
        }

        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZLauncher/1.0");
        return client;
    }
}
