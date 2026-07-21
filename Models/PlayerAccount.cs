using System;

namespace ZLauncher.Models;

/// <summary>
/// Тип аккаунта. Пока только «обычный» —
/// без входа в Microsoft/Mojang.
/// </summary>
public enum AccountType
{
    /// <summary>Обычный: только ник, UUID OfflinePlayer:name.</summary>
    Offline = 0
}

/// <summary>
/// Локальный аккаунт лаунчера (не требует авторизации).
/// </summary>
public sealed class PlayerAccount
{
    /// <summary>Стабильный id записи (не UUID Minecraft).</summary>
    public required string Id { get; init; }

    public required AccountType Type { get; init; }

    /// <summary>Имя игрока в игре (3–16, a-zA-Z0-9_).</summary>
    public required string Username { get; set; }

    /// <summary>UUID без дефисов (для offline — OfflinePlayer:name).</summary>
    public required string UuidCompact { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string TypeLabel => Type switch
    {
        AccountType.Offline => "Обычный",
        _ => Type.ToString()
    };

    public string TypeHint => Type switch
    {
        AccountType.Offline => "Без входа · обычный",
        _ => ""
    };

    public static PlayerAccount CreateOffline(string username, string uuidCompact) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Offline,
            Username = username,
            UuidCompact = uuidCompact.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow
        };
}
