using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZLauncher.Installer.Services;

/// <summary>
/// Скачивает последний ZLauncher-Portable.zip с GitHub Releases.
/// Setup-stub всегда ставит актуальную версию.
/// </summary>
public sealed class GitHubPackageService
{
    public const string Owner = "exteriya1337";
    public const string Repo = "ZLauncher";
    public const string PortableAssetName = "ZLauncher-Portable.zip";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ZLauncher.Setup/1.0");
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string ReleasesLatestApi =>
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public async Task<(string ZipPath, string Version, string AssetName)> DownloadLatestPortableAsync(
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report((0.02, "Запрос releases/latest…"));

        using var resp = await Http.GetAsync(ReleasesLatestApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = tag.TrimStart('v', 'V');

        string? url = null;
        string assetName = PortableAssetName;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(dl)) continue;

                if (name.Equals(PortableAssetName, StringComparison.OrdinalIgnoreCase) ||
                    (name.Contains("Portable", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    url = dl;
                    assetName = name;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "В последнем релизе нет ZLauncher-Portable.zip. " +
                "Опубликуй portable-ассет на GitHub Releases.");

        progress?.Report((0.08, $"Скачивание {assetName} (v{version})…"));

        var dir = Path.Combine(Path.GetTempPath(), "ZLauncher-Setup-Download");
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, assetName);

        using (var netResp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            netResp.EnsureSuccessStatusCode();
            var total = netResp.Content.Headers.ContentLength ?? -1L;
            await using var net = await netResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);

            var buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    var p = 0.08 + 0.75 * (read / (double)total);
                    var mb = read / (1024.0 * 1024.0);
                    var tmb = total / (1024.0 * 1024.0);
                    progress?.Report((p, $"Скачивание… {mb:F1} / {tmb:F1} МБ"));
                }
                else
                {
                    progress?.Report((0.4, $"Скачивание… {read / (1024.0 * 1024.0):F1} МБ"));
                }
            }
        }

        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 1000)
            throw new InvalidOperationException("Скачанный архив пуст или повреждён.");

        progress?.Report((0.85, "Пакет скачан"));
        return (zipPath, version, assetName);
    }
}
