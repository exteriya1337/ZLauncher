using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZLauncher.Services;

/// <summary>
/// Краткий перевод EN→RU для описаний модов (бесплатный endpoint Google Translate).
/// При сбое возвращает исходный текст.
/// </summary>
public sealed class TranslationService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, string> Cache =
        new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(3, 3);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZLauncher/1.0 (mod-descriptions)");
        return client;
    }

    /// <summary>Перевести короткий текст на русский. Результат кэшируется.</summary>
    public async Task<string> ToRussianAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var source = text.Trim();
        if (source.Length > 480)
            source = source[..477] + "…";

        // Уже по-русски (кириллица) — не трогаем
        if (LooksMostlyCyrillic(source))
            return source;

        if (Cache.TryGetValue(source, out var cached))
            return cached;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(source, out cached))
                return cached;

            // client=gtx — публичный endpoint, без ключа
            var url =
                "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ru&dt=t&q="
                + Uri.EscapeDataString(source);

            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var translated = ParseGoogleTranslate(json);
            if (string.IsNullOrWhiteSpace(translated))
                translated = source;

            Cache[source] = translated;
            return translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return source;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string ParseGoogleTranslate(string json)
    {
        // Формат: [[["перевод","source",...],...], ...]
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return "";

            var sentences = root[0];
            if (sentences.ValueKind != JsonValueKind.Array)
                return "";

            var sb = new StringBuilder();
            foreach (var part in sentences.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0)
                {
                    var piece = part[0].GetString();
                    if (!string.IsNullOrEmpty(piece))
                        sb.Append(piece);
                }
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksMostlyCyrillic(string text)
    {
        var letters = 0;
        var cyr = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
                continue;
            letters++;
            if (ch is >= 'А' and <= 'я' or 'ё' or 'Ё')
                cyr++;
        }

        return letters > 0 && cyr * 2 >= letters;
    }
}
