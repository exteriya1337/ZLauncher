using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZLauncher.Services;

public sealed class UpdateCheckResult
{
    public required bool UpdateAvailable { get; init; }
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseName { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? SetupDownloadUrl { get; init; }
    public string? PortableDownloadUrl { get; init; }
    public string? Notes { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Проверка и принудительная установка обновлений через GitHub Releases.
/// </summary>
public sealed class LauncherUpdateService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.ProductName}/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = AppInfo.Version;
        try
        {
            using var resp = await Http.GetAsync(AppInfo.ReleasesApiUrl, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult
                {
                    UpdateAvailable = false,
                    CurrentVersion = current
                };
            }

            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var latest = NormalizeVersion(tag);

            string? setupUrl = null;
            string? portableUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var an = asset.TryGetProperty("name", out var anEl) ? anEl.GetString() ?? "" : "";
                    var dl = asset.TryGetProperty("browser_download_url", out var dlEl)
                        ? dlEl.GetString()
                        : null;
                    if (string.IsNullOrEmpty(dl))
                        continue;

                    if (an.Equals(AppInfo.SetupAssetName, StringComparison.OrdinalIgnoreCase) ||
                        an.EndsWith(".Setup.exe", StringComparison.OrdinalIgnoreCase))
                        setupUrl = dl;
                    else if (an.Equals(AppInfo.PortableAssetName, StringComparison.OrdinalIgnoreCase) ||
                             (an.Contains("Portable", StringComparison.OrdinalIgnoreCase) &&
                              an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        portableUrl = dl;
                }
            }

            return new UpdateCheckResult
            {
                UpdateAvailable = IsNewer(latest, current),
                CurrentVersion = current,
                LatestVersion = latest,
                ReleaseName = name,
                ReleaseUrl = htmlUrl ?? AppInfo.ReleasesPageUrl,
                SetupDownloadUrl = setupUrl,
                PortableDownloadUrl = portableUrl,
                Notes = body
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                UpdateAvailable = false,
                CurrentVersion = current,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Скачивает Portable.zip, готовит bat-скрипт замены файлов и перезапуска, завершает процесс.
    /// Не возвращает управление при успехе (Environment.Exit).
    /// </summary>
    public async Task ApplyPortableUpdateAndRestartAsync(
        string portableDownloadUrl,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var work = Path.Combine(Path.GetTempPath(), "ZLauncher-ForceUpdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        var zipPath = Path.Combine(work, AppInfo.PortableAssetName);
        var extractDir = Path.Combine(work, "extract");
        Directory.CreateDirectory(extractDir);

        progress?.Report((0.05, "Скачивание обновления…"));
        await DownloadFileAsync(portableDownloadUrl, zipPath, p =>
        {
            progress?.Report((0.05 + p * 0.7, $"Скачивание… {(int)(p * 100)}%"));
        }, ct).ConfigureAwait(false);

        progress?.Report((0.78, "Распаковка…"));
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // Если zip содержит одну корневую папку — поднимем файлы
        extractDir = UnwrapSingleRoot(extractDir);

        var exeName = "ZLauncher.exe";
        if (!File.Exists(Path.Combine(extractDir, exeName)))
            throw new InvalidOperationException("В архиве обновления нет ZLauncher.exe.");

        progress?.Report((0.9, "Подготовка перезапуска…"));

        var batPath = Path.Combine(work, "apply-update.cmd");
        var pid = Environment.ProcessId;
        // cmd: wait for process, robocopy, start launcher
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"SRC={extractDir}\"");
        sb.AppendLine($"set \"DST={installDir}\"");
        sb.AppendLine($"set \"EXE={Path.Combine(installDir, exeName)}\"");
        sb.AppendLine($":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >NUL");
        sb.AppendLine("  goto wait");
        sb.AppendLine(")");
        sb.AppendLine("timeout /t 1 /nobreak >NUL");
        sb.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np >NUL");
        sb.AppendLine("start \"\" \"%EXE%\"");
        sb.AppendLine($"rd /s /q \"{work}\" 2>NUL");
        sb.AppendLine("endlocal");
        await File.WriteAllTextAsync(batPath, sb.ToString(), ct).ConfigureAwait(false);

        progress?.Report((0.98, "Перезапуск…"));

        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = work
        });

        // Дать bat стартовать
        await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
        Environment.Exit(0);
    }

    /// <summary>Скачать Setup-stub (онлайн) и запустить — для ручного обновления.</summary>
    public async Task DownloadAndRunSetupAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ZLauncher-Update");
        Directory.CreateDirectory(tempDir);
        var target = Path.Combine(tempDir, AppInfo.SetupAssetName);

        await DownloadFileAsync(downloadUrl, target, p => progress?.Report(p), ct).ConfigureAwait(false);

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static async Task DownloadFileAsync(
        string url,
        string target,
        Action<double>? progress,
        CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);

        var buffer = new byte[128 * 1024];
        long read = 0;
        int n;
        while ((n = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
                progress?.Invoke(Math.Clamp(read / (double)total, 0, 1));
            else
                progress?.Invoke(0);
        }

        progress?.Invoke(1);
    }

    private static string UnwrapSingleRoot(string extractDir)
    {
        var entries = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            if (File.Exists(Path.Combine(entries[0], "ZLauncher.exe")))
                return entries[0];
        }

        return extractDir;
    }

    public static void OpenReleasesPage(string? url = null)
    {
        var u = string.IsNullOrWhiteSpace(url) ? AppInfo.ReleasesPageUrl : url!;
        Process.Start(new ProcessStartInfo { FileName = u, UseShellExecute = true });
    }

    public static string NormalizeVersion(string tag)
    {
        var s = (tag ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return s;
    }

    public static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        if (string.IsNullOrWhiteSpace(local)) return true;

        if (Version.TryParse(Pad(remote), out var r) && Version.TryParse(Pad(local), out var l))
            return r > l;

        return string.Compare(remote, local, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string Pad(string v)
    {
        var parts = v.Split('-')[0].Split('.');
        while (parts.Length < 3)
            parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(4));
    }
}
