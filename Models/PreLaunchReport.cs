using System.Collections.Generic;

namespace ZLauncher.Models;

/// <summary>Результат проверок перед запуском игры.</summary>
public sealed class PreLaunchReport
{
    public required string JavaPath { get; init; }
    public required string JavaComponent { get; init; }
    public required string JavaLabel { get; init; }

    /// <summary>Моды, временно убранные из mods/ как несовместимые.</summary>
    public IReadOnlyList<string> QuarantinedMods { get; init; } = [];

    /// <summary>Моды, возвращённые из quarantine (снова совместимы).</summary>
    public IReadOnlyList<string> RestoredMods { get; init; } = [];

    /// <summary>Автоматически установленные зависимости.</summary>
    public IReadOnlyList<string> InstalledDependencies { get; init; } = [];

    /// <summary>Предупреждения (не блокируют запуск).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                $"Java: {JavaLabel}"
            };
            if (RestoredMods.Count > 0)
                parts.Add($"возвращено модов: {RestoredMods.Count}");
            if (QuarantinedMods.Count > 0)
                parts.Add($"отключено несовместимых: {QuarantinedMods.Count}");
            if (InstalledDependencies.Count > 0)
                parts.Add($"установлено зависимостей: {InstalledDependencies.Count}");
            if (Warnings.Count > 0)
                parts.Add($"предупреждений: {Warnings.Count}");
            return string.Join(" · ", parts);
        }
    }
}
