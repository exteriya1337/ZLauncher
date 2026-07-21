using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZLauncher.Helpers;
using ZLauncher.ViewModels;

namespace ZLauncher.Views;

public partial class MainWindow : Window
{
    private SmoothScrollHelper? _versionListScroll;
    private SmoothScrollHelper? _nickListScroll;
    private SmoothScrollHelper? _subVersionScroll;
    private SmoothScrollHelper? _modsScroll;
    private SmoothScrollHelper? _resourcePacksScroll;
    private SmoothScrollHelper? _shadersScroll;
    private bool _filtersOpen;
    private bool _filtersAnimating;
    private TranslateTransform? _filterSlide;
    private bool _launchSettingsOpen;
    private bool _launchSettingsAnimating;
    private ScaleTransform? _launchSettingsScale;
    private TranslateTransform? _launchSettingsSlide;
    private TranslateTransform? _nickSlide;
    private TranslateTransform? _playBarSlide;
    private MainViewModel? _subscribedVm;
    private string? _playLabelText;
    private int _playLabelAnimToken;
    private string? _memoryLabelText;
    private int _memoryLabelAnimToken;
    private bool _memorySliderDragging;
    private const double PlayLetterShift = 18;
    private static readonly TimeSpan PlayLetterDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan PlayLetterStagger = TimeSpan.FromMilliseconds(22);
    private const double MemoryLetterShift = 12;
    private static readonly TimeSpan MemoryLetterDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MemoryLetterStagger = TimeSpan.FromMilliseconds(18);

    private Control? _anchorVisual;
    private double _anchorViewportY;

    private int _activeTabIndex = -1;
    private int _tabAnimGen;
    // Короче fade — меньше лагов на вкладках с фоном
    private static readonly TimeSpan TabFadeDuration = TimeSpan.FromMilliseconds(140);
    private const double TabSlidePx = 8;

    public MainWindow()
    {
        InitializeComponent();

        Win32Properties.SetWindowCornerPreference(
            this,
            Win32Properties.WindowCornerPreference.Round);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Opened += OnOpened;
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnWindowPropertyChanged;
    }

    private bool _wasMinimized;

    private void OnOpened(object? sender, EventArgs e)
    {
        CenterOnWorkingArea();
        // HWND уже есть — включаем системную DWM-анимацию свернуть/развернуть
        NativeWindowEffects.EnableSystemMinimizeAnimation(this);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        var oldState = e.OldValue is WindowState os ? os : WindowState.Normal;
        var newState = e.NewValue is WindowState ns ? ns : WindowState;

        if (newState == WindowState.Minimized)
        {
            _wasMinimized = true;
            return;
        }

        // Развернули с панели задач: системный restore + лёгкий fade/scale контента
        if (_wasMinimized && newState == WindowState.Normal)
        {
            _wasMinimized = false;
            NativeWindowEffects.TryRestoreAnimated(this);
            _ = PlayRestoreContentAnimationAsync();
        }
    }

