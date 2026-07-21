using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ZLauncher.Services;

public sealed class PlayerSkinInfo
{
    public required string Nickname { get; init; }
    /// <summary>UUID с дефисами.</summary>
    public required string Uuid { get; init; }
    public required string UuidCompact { get; init; }
    public bool IsPremium { get; init; }
    public string? SkinFilePath { get; init; }
    /// <summary>Квадрат лица (голова) для превью в UI.</summary>
    public string? HeadFilePath { get; init; }
    public string? SkinUrl { get; init; }
}

/// <summary>
/// Загрузка скинов через публичные API Mojang / зеркала.
/// Для премиум-ников в игре используется их онлайн-UUID — клиенты других
/// игроков смогут подтянуть скин с sessionserver.mojang.com.
/// </summary>
public sealed class SkinService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly string _skinsRoot;

    public SkinService()
    {
        _skinsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher",
            "skins");
        Directory.CreateDirectory(_skinsRoot);
    }

    public string SkinsRoot => _skinsRoot;

    public string GetSkinCachePath(string uuidCompact) =>
        Path.Combine(_skinsRoot, $"{uuidCompact.ToLowerInvariant()}.png");

    public string GetHeadCachePath(string uuidCompact) =>
        Path.Combine(_skinsRoot, $"{uuidCompact.ToLowerInvariant()}_head.png");

    /// <summary>
    /// Резолвит UUID и скачивает скин.
    /// <paramref name="forceOffline"/> — обычный аккаунт:
    /// всегда OfflinePlayer UUID, без привязки к Microsoft.
    /// </summary>
    public async Task<PlayerSkinInfo> ResolveAndFetchAsync(
        string nickname,
        CancellationToken cancellationToken = default,
        bool forceOffline = true)
    {
        nickname = nickname.Trim();
        if (string.IsNullOrEmpty(nickname))
            nickname = "Player";

        string uuidCompact;
        string uuidDashed;
        bool isPremium;

        if (forceOffline)
        {
            // Обычный аккаунт: UUID как у offline-клиента Minecraft
            uuidCompact = OfflineUuidCompact(nickname);
            uuidDashed = FormatUuid(uuidCompact);
            isPremium = false;
        }
        else
        {
            var premium = await TryGetPremiumProfileAsync(nickname, cancellationToken)
                .ConfigureAwait(false);

            if (premium is not null)
            {
                uuidCompact = premium.Value.Uuid;
                uuidDashed = FormatUuid(uuidCompact);
                isPremium = true;
            }
            else
            {
                uuidCompact = OfflineUuidCompact(nickname);
                uuidDashed = FormatUuid(uuidCompact);
                isPremium = false;
            }
        }

        string? skinPath = null;
        string? skinUrl = null;

        // Скин: для премиум — sessionserver; для обычного — зеркала по нику (косметика)
        if (isPremium)
        {
            var tex = await TryGetTextureUrlAsync(uuidCompact, cancellationToken)
                .ConfigureAwait(false);
            skinUrl = tex;

            if (!string.IsNullOrEmpty(tex))
            {
                skinPath = await DownloadSkinAsync(uuidCompact, tex, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (skinPath is null || !File.Exists(skinPath))
        {
            // Для offline-аккаунта кэшируем скин под offline-uuid, чтобы не пересекаться с premium
            skinPath = await TryDownloadFromMirrorsAsync(nickname, uuidCompact, cancellationToken)
                .ConfigureAwait(false);
            if (skinPath is not null)
                skinUrl ??= $"mirror:{nickname}";
        }

        string? headPath = null;
        if (skinPath is not null && File.Exists(skinPath))
            headPath = BuildHeadPreview(skinPath, uuidCompact);

        // Если скин не скачался — пробуем готовый аватар-лицо с зеркал
        if (headPath is null || !File.Exists(headPath))
            headPath = await TryDownloadHeadAvatarAsync(nickname, uuidCompact, cancellationToken)
                .ConfigureAwait(false);

        return new PlayerSkinInfo
        {
            Nickname = nickname,
            Uuid = uuidDashed,
            UuidCompact = uuidCompact,
            IsPremium = isPremium,
            SkinFilePath = skinPath,
            HeadFilePath = headPath,
            SkinUrl = skinUrl
        };
    }

    /// <summary>
    /// Вырезает лицо из skin-текстуры (8×8 front head + hat overlay) и
    /// увеличивает до 64×64 nearest-neighbor (пиксельный вид Minecraft).
    /// </summary>
    public string? BuildHeadPreview(string skinPath, string uuidCompact)
    {
        try
        {
            var headPath = GetHeadCachePath(uuidCompact);
            if (File.Exists(headPath) &&
                File.GetLastWriteTimeUtc(headPath) >= File.GetLastWriteTimeUtc(skinPath))
                return headPath;

            using var skin = SKBitmap.Decode(skinPath);
            if (skin is null || skin.Width < 64 || skin.Height < 32)
                return null;

            // HD-скины (128×128 и т.д.): масштаб относительно 64
            var scale = Math.Max(1, skin.Width / 64);
            var face = 8 * scale;

            // Front face: (8,8); hat overlay: (40,8)
            var faceX = 8 * scale;
            var faceY = 8 * scale;
            var hatX = 40 * scale;
            var hatY = 8 * scale;

            using var head = new SKBitmap(face, face, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            for (var y = 0; y < face; y++)
            {
                for (var x = 0; x < face; x++)
                {
                    var basePx = skin.GetPixel(faceX + x, faceY + y);
                    // hat только если альфа > 0
                    if (skin.Height >= 32 && hatX + x < skin.Width && hatY + y < skin.Height)
                    {
                        var hatPx = skin.GetPixel(hatX + x, hatY + y);
                        if (hatPx.Alpha > 8)
                            basePx = BlendOver(basePx, hatPx);
                    }

                    head.SetPixel(x, y, basePx);
                }
            }

            // Апскейл 64px nearest-neighbor
            const int outSize = 64;
            using var scaled = new SKBitmap(outSize, outSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            for (var y = 0; y < outSize; y++)
            {
                var sy = y * face / outSize;
                for (var x = 0; x < outSize; x++)
                {
                    var sx = x * face / outSize;
                    scaled.SetPixel(x, y, head.GetPixel(sx, sy));
                }
            }

            using var image = SKImage.FromBitmap(scaled);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
                return null;

            using var fs = File.Create(headPath);
            data.SaveTo(fs);
            return headPath;
        }
        catch
        {
            return null;
        }
    }

    private static SKColor BlendOver(SKColor under, SKColor over)
    {
        if (over.Alpha >= 250)
            return over;
        if (over.Alpha <= 0)
            return under;

        var a = over.Alpha / 255f;
        var ia = 1f - a;
        return new SKColor(
            (byte)(over.Red * a + under.Red * ia),
            (byte)(over.Green * a + under.Green * ia),
            (byte)(over.Blue * a + under.Blue * ia),
            (byte)Math.Min(255, over.Alpha + under.Alpha * ia));
    }

    private async Task<string?> TryDownloadHeadAvatarAsync(
        string nickname,
        string uuidCompact,
        CancellationToken ct)
    {
        var path = GetHeadCachePath(uuidCompact);
        if (File.Exists(path) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(12))
            return path;

        var urls = new[]
        {
            $"https://mc-heads.net/avatar/{Uri.EscapeDataString(nickname)}/64",
            $"https://minotar.net/helm/{Uri.EscapeDataString(nickname)}/64",
            $"https://crafatar.com/avatars/{uuidCompact}?size=64&overlay=true"
        };

        foreach (var url in urls)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;

                await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(path);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);

                if (new FileInfo(path).Length > 50)
                    return path;
            }
            catch
            {
                // next
            }
        }

        return File.Exists(path) ? path : null;
    }

    private async Task<(string Uuid, string Name)?> TryGetPremiumProfileAsync(
        string nickname,
        CancellationToken ct)
    {
        try
        {
            // Актуальный API
            var url =
                $"https://api.minecraftservices.com/minecraft/profile/lookup/name/{Uri.EscapeDataString(nickname)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // legacy
                url =
                    $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(nickname)}";
                using var resp2 = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp2.IsSuccessStatusCode)
                    return null;

                await using var s2 = await resp2.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc2 = await JsonDocument.ParseAsync(s2, cancellationToken: ct)
                    .ConfigureAwait(false);
                var id2 = doc2.RootElement.GetProperty("id").GetString();
                var name2 = doc2.RootElement.TryGetProperty("name", out var n2)
                    ? n2.GetString() ?? nickname
                    : nickname;
                return id2 is null ? null : (id2, name2);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            var id = doc.RootElement.GetProperty("id").GetString();
            var name = doc.RootElement.TryGetProperty("name", out var n)
                ? n.GetString() ?? nickname
                : nickname;
            return id is null ? null : (id, name);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetTextureUrlAsync(string uuidCompact, CancellationToken ct)
    {
        try
        {
            var url =
                $"https://sessionserver.mojang.com/session/minecraft/profile/{uuidCompact}?unsigned=true";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("properties", out var props))
                return null;

            foreach (var prop in props.EnumerateArray())
            {
                if (prop.GetProperty("name").GetString() != "textures")
                    continue;

                var b64 = prop.GetProperty("value").GetString();
                if (string.IsNullOrEmpty(b64))
                    return null;

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                using var texDoc = JsonDocument.Parse(json);
                if (texDoc.RootElement.TryGetProperty("textures", out var textures) &&
                    textures.TryGetProperty("SKIN", out var skin) &&
                    skin.TryGetProperty("url", out var skinUrl))
                {
                    return skinUrl.GetString();
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task<string?> DownloadSkinAsync(
        string uuidCompact,
        string skinUrl,
        CancellationToken ct)
    {
        var path = GetSkinCachePath(uuidCompact);
        try
        {
            // Не качаем снова, если свежий кэш
            if (File.Exists(path) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(12))
                return path;

            using var resp = await Http.GetAsync(skinUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(path);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            return path;
        }
        catch
        {
            return File.Exists(path) ? path : null;
        }
    }

    private async Task<string?> TryDownloadFromMirrorsAsync(
        string nickname,
        string uuidCompact,
        CancellationToken ct)
    {
        var path = GetSkinCachePath(uuidCompact);
        var mirrors = new[]
        {
            $"https://mc-heads.net/skin/{Uri.EscapeDataString(nickname)}",
            $"https://minotar.net/skin/{Uri.EscapeDataString(nickname)}",
            $"https://crafatar.com/skins/{uuidCompact}"
        };

        foreach (var url in mirrors)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;

                await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(path);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);

                if (new FileInfo(path).Length > 100)
                    return path;
            }
            catch
            {
                // next
            }
        }

        return File.Exists(path) ? path : null;
    }

    public static string OfflineUuidCompact(string nickname)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes("OfflinePlayer:" + nickname));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string FormatUuid(string compact)
    {
        compact = compact.Replace("-", "", StringComparison.Ordinal);
        if (compact.Length != 32)
            return compact;

        return
            $"{compact[..8]}-{compact.Substring(8, 4)}-{compact.Substring(12, 4)}-{compact.Substring(16, 4)}-{compact.Substring(20)}";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 ZLauncher/1.0");
        return client;
    }
}
