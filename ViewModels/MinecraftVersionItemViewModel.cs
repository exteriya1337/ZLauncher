using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZLauncher.Models;
using ZLauncher.Services;

namespace ZLauncher.ViewModels;

public partial class VariantRowViewModel : ViewModelBase
{
    public VersionVariant Model { get; }

    public string DisplayName => Model.DisplayName;
    public string KindLabel => Model.KindLabel;
    public string Key => Model.Key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindColor))]
    [NotifyPropertyChangedFor(nameof(NameColor))]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLast;

    /// <summary>Уже установлена — подпись Vanilla/Forge/… и имя оранжевые.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindColor))]
    [NotifyPropertyChangedFor(nameof(NameColor))]
    private bool _isInstalled;

    /// <summary>Цвет метки лоадера (Vanilla / OptiFine / …).</summary>
    public string KindColor => IsInstalled ? "#E67E22" : "#666666";

    /// <summary>Цвет названия; установленные чуть ярче.</summary>
    public string NameColor => IsInstalled
        ? "#E67E22"
        : IsSelected
            ? "#E67E22"
            : "#C8C8C8";

    public VariantRowViewModel(VersionVariant model) => Model = model;
}

public partial class MinecraftVersionItemViewModel : ViewModelBase
{
    private const double RowHeight = 30;
    private const double LoadingHeight = 32;
    private const double ErrorHeight = 24;
    private const double PanelPadding = 8;

    private readonly ModLoaderVariantService _loaderService;
    private readonly Func<VersionVariant, bool> _isInstalled;
    private readonly Func<string, bool> _hasAnyInstalled;
    private readonly Func<MinecraftVersionItemViewModel, Task> _onExpandRequested;
    private readonly Action<MinecraftVersionItemViewModel, VersionVariant> _onVariantSelected;
    private CancellationTokenSource? _loadCts;
    private bool _variantsLoaded;

    public MinecraftVersion Version { get; }

    public string Id => Version.Id;
    public string TypeLabel => Version.TypeLabel;
    public string DisplayName => Version.DisplayName;

