using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ZLauncher.Helpers;

/// <summary>
/// Плавная прокрутка колёсиком для ScrollViewer.
/// </summary>
public sealed class SmoothScrollHelper
{
    private readonly ScrollViewer _scrollViewer;
    private readonly double _stepPixels;
    private readonly double _lerpFactor;

    private double _targetOffsetY;
    private bool _isAnimating;
    private bool _targetInitialized;
    private bool _suppressWheel;
    private TopLevel? _topLevel;

    /// <summary>Доп. «воздух» снизу в пикселях при расчёте max offset (доскролл под обрезанный текст).</summary>
    private readonly double _bottomOverscroll;

    public SmoothScrollHelper(
        ScrollViewer scrollViewer,
        double stepPixels = 72,
        double lerpFactor = 0.22,
        double bottomOverscroll = 0)
    {
        _scrollViewer = scrollViewer;
        _stepPixels = stepPixels;
        _lerpFactor = lerpFactor;
        _bottomOverscroll = Math.Max(0, bottomOverscroll);

        scrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        scrollViewer.AttachedToVisualTree += (_, _) =>
            _topLevel = TopLevel.GetTopLevel(scrollViewer);
        scrollViewer.DetachedFromVisualTree += (_, _) =>
        {
            _isAnimating = false;
            _topLevel = null;
        };

        _topLevel = TopLevel.GetTopLevel(scrollViewer);
    }

    public ScrollViewer ScrollViewer => _scrollViewer;

    public double OffsetY
    {
        get => _scrollViewer.Offset.Y;
        set
        {
            var max = GetMaxOffsetY();
            var y = Math.Clamp(value, 0, max);
            _targetOffsetY = y;
            _targetInitialized = true;
            _isAnimating = false;

            if (Math.Abs(_scrollViewer.Offset.Y - y) > 0.01)
                _scrollViewer.Offset = new Vector(0, y);
        }
    }

    /// <summary>На время удержания якоря — не реагировать на колёсико.</summary>
    public void SetWheelSuppressed(bool suppressed) => _suppressWheel = suppressed;

    private double GetMaxOffsetY()
    {
        // Extent уже включает margin контента; bottomOverscroll — доп. запас при необходимости
        return Math.Max(
            0,
            _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height + _bottomOverscroll);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_suppressWheel)
        {
            e.Handled = true;
            return;
        }

        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        e.Handled = true;

        var max = GetMaxOffsetY();
        if (!_targetInitialized)
        {
            _targetOffsetY = _scrollViewer.Offset.Y;
            _targetInitialized = true;
        }

        _targetOffsetY = Math.Clamp(
            _targetOffsetY - e.Delta.Y * _stepPixels,
            0,
            max);

        if (!_isAnimating)
        {
            _isAnimating = true;
            _topLevel ??= TopLevel.GetTopLevel(_scrollViewer);
            _topLevel?.RequestAnimationFrame(OnFrame);
        }
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_isAnimating || _suppressWheel)
        {
            _isAnimating = false;
            return;
        }

        var max = GetMaxOffsetY();
        _targetOffsetY = Math.Clamp(_targetOffsetY, 0, max);

        var current = _scrollViewer.Offset.Y;
        var next = current + (_targetOffsetY - current) * _lerpFactor;

        if (Math.Abs(_targetOffsetY - next) < 0.4)
        {
            OffsetY = _targetOffsetY;
            _isAnimating = false;
            return;
        }

        _scrollViewer.Offset = new Vector(0, next);
        _topLevel?.RequestAnimationFrame(OnFrame);
    }
}
