using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ZLauncher.Services;

/// <summary>
/// Лёгкий буфер логов установки. Очищается перед каждой новой установкой версии.
/// Потокобезопасен; ограничивает число строк, чтобы не раздувать память.
/// </summary>
public sealed class DebugLogService
{
    public static DebugLogService Instance { get; } = new();

    /// <summary>Максимум строк в буфере одной установки.</summary>
    public const int MaxLines = 12_000;

    private readonly object _lock = new();
    private readonly List<string> _lines = new(1024);
    private int _version;

    /// <summary>Инкрементируется при Clear / Log — UI может throttle-ить обновления.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Событие после Clear или Log (может приходить с фонового потока).</summary>
    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_lock)
                return _lines.Count;
        }
    }

    /// <summary>Полная очистка перед новой установкой.</summary>
    public void Clear(string? header = null)
    {
        lock (_lock)
        {
            _lines.Clear();
            if (!string.IsNullOrWhiteSpace(header))
                _lines.Add(FormatLine(header!));
        }

        Interlocked.Increment(ref _version);
        RaiseChanged();
    }

    public void Log(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        lock (_lock)
        {
            _lines.Add(FormatLine(message));
            TrimIfNeeded_NoLock();
        }

        Interlocked.Increment(ref _version);
        RaiseChanged();
    }

    public void Log(string category, string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        Log($"[{category}] {message}");
    }

    /// <summary>Снимок всех строк (копия).</summary>
    public string GetText()
    {
        lock (_lock)
        {
            if (_lines.Count == 0)
                return "";

            var sb = new StringBuilder(_lines.Count * 64);
            for (var i = 0; i < _lines.Count; i++)
            {
                if (i > 0)
                    sb.AppendLine();
                sb.Append(_lines[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>Копия строк для UI.</summary>
    public List<string> GetLinesCopy()
    {
        lock (_lock)
            return new List<string>(_lines);
    }

    private void TrimIfNeeded_NoLock()
    {
        if (_lines.Count <= MaxLines)
            return;

        // Срезаем старую четверть — дешевле, чем по одной
        var remove = MaxLines / 4;
        _lines.RemoveRange(0, remove);
        _lines.Insert(0, FormatLine($"… отброшено {remove} старых строк (лимит {MaxLines})"));
    }

    private static string FormatLine(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        return $"[{ts}] {message}";
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // UI-подписчики не должны ломать установку
        }
    }
}