    public ObservableCollection<VariantRowViewModel> Variants { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariantsPanelMaxHeight))]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Хотя бы одна подверсия/сборка этой версии установлена.</summary>
    [ObservableProperty]
    private bool _hasInstalledVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariantsPanelMaxHeight))]
    private bool _isLoadingVariants;

    [ObservableProperty]
    private VersionVariant? _selectedVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariantsPanelMaxHeight))]
    private string? _variantsError;

    /// <summary>Прозрачность блока подверсий (fade in/out).</summary>
    [ObservableProperty]
    private double _panelOpacity;

    /// <summary>
    /// Анимируемая высота панели: 0 ↔ расчёт по числу строк.
    /// Чуть с запасом, чтобы текст/метки не обрезались при transition.
    /// </summary>
    public double VariantsPanelMaxHeight
    {
        get
        {
            if (!IsExpanded)
                return 0;

            if (IsLoadingVariants && Variants.Count == 0)
                return LoadingHeight + PanelPadding;

            var h = PanelPadding;
            if (IsLoadingVariants)
                h += LoadingHeight * 0.5;
            if (!string.IsNullOrEmpty(VariantsError))
                h += ErrorHeight;
            h += Math.Max(Variants.Count, 0) * RowHeight;
            // небольшой запас под отступы строк
            h += 6;
            if (h < PanelPadding + 12)
                h = PanelPadding + 12;
            return h;
        }
    }

    /// <summary>Угол шеврона ▸ / ▾.</summary>
    public double ChevronAngle => IsExpanded ? 90 : 0;

    public MinecraftVersionItemViewModel(
        MinecraftVersion version,
        ModLoaderVariantService loaderService,
        Func<VersionVariant, bool> isInstalled,
        Func<string, bool> hasAnyInstalled,
        Func<MinecraftVersionItemViewModel, Task> onExpandRequested,
        Action<MinecraftVersionItemViewModel, VersionVariant> onVariantSelected)
    {
        Version = version;
        _loaderService = loaderService;
        _isInstalled = isInstalled;
        _hasAnyInstalled = hasAnyInstalled;
        _onExpandRequested = onExpandRequested;
        _onVariantSelected = onVariantSelected;

        Variants.CollectionChanged += OnVariantsCollectionChanged;
        RefreshInstalledFlags();
    }

    private void OnVariantsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(VariantsPanelMaxHeight));

    /// <summary>
    /// Обновить флаги установки (после Install / при открытии списка).
    /// <paramref name="installedGameIds"/> — снимок GameVersionId с диска (без N× скана).
    /// </summary>
    public void RefreshInstalledFlags(ISet<string>? installedGameIds = null)
    {
        foreach (var row in Variants)
            row.IsInstalled = _isInstalled(row.Model);

        var byMarker = installedGameIds is not null
            ? installedGameIds.Contains(Id)
            : _hasAnyInstalled(Id);

        // Маркеры + уже подгруженные строки (если лоадер не в recommended-списке)
        HasInstalledVariant = byMarker || Variants.Any(r => r.IsInstalled);
    }

    partial void OnSelectedVariantChanged(VersionVariant? value)
    {
        foreach (var row in Variants)
            row.IsSelected = value is not null && row.Key == value.Key;
    }

    [RelayCommand]
    private async Task ToggleExpandAsync()
    {
        if (IsExpanded)
        {
            Collapse();
            return;
        }

        await _onExpandRequested(this).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ChooseVariant(VariantRowViewModel? row)
    {
        if (row is null)
            return;

        SelectedVariant = row.Model;
        _onVariantSelected(this, row.Model);
    }

    public async Task ExpandAsync()
    {
        if (IsExpanded)
        {
            if (!_variantsLoaded && !IsLoadingVariants)
                await LoadVariantsAsync().ConfigureAwait(true);
            return;
        }

        // Сначала высота/opacity → Avalonia проиграет transition
        UsePanelAnimation = true;
        IsExpanded = true;
        PanelOpacity = 1;
        OnPropertyChanged(nameof(VariantsPanelMaxHeight));
        OnPropertyChanged(nameof(UsePanelAnimation));

        if (!_variantsLoaded && !IsLoadingVariants)
            await LoadVariantsAsync().ConfigureAwait(true);
        else
            OnPropertyChanged(nameof(VariantsPanelMaxHeight));
    }

    /// <summary>Плавное сворачивание (клик по уже открытой версии).</summary>
    public void Collapse()
    {
        if (!IsExpanded)
            return;

        UsePanelAnimation = true;
        PanelOpacity = 0;
        IsExpanded = false;
        OnPropertyChanged(nameof(VariantsPanelMaxHeight));
        OnPropertyChanged(nameof(UsePanelAnimation));
    }

    /// <summary>
    /// Мгновенное сворачивание при переключении на другую версию —
    /// без анимации, чтобы не дёргать скролл вместе с expand соседа.
    /// </summary>
    public void CollapseInstant()
    {
        if (!IsExpanded)
            return;

        UsePanelAnimation = false;
        PanelOpacity = 0;
        IsExpanded = false;
        OnPropertyChanged(nameof(VariantsPanelMaxHeight));
        OnPropertyChanged(nameof(UsePanelAnimation));
        // Вернём анимацию для следующих действий
        UsePanelAnimation = true;
        OnPropertyChanged(nameof(UsePanelAnimation));
    }

    /// <summary>Вкл. transitions MaxHeight/Opacity на панели (false = мгновенно).</summary>
    [ObservableProperty]
    private bool _usePanelAnimation = true;

    public void PreferVariantKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || Variants.Count == 0)
            return;

        var match = Variants.FirstOrDefault(v => v.Key == key);
        if (match is not null)
            SelectedVariant = match.Model;
    }

    private async Task LoadVariantsAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsLoadingVariants = true;
        VariantsError = null;
        OnPropertyChanged(nameof(VariantsPanelMaxHeight));

        try
        {
            IReadOnlyList<VersionVariant> variants;

            // Кастомная папка из versions/ — один пункт, без опроса лоадеров
            if (string.Equals(Version.Type, "custom", StringComparison.OrdinalIgnoreCase))
            {
                variants = new[] { GameInstallService.CreateCustomVariant(Version) };
            }
            else
            {
                // recommendedOnly по умолчанию (левый список)
                variants = await _loaderService
                    .GetVariantsAsync(Version.Id, token)
                    .ConfigureAwait(true);
            }

            if (token.IsCancellationRequested)
                return;

            Variants.Clear();
            for (var i = 0; i < variants.Count; i++)
            {
                var model = variants[i];
                Variants.Add(new VariantRowViewModel(model)
                {
                    IsLast = i == variants.Count - 1,
                    IsInstalled = _isInstalled(model)
                });
            }

            _variantsLoaded = true;
            SelectedVariant ??= Variants.FirstOrDefault()?.Model;
            OnSelectedVariantChanged(SelectedVariant);
            RefreshInstalledFlags();
            OnPropertyChanged(nameof(VariantsPanelMaxHeight));
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception)
        {
            VariantsError = "Не все лоадеры загрузились";
            if (Variants.Count == 0)
            {
                var vanilla = new VersionVariant
                {
                    Key = $"vanilla:{Version.Id}",
                    GameVersionId = Version.Id,
                    Kind = LoaderKind.Vanilla,
                    DisplayName = "Vanilla",
                    LoaderVersion = Version.Id
                };
                Variants.Add(new VariantRowViewModel(vanilla)
                {
                    IsLast = true,
                    IsInstalled = _isInstalled(vanilla)
                });
                _variantsLoaded = true;
                SelectedVariant ??= vanilla;
                OnSelectedVariantChanged(SelectedVariant);
            }

            RefreshInstalledFlags();
            OnPropertyChanged(nameof(VariantsPanelMaxHeight));
        }
        finally
        {
            IsLoadingVariants = false;
            OnPropertyChanged(nameof(VariantsPanelMaxHeight));
        }
    }
}
