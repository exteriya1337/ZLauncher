using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Проверка обновлений через GitHub Releases API.
/// </summary>
public sealed class LauncherUpdateService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
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
                    CurrentVersion = current,
                    Error = null
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

            var available = IsNewer(latest, current);

            return new UpdateCheckResult
            {
                UpdateAvailable = available,
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
    /// Скачивает установщик обновления и запускает его, затем завершает текущий процесс.
    /// </summary>
    public async Task DownloadAndRunSetupAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ZLauncher-Update");
        Directory.CreateDirectory(tempDir);
        var target = Path.Combine(tempDir, AppInfo.SetupAssetName);

        await using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var net = await Http.GetStreamAsync(downloadUrl, ct).ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = -1;
            // try get length from HEAD-less stream — often unknown
            long read = 0;
            int n;
            while ((n = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0)
                    progress?.Report(Math.Clamp(read / (double)total, 0, 1));
                else
                    progress?.Report(0);
            }
        }

        progress?.Report(1);

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
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
        // strip pre-release suffix for comparison base if needed
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return s;
    }

    /// <summary>true if remote &gt; local.</summary>
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
