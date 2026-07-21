using System;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ZLauncher.ViewModels;

namespace ZLauncher.Views;

public partial class SplashWindow : Window
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _spinCts;
    private bool _started;

    public SplashWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        StopSpinner();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
            return;
        _started = true;

        StartSpinner();

        if (DataContext is not SplashViewModel vm)
            DataContext = vm = new SplashViewModel();

        vm.LogLines.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { LogScroll?.ScrollToEnd(); } catch { /* ignore */ }
            });
        };

        _cts = new CancellationTokenSource();
        try
        {
            await vm.RunAsync(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (Exception ex)
        {
            try { vm.StatusText = "Ошибка: " + ex.Message; }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Плавное бесконечное вращение: анимация на Visual (Ellipse), не на RotateTransform.
    /// </summary>
    private void StartSpinner()
    {
        if (SpinRing is null)
            return;

        var rotate = SpinRing.RenderTransform as RotateTransform;
        if (rotate is null)
        {
            rotate = new RotateTransform();
            SpinRing.RenderTransform = rotate;
        }

        SpinRing.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        _spinCts?.Cancel();
        _spinCts = new CancellationTokenSource();
        var token = _spinCts.Token;

        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(0.85),
            IterationCount = IterationCount.Infinite,
            // Linear — равномерное вращение без рывков на стыке 0/360
            Easing = new LinearEasing(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 0d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 360d) }
                }
            }
        };

        // Важно: RunAsync(Visual), не Transform — иначе InvalidCastException
        _ = animation.RunAsync(SpinRing, token);
    }

    private void StopSpinner()
    {
        try { _spinCts?.Cancel(); } catch { /* ignore */ }
        _spinCts = null;
    }
}
