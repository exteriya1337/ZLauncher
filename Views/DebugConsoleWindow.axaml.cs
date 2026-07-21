using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZLauncher.Services;

namespace ZLauncher.Views;

public partial class DebugConsoleWindow : Window
{
    private static DebugConsoleWindow? _instance;

    private readonly DebugLogService _log = DebugLogService.Instance;
    private readonly DispatcherTimer _refreshTimer;
    private int _lastVersion = -1;
    private bool _stickToBottom = true;

    public DebugConsoleWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _refreshTimer.Tick += OnRefreshTick;

        Opened += (_, _) =>
        {
            _log.Changed += OnLogChanged;
            ForceRefresh();
            _refreshTimer.Start();
        };

        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _log.Changed -= OnLogChanged;
            if (ReferenceEquals(_instance, this))
                _instance = null;
        };

        // Если пользователь прокрутил вверх — не дёргаем вниз
        if (LogScroll is not null)
        {
            LogScroll.ScrollChanged += (_, e) =>
            {
                if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
                    return;

                var extent = LogScroll.Extent.Height;
                var viewport = LogScroll.Viewport.Height;
                var offset = LogScroll.Offset.Y;
                _stickToBottom = offset + viewport >= extent - 24;
            };
        }
    }

    /// <summary>Одно окно консоли; повторный 3× клик активирует существующее.</summary>
    public static void ShowOrActivate(Window? owner)
    {
        if (_instance is not null)
        {
            try
            {
                if (_instance.WindowState == WindowState.Minimized)
                    _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                _instance.ForceRefresh();
                return;
            }
            catch
            {
                _instance = null;
            }
        }

        var win = new DebugConsoleWindow();
        _instance = win;
        if (owner is not null)
            win.Show(owner);
        else
            win.Show();
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        // Тяжёлую перерисовку делает таймер (throttle)
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        var v = _log.Version;
        if (v == _lastVersion)
            return;
        ForceRefresh();
    }

    private void ForceRefresh()
    {
        _lastVersion = _log.Version;
        var text = _log.GetText();
        var count = _log.Count;

        if (LogBox is not null && LogBox.Text != text)
            LogBox.Text = string.IsNullOrEmpty(text) ? "(пусто — начни установку версии)" : text;

        if (LineCountText is not null)
            LineCountText.Text = $"{count} строк";

        if (_stickToBottom && LogScroll is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    ScrollLogToEnd();
                }
                catch
                {
                    // ignore
                }
            }, DispatcherPriority.Background);
        }
    }

    private void ScrollLogToEnd()
    {
        if (LogScroll is null)
            return;

        // Extent может обновиться после layout
        var y = Math.Max(0, LogScroll.Extent.Height - LogScroll.Viewport.Height);
        LogScroll.Offset = new Vector(LogScroll.Offset.X, y);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = _log.GetText();
            var clipboard = Clipboard ?? TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _log.Clear("Лог очищен вручную");
        ForceRefresh();
    }

    private void OnScrollBottomClick(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        try
        {
            ScrollLogToEnd();
        }
        catch
        {
            // ignore
        }
    }
}