    /// <summary>Доп. fade+scale при разворачивании (если DWM-анимация слабая).</summary>
    private async System.Threading.Tasks.Task PlayRestoreContentAnimationAsync()
    {
        if (RootChrome is null)
            return;

        try
        {
            ScaleTransform? scale = null;
            if (RootChrome.RenderTransform is TransformGroup g &&
                g.Children.Count > 0 &&
                g.Children[0] is ScaleTransform st)
            {
                scale = st;
            }

            RootChrome.Opacity = 0.55;
            if (scale is not null)
            {
                scale.ScaleX = 0.94;
                scale.ScaleY = 0.94;
            }

            await System.Threading.Tasks.Task.Delay(16);

            RootChrome.Opacity = 1;
            if (scale is not null)
            {
                // transitions on ScaleTransform need to be set
                if (scale.Transitions is null || scale.Transitions.Count == 0)
                {
                    scale.Transitions =
                    [
                        new DoubleTransition
                        {
                            Property = ScaleTransform.ScaleXProperty,
                            Duration = TimeSpan.FromMilliseconds(220),
                            Easing = new CubicEaseOut()
                        },
                        new DoubleTransition
                        {
                            Property = ScaleTransform.ScaleYProperty,
                            Duration = TimeSpan.FromMilliseconds(220),
                            Easing = new CubicEaseOut()
                        }
                    ];
                }

                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }
        catch
        {
            if (RootChrome is not null)
                RootChrome.Opacity = 1;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        CenterOnWorkingArea();
        NativeWindowEffects.EnableSystemMinimizeAnimation(this);

        // bottomOverscroll: чуть дальше вниз, если extent «короткий» из‑за layout
        // Достаточно воздуха: последняя плашка (скругления) не режется краем viewport
        _versionListScroll = new SmoothScrollHelper(VersionScroll, bottomOverscroll: 48);
        _nickListScroll = new SmoothScrollHelper(NickScroll, stepPixels: 48, lerpFactor: 0.22, bottomOverscroll: 36);
        if (this.FindControl<ScrollViewer>("SubVersionScroll") is { } subScroll)
            _subVersionScroll = new SmoothScrollHelper(subScroll, stepPixels: 56, lerpFactor: 0.22, bottomOverscroll: 12);
        if (this.FindControl<ScrollViewer>("ModsScroll") is { } modsScroll)
        {
            _modsScroll = new SmoothScrollHelper(modsScroll, stepPixels: 72, lerpFactor: 0.2, bottomOverscroll: 18);
            modsScroll.AddHandler(
                RequestBringIntoViewEvent,
                static (_, args) => args.Handled = true,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }
        if (this.FindControl<ScrollViewer>("ResourcePacksScroll") is { } rpScroll)
        {
            _resourcePacksScroll = new SmoothScrollHelper(rpScroll, stepPixels: 72, lerpFactor: 0.2, bottomOverscroll: 18);
            rpScroll.AddHandler(
                RequestBringIntoViewEvent,
                static (_, args) => args.Handled = true,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }
        if (this.FindControl<ScrollViewer>("ShadersScroll") is { } shScroll)
        {
            _shadersScroll = new SmoothScrollHelper(shScroll, stepPixels: 72, lerpFactor: 0.2, bottomOverscroll: 18);
            shScroll.AddHandler(
                RequestBringIntoViewEvent,
                static (_, args) => args.Handled = true,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }
        if (this.FindControl<ScrollViewer>("HomeNewsScroll") is { } newsScroll)
        {
            _ = new SmoothScrollHelper(newsScroll, stepPixels: 64, lerpFactor: 0.22, bottomOverscroll: 24);
            newsScroll.AddHandler(
                RequestBringIntoViewEvent,
                static (_, args) => args.Handled = true,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }

        // Не даём системе прокручивать к focused/selected
        VersionScroll.AddHandler(
            RequestBringIntoViewEvent,
            static (_, args) => args.Handled = true,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        VersionList.AddHandler(
            RequestBringIntoViewEvent,
            static (_, args) => args.Handled = true,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        NickScroll.AddHandler(
            RequestBringIntoViewEvent,
            static (_, args) => args.Handled = true,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        NickItemsControl.AddHandler(
            RequestBringIntoViewEvent,
            static (_, args) => args.Handled = true,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        SubscribeToViewModel(DataContext as MainViewModel);
        // Буквы на кнопке могли не создаться при раннем DataContextChanged
        if (DataContext is MainViewModel loadedVm)
        {
            _playLabelText = loadedVm.PrimaryActionText;
            SetPlayLabelInstant(_playLabelText ?? "ИГРАТЬ");
            _memoryLabelText = loadedVm.MemoryMaxLabel;
            SetMemoryLabelInstant(_memoryLabelText ?? "");
        }

        if (MemorySlider is not null)
        {
            MemorySlider.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => _memorySliderDragging = true,
                RoutingStrategies.Tunnel);
        }

        _filterSlide = FilterPanel.RenderTransform as TranslateTransform;
        if (_filterSlide is null)
        {
            _filterSlide = new TranslateTransform();
            FilterPanel.RenderTransform = _filterSlide;
        }

        FilterPanel.Opacity = 0;
        _filterSlide.Y = -10;
        FilterPopup.IsOpen = false;

        SetupLaunchSettingsTransforms();
        SetLaunchSettingsVisual(open: false);
        LaunchSettingsPopup.IsOpen = false;

        // Вкладки: подготовка transforms + стартовое состояние (без анимации)
        PrepareAllTabHosts();
        var startTab = DataContext is MainViewModel tvm ? tvm.MainTabIndex : 0;
        ApplyTabIndex(startTab, animate: false);

        // Входная анимация ников и нижней панели (только если на «Главной»)
        if (startTab == 0)
            _ = PlayEntranceAnimationsAsync();
        else
        {
            // Не-главная: контент уже показан ApplyTabIndex; nick/play скрыты
            NickPanel.Opacity = 0;
            PlayBar.Opacity = 0;
        }
    }

    private async System.Threading.Tasks.Task PlayEntranceAnimationsAsync()
    {
        _nickSlide = EnsureSlideTransform(NickPanel, TabSlidePx + 4);
        _playBarSlide = EnsureSlideTransform(PlayBar, TabSlidePx + 4);
        EnsureOpacityTransitions(NickPanel);
        EnsureOpacityTransitions(PlayBar);
        EnsureOpacityTransitions(TabHome);
        TabHome.RenderTransform = null;

        SetTabVisibleInstant(TabHome, show: true, hitTest: true);
        var t = TabHome.Transitions;
        TabHome.Transitions = null;
        TabHome.Opacity = 0;
        TabHome.Transitions = t;

        NickPanel.IsVisible = true;
        NickPanel.Opacity = 0;
        _nickSlide.Y = TabSlidePx + 4;
        NickPanel.IsHitTestVisible = true;

        PlayBar.IsVisible = true;
        PlayBar.Opacity = 0;
        _playBarSlide.Y = TabSlidePx + 4;
        PlayBar.IsHitTestVisible = true;

        await System.Threading.Tasks.Task.Delay(60);

        TabHome.Opacity = 1;
        NickPanel.Opacity = 1;
        _nickSlide.Y = 0;

        await System.Threading.Tasks.Task.Delay(50);

        PlayBar.Opacity = 1;
        _playBarSlide.Y = 0;
    }

    // ── Переключение вкладок (быстрый fade + IsVisible, без slide на тяжёлых фонах) ──

    private void PrepareAllTabHosts()
    {
        foreach (var host in EnumerateContentTabs())
        {
            EnsureOpacityTransitions(host);
            // Без TranslateTransform на вкладках с JPEG-фоном — сильно дешевле
            host.RenderTransform = null;
            host.Opacity = 0;
            host.IsHitTestVisible = false;
            host.IsVisible = false;
            host.ZIndex = 0;
        }

        EnsureOpacityTransitions(NickPanel);
        EnsureOpacityTransitions(PlayBar);
        // Nick/Play — лёгкий slide как раньше (маленькие панели)
        EnsureSlideTransform(NickPanel, TabSlidePx);
        EnsureSlideTransform(PlayBar, TabSlidePx);
    }

    private IEnumerable<Control> EnumerateContentTabs()
    {
        yield return TabHome;
        yield return TabVersions;
        yield return TabMods;
        yield return TabResourcePacks;
        yield return TabShaders;
        yield return TabErrors;
    }

    private void ApplyTabIndex(int index, bool animate)
    {
        if (index == _activeTabIndex && animate)
            return;

        var gen = ++_tabAnimGen;
        _activeTabIndex = index;

        var hosts = new (Control Host, bool Show, bool HitTest)[]
        {
            (TabHome, index == 0, true), // новости на главной кликабельны
            (NickPanel, index == 0, true),
            (PlayBar, index == 0, true),
            (TabVersions, index == 1, true),
            (TabMods, index == 2, true),
            (TabResourcePacks, index == 3, true),
            (TabShaders, index == 4, true),
            (TabErrors, index == 5, true),
        };

        if (!animate)
        {
            foreach (var (host, show, hit) in hosts)
                SetTabVisibleInstant(host, show, hit);
            return;
        }

        _ = AnimateTabSwitchAsync(gen, hosts);
    }

    private async System.Threading.Tasks.Task AnimateTabSwitchAsync(
        int gen,
        (Control Host, bool Show, bool HitTest)[] hosts)
    {
        // 1) Fade-out / hide outgoing
        foreach (var (host, show, _) in hosts)
        {
            if (show) continue;
            if (!host.IsVisible && host.Opacity < 0.01)
                continue;
            host.IsHitTestVisible = false;
            host.ZIndex = 0;
            host.Opacity = 0;
        }

        await System.Threading.Tasks.Task.Delay((int)TabFadeDuration.TotalMilliseconds + 8);
        if (gen != _tabAnimGen)
            return;

        // 2) Полностью убрать неактивные из дерева (не рендерятся → нет лагов)
        foreach (var (host, show, _) in hosts)
        {
            if (!show)
                host.IsVisible = false;
        }

        // 3) Показать входящие: IsVisible → Opacity 0 → кадр → Opacity 1
        foreach (var (host, show, hit) in hosts)
        {
            if (!show) continue;
            host.IsVisible = true;
            host.ZIndex = 2;
            // мгновенно 0 без анимации
            var t = host.Transitions;
            host.Transitions = null;
            host.Opacity = 0;
            host.Transitions = t ?? CreateOpacityTransitions();
            host.IsHitTestVisible = hit;
        }

        await System.Threading.Tasks.Task.Delay(16);
        if (gen != _tabAnimGen)
            return;

        foreach (var (host, show, hit) in hosts)
        {
            if (!show) continue;
            host.Opacity = 1;
            host.IsHitTestVisible = hit;
            // Nick чуть раньше Play
            if (ReferenceEquals(host, NickPanel))
            {
                await System.Threading.Tasks.Task.Delay(30);
                if (gen != _tabAnimGen) return;
            }
        }
    }

    private static void SetTabVisibleInstant(Control host, bool show, bool hitTest)
    {
        var t = host.Transitions;
        host.Transitions = null;
        host.IsVisible = show;
        host.Opacity = show ? 1 : 0;
        host.IsHitTestVisible = show && hitTest;
        host.ZIndex = show ? 2 : 0;
        host.Transitions = t ?? CreateOpacityTransitions();
    }

    private static Transitions CreateOpacityTransitions() =>
    [
        new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TabFadeDuration,
            Easing = new CubicEaseOut()
        }
    ];

    private static void EnsureOpacityTransitions(Control host)
    {
        // Всегда короткий fade (перезаписываем длинные из XAML на тяжёлых вкладках)
        host.Transitions = CreateOpacityTransitions();
    }

    private static TranslateTransform EnsureSlideTransform(Control host, double hiddenY)
    {
        if (host.RenderTransform is TranslateTransform existing)
        {
            existing.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TabFadeDuration,
                    Easing = new CubicEaseOut()
                }
            ];
            return existing;
        }

        var tr = new TranslateTransform { Y = hiddenY };
        tr.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TabFadeDuration,
                Easing = new CubicEaseOut()
            }
        ];
        host.RenderTransform = tr;
        return tr;
    }

    private void SetupLaunchSettingsTransforms()
    {
        if (LaunchSettingsPanel.RenderTransform is TransformGroup group &&
            group.Children.Count >= 2 &&
            group.Children[0] is ScaleTransform scale &&
            group.Children[1] is TranslateTransform slide)
        {
            _launchSettingsScale = scale;
            _launchSettingsSlide = slide;
            return;
        }

        _launchSettingsScale = new ScaleTransform(0.94, 0.94);
        _launchSettingsScale.Transitions =
        [
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(240),
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(240),
                Easing = new CubicEaseOut()
            }
        ];
        _launchSettingsSlide = new TranslateTransform(0, 10);
        _launchSettingsSlide.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(240),
                Easing = new CubicEaseOut()
            }
        ];
        LaunchSettingsPanel.RenderTransform = new TransformGroup
        {
            Children = { _launchSettingsScale, _launchSettingsSlide }
        };
    }

    private void SetLaunchSettingsVisual(bool open)
    {
        SetupLaunchSettingsTransforms();
        if (open)
        {
            LaunchSettingsPanel.Opacity = 1;
            _launchSettingsScale!.ScaleX = 1;
            _launchSettingsScale.ScaleY = 1;
            _launchSettingsSlide!.Y = 0;
        }
        else
        {
            LaunchSettingsPanel.Opacity = 0;
            _launchSettingsScale!.ScaleX = 0.94;
            _launchSettingsScale.ScaleY = 0.94;
            _launchSettingsSlide!.Y = 10;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToViewModel(DataContext as MainViewModel);
    }

    private void SubscribeToViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_subscribedVm, vm))
            return;

