using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZLauncher.Models;

namespace ZLauncher.Services;

public sealed class AppSettings
{
    public string? SelectedVersionId { get; set; }
    public string? SelectedVariantKey { get; set; }

    /// <summary>Аккаунты (обычные — без входа).</summary>
    public List<PlayerAccount> Accounts { get; set; } = new();

    /// <summary>Id выбранного аккаунта.</summary>
    public string? SelectedAccountId { get; set; }

    /// <summary>Устаревшее: ники (мигрируются в Accounts).</summary>
    public List<string> Nicknames { get; set; } = new();

    /// <summary>Устаревшее: активный ник.</summary>
    public string? SelectedNickname { get; set; }

    // ── Настройки запуска ──

    /// <summary>Максимум RAM для JVM, МБ (если не AutoMemory).</summary>
    public int MemoryMaxMb { get; set; } = 4096;

    /// <summary>Автоматически подбирать объём RAM под ПК.</summary>
    public bool AutoMemory { get; set; } = true;

    /// <summary>Доп. аргументы JVM (например -XX:+UseG1GC).</summary>
    public string JvmArguments { get; set; } = "";

    /// <summary>Ширина окна игры.</summary>
    public int WindowWidth { get; set; } = 1280;

    /// <summary>Высота окна игры.</summary>
    public int WindowHeight { get; set; } = 720;

    /// <summary>Запуск в полноэкранном режиме.</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Сворачивать лаунчер после запуска игры.</summary>
    public bool MinimizeOnLaunch { get; set; }

    /// <summary>Закрывать лаунчер после запуска игры.</summary>
    public bool CloseOnLaunch { get; set; }

    /// <summary>Сразу запускать игру после успешной установки.</summary>
    public bool LaunchAfterInstall { get; set; }
}

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public AppSettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZLauncher");

        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new AppSettings();

            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            MigrateNicknamesToAccounts(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Старые settings хранили только строки-ники → обычные аккаунты.
    /// </summary>
    private static void MigrateNicknamesToAccounts(AppSettings settings)
    {
        if (settings.Accounts.Count > 0)
            return;

        foreach (var nick in settings.Nicknames
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var uuid = SkinService.OfflineUuidCompact(nick);
            settings.Accounts.Add(PlayerAccount.CreateOffline(nick, uuid));
        }

        if (settings.Accounts.Count == 0)
            return;

        if (!string.IsNullOrWhiteSpace(settings.SelectedNickname))
        {
            var match = settings.Accounts.FirstOrDefault(a =>
                string.Equals(a.Username, settings.SelectedNickname, StringComparison.OrdinalIgnoreCase));
            settings.SelectedAccountId = match?.Id ?? settings.Accounts[0].Id;
        }
        else
        {
            settings.SelectedAccountId = settings.Accounts[0].Id;
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Не мешаем работе лаунчера, если диск недоступен
        }
    }
}