        if (_subscribedVm is not null)
        {
            _subscribedVm.VersionExpanding -= OnVersionExpanding;
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm.RequestMinimizeLauncher -= OnRequestMinimizeLauncher;
            _subscribedVm.RequestCloseLauncher -= OnRequestCloseLauncher;
            _subscribedVm.RequestCopyToClipboard -= OnRequestCopyToClipboard;
        }

        _subscribedVm = vm;

        if (_subscribedVm is not null)
        {
            _subscribedVm.VersionExpanding += OnVersionExpanding;
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedVm.RequestMinimizeLauncher += OnRequestMinimizeLauncher;
            _subscribedVm.RequestCloseLauncher += OnRequestCloseLauncher;
            _subscribedVm.RequestCopyToClipboard += OnRequestCopyToClipboard;
            // Стартовый текст без анимации
            _playLabelText = _subscribedVm.PrimaryActionText;
            SetPlayLabelInstant(_playLabelText ?? "ИГРАТЬ");
            _memoryLabelText = _subscribedVm.MemoryMaxLabel;
            SetMemoryLabelInstant(_memoryLabelText ?? "");
        }
    }

    private void OnRequestMinimizeLauncher(object? sender, EventArgs e)
    {
        MinimizeWithSystemAnimation();
    }

    private void OnRequestCloseLauncher(object? sender, EventArgs e)
    {
        Close();
    }

    /// <summary>Свернуть с системной анимацией Windows (как у обычных окон).</summary>
    private void MinimizeWithSystemAnimation()
    {
        if (!NativeWindowEffects.TryMinimizeAnimated(this))
            WindowState = WindowState.Minimized;
    }

    private async void OnRequestCopyToClipboard(object? sender, string text)
    {
        try
        {
            var clipboard = Clipboard;
            if (clipboard is null || string.IsNullOrEmpty(text))
                return;

            await clipboard.SetTextAsync(text);
        }
        catch
        {
            // ignore
        }
    }

    private void OnMemorySliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        FinishMemorySliderDrag();
    }

    private void OnMemorySliderLostFocus(object? sender, RoutedEventArgs e)
    {
        FinishMemorySliderDrag();
    }

    private void FinishMemorySliderDrag()
    {
        _memorySliderDragging = false;
        if (DataContext is not MainViewModel vm || vm.AutoMemory)
            return;

        // Плавный довод до шага 512 МБ
        if (MemorySlider is null)
        {
            vm.SnapMemoryMaxToStep();
            return;
        }

        var from = MemorySlider.Value;
        var to = (double)MainViewModel.SnapMemoryMb((int)Math.Round(from));
        to = Math.Clamp(to, MemorySlider.Minimum, MemorySlider.Maximum);
        if (Math.Abs(from - to) < 0.5)
        {
            vm.SnapMemoryMaxToStep();
            return;
        }

        _ = AnimateMemorySliderToAsync(to);
    }

    /// <summary>Плавный довод ползунка RAM к ближайшему шагу / целевому значению.</summary>
    private async System.Threading.Tasks.Task AnimateMemorySliderToAsync(double target)
    {
        if (MemorySlider is null || DataContext is not MainViewModel vm)
            return;

        var from = MemorySlider.Value;
        const int frames = 12;
        for (var i = 1; i <= frames; i++)
        {
            if (_memorySliderDragging)
                return;

            var t = i / (double)frames;
            // ease-out cubic
            var eased = 1 - Math.Pow(1 - t, 3);
            var v = from + (target - from) * eased;
            MemorySlider.Value = v;
            // синхронизируем VM без лишнего snap
            var asInt = (int)Math.Round(v);
            if (vm.MemoryMaxMb != asInt)
                vm.MemoryMaxMb = asInt;

            await System.Threading.Tasks.Task.Delay(16);
        }

        if (!_memorySliderDragging)
        {
            MemorySlider.Value = target;
            vm.MemoryMaxMb = (int)Math.Round(target);
            vm.SnapMemoryMaxToStep();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (e.PropertyName is nameof(MainViewModel.PrimaryActionText))
        {
            var next = vm.PrimaryActionText;
            if (string.Equals(next, _playLabelText, StringComparison.Ordinal))
                return;

            // Во время установки % меняется часто — без побуквенной анимации
            var wasProgress = _playLabelText is not null && _playLabelText.EndsWith('%');
            var isProgress = next.EndsWith('%');
            if (wasProgress && isProgress)
            {
                _playLabelText = next;
                SetPlayLabelInstant(next);
                return;
            }

            _ = AnimatePlayLabelLettersAsync(next);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.MemoryMaxLabel)
            or nameof(MainViewModel.MemoryMaxMb)
            or nameof(MainViewModel.AutoMemory))
        {
            var nextMem = vm.MemoryMaxLabel;
            if (string.Equals(nextMem, _memoryLabelText, StringComparison.Ordinal))
                return;

            // Во время drag — мгновенно (иначе анимация захлёбывается);
            // при отпущенном слайдере / авто — побуквенно как у ИГРАТЬ
            if (_memorySliderDragging)
                SetMemoryLabelInstant(nextMem);
            else
                _ = AnimateMemoryLabelLettersAsync(nextMem);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.MainTabIndex))
        {
            ApplyTabIndex(vm.MainTabIndex, animate: true);
        }

    }

    private void SetPlayLabelInstant(string text)
    {
        SetLetterLabelInstant(PlayLabelIn, PlayLabelOut, text, fontSize: 15, bold: true, Brushes.White);
    }

    private void SetMemoryLabelInstant(string text)
    {
        _memoryLabelText = text;
        SetLetterLabelInstant(
            MemoryLabelIn,
            MemoryLabelOut,
            text,
            fontSize: 12,
            bold: true,
            new SolidColorBrush(Color.Parse("#E67E22")));
    }

    private static void SetLetterLabelInstant(
        StackPanel? labelIn,
        StackPanel? labelOut,
        string text,
        double fontSize,
        bool bold,
        IBrush foreground)
    {
        if (labelIn is null || labelOut is null)
            return;

        try
        {
            labelOut.Children.Clear();
            labelIn.Children.Clear();
            foreach (var ch in text)
            {
                labelIn.Children.Add(CreateLetter(
                    ch.ToString(), y: 0, opacity: 1, fontSize, bold, foreground,
                    PlayLetterDuration));
            }
        }
        catch
        {
            // ignore layout races
        }
    }

    private static TextBlock CreatePlayLetter(string ch, double y, double opacity) =>
        CreateLetter(ch, y, opacity, fontSize: 15, bold: true, Brushes.White, PlayLetterDuration);

    private static TextBlock CreateMemoryLetter(string ch, double y, double opacity) =>
        CreateLetter(
            ch, y, opacity, fontSize: 12, bold: true,
            new SolidColorBrush(Color.Parse("#E67E22")),
            MemoryLetterDuration);

    private static TextBlock CreateLetter(
        string ch,
        double y,
        double opacity,
        double fontSize,
        bool bold,
        IBrush foreground,
        TimeSpan duration)
    {
        var tb = new TextBlock
        {
            Text = ch == " " ? "\u00A0" : ch,
            FontSize = fontSize,
            FontWeight = bold ? FontWeight.Bold : FontWeight.SemiBold,
            Foreground = foreground,
            Opacity = opacity,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var tr = new TranslateTransform(0, y);
        tr.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = duration,
                Easing = new CubicEaseOut()
            }
        ];
        tb.RenderTransform = tr;
        tb.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new CubicEaseOut()
            }
        ];
        return tb;
    }

    /// <summary>
    /// Старые буквы улетают вверх, новые прилетают снизу (со сдвигом по времени).
    /// Только UI-поток — без Task.Run (иначе гонки/краши Avalonia).
    /// </summary>
    private async System.Threading.Tasks.Task AnimatePlayLabelLettersAsync(string nextText)
    {
        await AnimateLetterLabelAsync(
            nextText,
            () => _playLabelText,
            t => _playLabelText = t,
            () => ++_playLabelAnimToken,
            () => _playLabelAnimToken,
            PlayLabelIn,
            PlayLabelOut,
            CreatePlayLetter,
            PlayLetterShift,
            PlayLetterStagger,
            PlayLetterDuration,
            SetPlayLabelInstant).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task AnimateMemoryLabelLettersAsync(string nextText)
    {
        await AnimateLetterLabelAsync(
            nextText,
            () => _memoryLabelText,
            t => _memoryLabelText = t,
            () => ++_memoryLabelAnimToken,
            () => _memoryLabelAnimToken,
            MemoryLabelIn,
            MemoryLabelOut,
            CreateMemoryLetter,
            MemoryLetterShift,
            MemoryLetterStagger,
            MemoryLetterDuration,
            SetMemoryLabelInstant).ConfigureAwait(true);
    }

    private static async System.Threading.Tasks.Task AnimateLetterLabelAsync(
        string nextText,
        Func<string?> getPrev,
        Action<string> setPrev,
        Func<int> bumpToken,
        Func<int> getToken,
        StackPanel? labelIn,
        StackPanel? labelOut,
        Func<string, double, double, TextBlock> createLetter,
        double letterShift,
        TimeSpan stagger,
        TimeSpan duration,
        Action<string> setInstant)
    {
        var token = bumpToken();
        var prev = getPrev() ?? "";
        setPrev(nextText);

        if (labelIn is null || labelOut is null)
            return;

        try
        {
            labelOut.Children.Clear();
            var outgoing = labelIn.Children.OfType<TextBlock>().ToList();
            labelIn.Children.Clear();

            if (outgoing.Count == 0 && prev.Length > 0)
            {
                foreach (var ch in prev)
                    outgoing.Add(createLetter(ch.ToString(), 0, 1));
            }

            foreach (var letter in outgoing)
                labelOut.Children.Add(letter);

            var incoming = new List<TextBlock>();
            foreach (var ch in nextText)
            {
                var letter = createLetter(ch.ToString(), letterShift, 0);
                labelIn.Children.Add(letter);
                incoming.Add(letter);
            }

            await System.Threading.Tasks.Task.Delay(16);
            if (token != getToken())
                return;

            var maxCount = Math.Max(outgoing.Count, incoming.Count);
            for (var step = 0; step < maxCount; step++)
            {
                if (token != getToken())
                    return;

                if (step < outgoing.Count)
                {
                    var letter = outgoing[step];
                    if (letter.RenderTransform is TranslateTransform tr)
                        tr.Y = -letterShift;
                    letter.Opacity = 0;
                }

                if (step < incoming.Count)
                {
                    var letter = incoming[step];
                    if (letter.RenderTransform is TranslateTransform tr)
                        tr.Y = 0;
                    letter.Opacity = 1;
                }

                await System.Threading.Tasks.Task.Delay(stagger);
            }

            await System.Threading.Tasks.Task.Delay(duration);
            if (token != getToken())
                return;

            labelOut.Children.Clear();
        }
        catch
        {
            if (token == getToken())
                setInstant(nextText);
        }
    }

    /// <summary>
    /// Один раз поправить скролл после мгновенного collapse соседей.
    /// Во время анимации MaxHeight якорь НЕ крутим — иначе дёрганье.
    /// </summary>
    private void OnVersionExpanding(object? sender, MinecraftVersionItemViewModel item)
    {
        _versionListScroll?.SetWheelSuppressed(true);

        _anchorVisual = VersionList.ContainerFromItem(item) as Control
                        ?? FindVersionItemVisual(item);
        _anchorViewportY = GetViewportY(_anchorVisual) ?? 0;

        // 1–2 кадра layout после CollapseInstant, затем стоп
        Dispatcher.UIThread.Post(() =>
        {
            RestoreAnchorOnce();
            Dispatcher.UIThread.Post(() =>
            {
                RestoreAnchorOnce();
                StopAnchoring();
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Render);
    }

    private void RestoreAnchorOnce()
    {
        if (_versionListScroll is null || _anchorVisual is null)
            return;

        if (DataContext is MainViewModel vm && vm.SelectedVersion is { } selected)
        {
            var resolved = VersionList.ContainerFromItem(selected) as Control
                           ?? FindVersionItemVisual(selected);
            if (resolved is not null)
                _anchorVisual = resolved;
        }

        var currentY = GetViewportY(_anchorVisual);
        if (currentY is null)
            return;

        var delta = currentY.Value - _anchorViewportY;
        if (Math.Abs(delta) < 0.5)
            return;

        // Прямой offset без smooth-lerp (якорь должен быть точным и разовым)
        var sv = _versionListScroll.ScrollViewer;
        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var next = Math.Clamp(sv.Offset.Y + delta, 0, max);
        sv.Offset = new Vector(0, next);
        _anchorViewportY = GetViewportY(_anchorVisual) ?? _anchorViewportY;
    }

    private void StopAnchoring()
    {
        _anchorVisual = null;
        _versionListScroll?.SetWheelSuppressed(false);
    }

    private static double? GetViewportY(Control? visual)
    {
        if (visual is null)
            return null;

        // Позиция верхнего края строки относительно ScrollViewer (viewport)
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null)
            return null;

        // Ищем ScrollViewer-предка
        ScrollViewer? sv = null;
        for (var v = visual as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is ScrollViewer scroll)
            {
                sv = scroll;
                break;
            }
        }

        if (sv is null)
            return null;

        var p = visual.TranslatePoint(new Point(0, 0), sv);
        return p?.Y;
    }

    private Control? FindVersionItemVisual(MinecraftVersionItemViewModel item)
    {
        // Fallback: ищем DockPanel с DataContext == item
        foreach (var descendant in VersionList.GetVisualDescendants().OfType<Control>())
        {
            if (ReferenceEquals(descendant.DataContext, item) && descendant is DockPanel)
                return descendant;
        }

        return null;
    }

    private void CenterOnWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scaling = DesktopScaling > 0 ? DesktopScaling : 1.0;

        var widthPx = (int)Math.Round(ClientSize.Width * scaling);
        var heightPx = (int)Math.Round(ClientSize.Height * scaling);

        if (widthPx <= 0)
            widthPx = (int)Math.Round(Width * scaling);
        if (heightPx <= 0)
            heightPx = (int)Math.Round(Height * scaling);

        var x = area.X + (area.Width - widthPx) / 2;
        var y = area.Y + (area.Height - heightPx) / 2;

        Position = new PixelPoint(x, y);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        MinimizeWithSystemAnimation();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>3× клик по «ZLauncher» — Debug-консоль логов установки.</summary>
    private void OnBrandTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Не тащим окно при кликах по бренду
        e.Handled = true;

        if (e.ClickCount >= 3)
            DebugConsoleWindow.ShowOrActivate(this);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        for (var visual = e.Source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is Button)
                return;

            // Клики по логотипу — для Debug-консоли, не для drag
            if (visual is TextBlock tb && tb.Name == "BrandTitle")
                return;
        }

        BeginMoveDrag(e);
    }

    private async void OnGearClick(object? sender, RoutedEventArgs e)
    {
        if (_filtersAnimating)
            return;

        if (_filtersOpen)
            await CloseFiltersAnimatedAsync();
        else
            await OpenFiltersAnimatedAsync();
    }

    private async System.Threading.Tasks.Task OpenFiltersAnimatedAsync()
    {
        _filtersAnimating = true;
        try
        {
            EnsureFilterSlide();

            FilterPanel.Opacity = 0;
            _filterSlide!.Y = -10;
            FilterPopup.IsOpen = true;
            _filtersOpen = true;

            await System.Threading.Tasks.Task.Delay(16);

            FilterPanel.Opacity = 1;
            _filterSlide.Y = 0;

            await System.Threading.Tasks.Task.Delay(220);
        }
        finally
        {
            _filtersAnimating = false;
        }
    }

    private async System.Threading.Tasks.Task CloseFiltersAnimatedAsync()
    {
        _filtersAnimating = true;
        try
        {
            EnsureFilterSlide();

            FilterPanel.Opacity = 0;
            _filterSlide!.Y = -8;

            await System.Threading.Tasks.Task.Delay(180);

            FilterPopup.IsOpen = false;
            _filtersOpen = false;
        }
        finally
        {
            _filtersAnimating = false;
        }
    }

    private void OnFilterPopupClosed(object? sender, EventArgs e)
    {
        _filtersOpen = false;
        EnsureFilterSlide();
        FilterPanel.Opacity = 0;
        if (_filterSlide is not null)
            _filterSlide.Y = -10;
    }

    private void EnsureFilterSlide()
    {
        if (_filterSlide is not null)
            return;

        _filterSlide = FilterPanel.RenderTransform as TranslateTransform;
        if (_filterSlide is null)
        {
            _filterSlide = new TranslateTransform();
            FilterPanel.RenderTransform = _filterSlide;
        }
    }

    private async void OnAddNicknameClick(object? sender, RoutedEventArgs e)
    {
        await TryAddNicknameWithFlyAsync();
    }

    private async void OnNickInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await TryAddNicknameWithFlyAsync();
        }
    }

    private async System.Threading.Tasks.Task TryAddNicknameWithFlyAsync()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var nick = vm.TryPrepareNicknameAdd(out var isNew);
        if (nick is null)
        {
            // лёгкий shake поля при ошибке
            await ShakeControlAsync(NickInputBox);
            return;
        }

        // Анимация «перелёта» из поля ввода в селект
        await PlayNickFlyAnimationAsync(nick);

        vm.CommitNicknameAdd(nick, isNew);

        // После добавления слегка «пульсануть» селект
        await PulseControlAsync(NickSelectButton);
    }

    private async System.Threading.Tasks.Task PlayNickFlyAnimationAsync(string nick)
    {
        // Старт: центр поля ввода, финиш: селект
        var start = NickInputBox.TranslatePoint(new Point(8, NickInputBox.Bounds.Height / 2), NickPanel);
        var end = NickSelectButton.TranslatePoint(new Point(12, NickSelectButton.Bounds.Height / 2), NickPanel);
        if (start is null || end is null)
            return;

        if (FlyingNick.RenderTransform is not TransformGroup group ||
            group.Children.Count < 2 ||
            group.Children[0] is not ScaleTransform scale ||
            group.Children[1] is not TranslateTransform slide)
        {
            scale = new ScaleTransform(1, 1);
            slide = new TranslateTransform();
            FlyingNick.RenderTransform = new TransformGroup { Children = { scale, slide } };
        }

        FlyingNick.Text = nick;
        FlyingNick.IsVisible = true;
        FlyingNick.Opacity = 1;
        slide.X = start.Value.X;
        slide.Y = start.Value.Y - 8;
        scale.ScaleX = 1;
        scale.ScaleY = 1;

        slide.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(320),
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(320),
                Easing = new CubicEaseOut()
            }
        ];
        scale.Transitions =
        [
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(320),
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(320),
                Easing = new CubicEaseOut()
            }
        ];
        FlyingNick.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseOut()
            }
        ];

        await System.Threading.Tasks.Task.Delay(16);

        slide.X = end.Value.X;
        slide.Y = end.Value.Y - 8;
        scale.ScaleX = 0.9;
        scale.ScaleY = 0.9;
        FlyingNick.Opacity = 0.15;

        await System.Threading.Tasks.Task.Delay(340);

        FlyingNick.IsVisible = false;
        FlyingNick.Opacity = 0;
        slide.Transitions = null;
        scale.Transitions = null;
        FlyingNick.Transitions = null;
    }

    private async System.Threading.Tasks.Task ShakeControlAsync(Control control)
    {
        var transform = control.RenderTransform as TranslateTransform
                        ?? new TranslateTransform();
        control.RenderTransform = transform;
        transform.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(50)
            }
        ];

        transform.X = -5;
        await System.Threading.Tasks.Task.Delay(50);
        transform.X = 5;
        await System.Threading.Tasks.Task.Delay(50);
        transform.X = -3;
        await System.Threading.Tasks.Task.Delay(50);
        transform.X = 0;
        await System.Threading.Tasks.Task.Delay(50);
        transform.Transitions = null;
    }

    private async System.Threading.Tasks.Task PulseControlAsync(Control control)
    {
        // Короткий scale pulse через Border внутри template сложно — лёгкий opacity
        var old = control.Opacity;
        control.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(120)
            }
        ];
        control.Opacity = 0.55;
        await System.Threading.Tasks.Task.Delay(120);
        control.Opacity = 1;
        await System.Threading.Tasks.Task.Delay(120);
        control.Transitions = null;
        control.Opacity = old;
    }

    private async void OnLaunchSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_launchSettingsAnimating)
            return;

        if (_launchSettingsOpen)
            await CloseLaunchSettingsAnimatedAsync();
        else
            await OpenLaunchSettingsAnimatedAsync();
    }

    private async System.Threading.Tasks.Task OpenLaunchSettingsAnimatedAsync()
    {
        _launchSettingsAnimating = true;
        try
        {
            SetLaunchSettingsVisual(open: false);
            LaunchSettingsPopup.IsOpen = true;
            _launchSettingsOpen = true;

            // Popup создаёт визуал — обновить подпись RAM
            if (DataContext is MainViewModel vmMem)
            {
                _memoryLabelText = null;
                SetMemoryLabelInstant(vmMem.MemoryMaxLabel);
            }

            await System.Threading.Tasks.Task.Delay(16);

            // scale + slide + fade
            SetLaunchSettingsVisual(open: true);

            await System.Threading.Tasks.Task.Delay(240);
        }
        finally
        {
            _launchSettingsAnimating = false;
        }
    }

    private async System.Threading.Tasks.Task CloseLaunchSettingsAnimatedAsync()
    {
        _launchSettingsAnimating = true;
        try
        {
            SetLaunchSettingsVisual(open: false);

            await System.Threading.Tasks.Task.Delay(180);

            LaunchSettingsPopup.IsOpen = false;
            _launchSettingsOpen = false;
        }
        finally
        {
            _launchSettingsAnimating = false;
        }
    }

    private void OnLaunchSettingsPopupClosed(object? sender, EventArgs e)
    {
        _launchSettingsOpen = false;
        SetLaunchSettingsVisual(open: false);
    }
}
