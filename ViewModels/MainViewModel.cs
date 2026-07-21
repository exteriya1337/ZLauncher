using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZLauncher.Models;
using ZLauncher.Services;

namespace ZLauncher.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly MojangVersionService _mojang = new();
    private readonly ModLoaderVariantService _loaders = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly GameInstallService _installs = new();
    private readonly JavaRuntimeService _java = new();
    private readonly SkinService _skins = new();
    private readonly ModrinthService _modrinth = new();
    private readonly TranslationService _translator = new();
    private readonly MinecraftNewsService _newsApi = new();
    private readonly LauncherUpdateService _updates = new();
    private readonly PreLaunchService _preLaunch;
    private readonly GameLaunchService _launcher;
    private UpdateCheckResult? _pendingUpdate;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _skinCts;
    private CancellationTokenSource? _newsCts;
    private CancellationTokenSource? _modsCts;
    private int _modsOffset;
    private int _modsTotalHits;
    private int _modsLoadGen;
    private CancellationTokenSource? _rpCts;
    private int _rpOffset;
    private int _rpTotalHits;
    private int _rpLoadGen;
    private CancellationTokenSource? _shCts;
    private int _shOffset;
    private int _shTotalHits;
    private int _shLoadGen;

    private const int FilterAnimationDelayMs = 200;

    private const double PlayButtonWidth = 180;

    private IReadOnlyList<MinecraftVersion> _allVersions = Array.Empty<MinecraftVersion>();
    private bool _isRestoringSelection;
    private bool _suppressSettingsSave;
    private CancellationTokenSource? _filterApplyCts;
    private CancellationTokenSource? _installCts;

    public ObservableCollection<MinecraftVersionItemViewModel> Versions { get; } = new();

    /// <summary>Аккаунты (обычные — без входа в Microsoft).</summary>
    public ObservableCollection<PlayerAccount> Accounts { get; } = new();

    /// <summary>Совместимость: имена аккаунтов (для старых биндингов).</summary>
    public IEnumerable<string> Nicknames => Accounts.Select(a => a.Username);

    /// <summary>
    /// Сигнал UI: раскрывается конкретная версия — удержать её на месте в viewport.
    /// </summary>
    public event EventHandler<MinecraftVersionItemViewModel>? VersionExpanding;

    [ObservableProperty]
    private MinecraftVersionItemViewModel? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionEnabled))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private VersionVariant? _selectedVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionEnabled))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private bool _isLoading;

    /// <summary>Видимость баннера «Загрузка…» (скрывается после fade-out).</summary>
    [ObservableProperty]
    private bool _loadingBannerVisible;

    /// <summary>Прозрачность баннера загрузки (для плавного скрытия).</summary>
    [ObservableProperty]
    private double _loadingOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorsPanelText))]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    /// <summary>
    /// Вкладка: 0 Главная · 1 Версии · 2 Моды · 3 Ресурспаки · 4 Шейдеры · 5 Ошибки.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeTab))]
    [NotifyPropertyChangedFor(nameof(IsVersionsTab))]
    [NotifyPropertyChangedFor(nameof(IsModsTab))]
    [NotifyPropertyChangedFor(nameof(IsResourcePacksTab))]
    [NotifyPropertyChangedFor(nameof(IsShadersTab))]
    [NotifyPropertyChangedFor(nameof(IsErrorsTab))]
    private int _mainTabIndex;

    public bool IsHomeTab => MainTabIndex == 0;
    public bool IsVersionsTab => MainTabIndex == 1;
    public bool IsModsTab => MainTabIndex == 2;
    public bool IsResourcePacksTab => MainTabIndex == 3;
    public bool IsShadersTab => MainTabIndex == 4;
    public bool IsErrorsTab => MainTabIndex == 5;

    /// <summary>Все загруженные карточки (до фильтра «установленные»).</summary>
    public ObservableCollection<ModCardViewModel> ModCards { get; } = new();

    /// <summary>Карточки с учётом фильтра Установленные / Не установленные.</summary>
    public ObservableCollection<ModCardViewModel> VisibleModCards { get; } = new();

    [ObservableProperty]
    private string _modsSearchQuery = "";

    [ObservableProperty]
    private bool _isLoadingMods;

    [ObservableProperty]
    private bool _isLoadingMoreMods;

    [ObservableProperty]
    private string? _modsError;

    [ObservableProperty]
    private string _modsStatusText = "";

    /// <summary>Фильтровать моды по выбранной версии Minecraft.</summary>
    [ObservableProperty]
    private bool _modsFilterBySelectedVersion = true;

    /// <summary>
    /// 0 = все, 1 = только установленные, 2 = только не установленные.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsModsFilterInstalled))]
    [NotifyPropertyChangedFor(nameof(IsModsFilterNotInstalled))]
    [NotifyPropertyChangedFor(nameof(CanLoadMoreMods))]
    private int _modsInstallFilter;

    public bool IsModsFilterAll => ModsInstallFilter == 0;
    public bool IsModsFilterInstalled => ModsInstallFilter == 1;
    public bool IsModsFilterNotInstalled => ModsInstallFilter == 2;

    public bool CanLoadMoreMods =>
        ModsInstallFilter != 1 &&
        !IsLoadingMods && !IsLoadingMoreMods &&
        _modsOffset < _modsTotalHits && ModCards.Count > 0;

    public string ModsVersionFilterHint =>
        ModsFilterBySelectedVersion && SelectedVersion is not null
            ? $"для {SelectedVersion.Id}"
            : "все версии MC";

    // ── Ресурспаки (Modrinth resourcepack) ──────────────────────────────

    public ObservableCollection<ModCardViewModel> ResourcePackCards { get; } = new();
    public ObservableCollection<ModCardViewModel> VisibleResourcePackCards { get; } = new();

    [ObservableProperty]
    private string _rpSearchQuery = "";

    [ObservableProperty]
    private bool _isLoadingResourcePacks;

    [ObservableProperty]
    private bool _isLoadingMoreResourcePacks;

    [ObservableProperty]
    private string? _rpError;

    [ObservableProperty]
    private string _rpStatusText = "";

    [ObservableProperty]
    private bool _rpFilterBySelectedVersion = true;

    /// <summary>0 = все, 1 = установленные, 2 = не установленные.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRpFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsRpFilterInstalled))]
    [NotifyPropertyChangedFor(nameof(IsRpFilterNotInstalled))]
    [NotifyPropertyChangedFor(nameof(CanLoadMoreResourcePacks))]
    private int _rpInstallFilter;

    public bool IsRpFilterAll => RpInstallFilter == 0;
    public bool IsRpFilterInstalled => RpInstallFilter == 1;
    public bool IsRpFilterNotInstalled => RpInstallFilter == 2;

    public bool CanLoadMoreResourcePacks =>
        RpInstallFilter != 1 &&
        !IsLoadingResourcePacks && !IsLoadingMoreResourcePacks &&
        _rpOffset < _rpTotalHits && ResourcePackCards.Count > 0;

    public string RpVersionFilterHint =>
        RpFilterBySelectedVersion && SelectedVersion is not null
            ? $"для {SelectedVersion.Id}"
            : "все версии MC";

    // ── Шейдеры (Modrinth shader) ───────────────────────────────────────

    public ObservableCollection<ModCardViewModel> ShaderCards { get; } = new();
    public ObservableCollection<ModCardViewModel> VisibleShaderCards { get; } = new();

    [ObservableProperty]
    private string _shSearchQuery = "";

    [ObservableProperty]
    private bool _isLoadingShaders;

    [ObservableProperty]
    private bool _isLoadingMoreShaders;

    [ObservableProperty]
    private string? _shError;

    [ObservableProperty]
    private string _shStatusText = "";

    [ObservableProperty]
    private bool _shFilterBySelectedVersion = true;

    /// <summary>0 = все, 1 = установленные, 2 = не установленные.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsShFilterInstalled))]
    [NotifyPropertyChangedFor(nameof(IsShFilterNotInstalled))]
    [NotifyPropertyChangedFor(nameof(CanLoadMoreShaders))]
    private int _shInstallFilter;

    public bool IsShFilterAll => ShInstallFilter == 0;
    public bool IsShFilterInstalled => ShInstallFilter == 1;
    public bool IsShFilterNotInstalled => ShInstallFilter == 2;

    public bool CanLoadMoreShaders =>
        ShInstallFilter != 1 &&
        !IsLoadingShaders && !IsLoadingMoreShaders &&
        _shOffset < _shTotalHits && ShaderCards.Count > 0;

    public string ShVersionFilterHint =>
        ShFilterBySelectedVersion && SelectedVersion is not null
            ? $"для {SelectedVersion.Id}"
            : "все версии MC";

    /// <summary>
    /// Полный список подверсий выбранной MC-версии (вкладка «Версии»).
    /// Слева — только рекомендуемые; здесь — все Forge/Fabric/…
    /// </summary>
    public ObservableCollection<VariantRowViewModel> FullVariants { get; } = new();

    [ObservableProperty]
    private bool _isLoadingFullVariants;

    /// <summary>Фоновая догрузка полного списка (список уже виден).</summary>
    [ObservableProperty]
    private bool _isRefreshingFullVariants;

    [ObservableProperty]
    private string? _fullVariantsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyErrorButtonText))]
    private bool _errorCopiedFeedback;

    public string CopyErrorButtonText => ErrorCopiedFeedback ? "Скопировано" : "Копировать";

    private CancellationTokenSource? _fullVariantsCts;
    private string? _fullVariantsLoadedForId;
    private int _fullVariantsLoadGen;

    /// <summary>Подпись выбранной сборки (вкладка «Версии»).</summary>
    public string SelectedBuildSummary
    {
        get
        {
            if (SelectedVariant is null || SelectedVersion is null)
                return "Версия не выбрана";

            if (SelectedVariant.Kind == LoaderKind.Vanilla)
                return $"{SelectedVariant.GameVersionId} · Vanilla";

            return $"{SelectedVariant.GameVersionId} · {SelectedVariant.DisplayName}";
        }
    }

    public string SelectedBuildHint
    {
        get
        {
            if (SelectedVariant is null)
                return "Выберите версию";
            return IsSelectedInstalled ? "Установлена" : "Не установлена";
        }
    }

    /// <summary>Текст для панели «Ошибки» (или заглушка, если всё чисто).</summary>
    public string ErrorsPanelText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
                return ErrorMessage!;
            if (IsInstalling)
                return "Идёт установка… ошибки появятся здесь, если что-то пойдёт не так.";
            return "Пока ошибок нет.\nСбои установки и запуска игры будут показаны здесь.";
        }
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>Релизы всегда включены (нельзя снять).</summary>
    public bool ShowReleases => true;

    [RelayCommand]
    private void SelectHomeTab()
    {
        MainTabIndex = 0;
        if (NewsCards.Count == 0 && !IsLoadingNews)
            _ = LoadNewsAsync();
    }

    // ── Новости Minecraft (главная) ─────────────────────────────────

    public ObservableCollection<NewsCardViewModel> NewsCards { get; } = new();

    [ObservableProperty]
    private bool _isLoadingNews;

    [ObservableProperty]
    private string? _newsError;

    [ObservableProperty]
    private string _newsStatusText = "";

    [RelayCommand]
    private Task RefreshNewsAsync() => LoadNewsAsync(force: true);

    private async Task LoadNewsAsync(bool force = false)
    {
        if (IsLoadingNews)
            return;
        if (!force && NewsCards.Count > 0)
            return;

        _newsCts?.Cancel();
        _newsCts = new CancellationTokenSource();
        var token = _newsCts.Token;

        IsLoadingNews = true;
        NewsError = null;
        NewsStatusText = "Загрузка новостей Mojang…";

        try
        {
            // Одна свежая новость — крупная карточка, без сетки
            var items = await _newsApi.GetNewsAsync(limit: 1, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
                return;

            ClearNewsCards();
            foreach (var item in items)
            {
                var card = new NewsCardViewModel(item, _newsApi, _translator);
                NewsCards.Add(card);
                card.StartEnrich(token);
            }

            NewsStatusText = NewsCards.Count == 0
                ? "Новостей пока нет"
                : "Свежая новость от Mojang";
            NewsError = null;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            NewsError = $"Не удалось загрузить новости: {ex.Message}";
            NewsStatusText = "Ошибка загрузки";
        }
        finally
        {
            IsLoadingNews = false;
        }
    }

    private void ClearNewsCards()
    {
        foreach (var c in NewsCards)
            c.DisposeResources();
        NewsCards.Clear();
    }

    [RelayCommand]
    private void SelectVersionsTab()
    {
        MainTabIndex = 1;
        // Не блокируем UI: мгновенный seed + фоновая догрузка
        _ = LoadFullVariantsAsync();
    }

    [RelayCommand]
    private void SelectModsTab()
    {
        MainTabIndex = 2;
        if (ModCards.Count == 0 && !IsLoadingMods)
            _ = LoadModsInternalAsync(reset: true);
    }

    [RelayCommand]
    private void SelectResourcePacksTab()
    {
        MainTabIndex = 3;
        if (ResourcePackCards.Count == 0 && !IsLoadingResourcePacks)
            _ = LoadResourcePacksInternalAsync(reset: true);
    }

    [RelayCommand]
    private void SelectShadersTab()
    {
        MainTabIndex = 4;
        if (ShaderCards.Count == 0 && !IsLoadingShaders)
            _ = LoadShadersInternalAsync(reset: true);
    }

    [RelayCommand]
    private Task SearchModsAsync() => LoadModsInternalAsync(reset: true);

    [RelayCommand]
    private Task SearchResourcePacksAsync() => LoadResourcePacksInternalAsync(reset: true);

    [RelayCommand]
    private Task SearchShadersAsync() => LoadShadersInternalAsync(reset: true);

    [RelayCommand]
    private Task LoadMoreModsAsync()
    {
        if (!CanLoadMoreMods)
            return Task.CompletedTask;
        return LoadModsInternalAsync(reset: false);
    }

    [RelayCommand]
    private Task LoadMoreResourcePacksAsync()
    {
        if (!CanLoadMoreResourcePacks)
            return Task.CompletedTask;
        return LoadResourcePacksInternalAsync(reset: false);
    }

    [RelayCommand]
    private Task LoadMoreShadersAsync()
    {
        if (!CanLoadMoreShaders)
            return Task.CompletedTask;
        return LoadShadersInternalAsync(reset: false);
    }

    [RelayCommand]
    private void SetModsInstallFilter(string? mode)
    {
        ModsInstallFilter = mode?.ToLowerInvariant() switch
        {
            "installed" or "1" => 1,
            "notinstalled" or "not_installed" or "2" => 2,
            _ => 0
        };
    }

    [RelayCommand]
    private void SetRpInstallFilter(string? mode)
    {
        RpInstallFilter = mode?.ToLowerInvariant() switch
        {
            "installed" or "1" => 1,
            "notinstalled" or "not_installed" or "2" => 2,
            _ => 0
        };
    }

    [RelayCommand]
    private void SetShInstallFilter(string? mode)
    {
        ShInstallFilter = mode?.ToLowerInvariant() switch
        {
            "installed" or "1" => 1,
            "notinstalled" or "not_installed" or "2" => 2,
            _ => 0
        };
    }

    partial void OnModsInstallFilterChanged(int value) =>
        _ = ApplyModsInstallFilterAsync();

    partial void OnRpInstallFilterChanged(int value) =>
        _ = ApplyRpInstallFilterAsync();

    partial void OnShInstallFilterChanged(int value) =>
        _ = ApplyShInstallFilterAsync();

    private async Task ApplyModsInstallFilterAsync()
    {
        if (ModsInstallFilter == 1)
            await EnsureInstalledModsListedAsync().ConfigureAwait(true);

        RebuildVisibleModCards();
        UpdateModsFilterStatusText();
        OnPropertyChanged(nameof(CanLoadMoreMods));
    }

    private async Task LoadModsInternalAsync(bool reset)
    {
        // Режим «Установленные» — только локальный список, без поиска Modrinth
        if (ModsInstallFilter == 1 && reset)
        {
            _modsCts?.Cancel();
            _modsCts = new CancellationTokenSource();
            ClearModCards();
            IsLoadingMods = true;
            IsLoadingMoreMods = false;
            ModsError = null;
            ModsStatusText = "Загрузка установленных…";
            try
            {
                await EnsureInstalledModsListedAsync().ConfigureAwait(true);
                RebuildVisibleModCards();
                UpdateModsFilterStatusText();
            }
            finally
            {
                IsLoadingMods = false;
                OnPropertyChanged(nameof(CanLoadMoreMods));
            }
            return;
        }

        if (reset)
        {
            _modsCts?.Cancel();
            _modsCts = new CancellationTokenSource();
            _modsOffset = 0;
            _modsTotalHits = 0;
            ClearModCards();
            IsLoadingMods = true;
            IsLoadingMoreMods = false;
            ModsError = null;
            ModsStatusText = "Загрузка с Modrinth…";
            OnPropertyChanged(nameof(CanLoadMoreMods));
        }
        else
        {
            if (IsLoadingMods || IsLoadingMoreMods)
                return;
            IsLoadingMoreMods = true;
            OnPropertyChanged(nameof(CanLoadMoreMods));
        }

        var token = _modsCts?.Token ?? CancellationToken.None;
        var gen = ++_modsLoadGen;
        var query = ModsSearchQuery;
        var gameVersion = ModsFilterBySelectedVersion ? SelectedVersion?.Id : null;
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        const int pageSize = 24;

        try
        {
            var result = await _modrinth
                .SearchModsAsync(query, gameVersion, index, _modsOffset, pageSize, token)
                .ConfigureAwait(true);

            if (gen != _modsLoadGen || token.IsCancellationRequested)
                return;

            _modsTotalHits = result.TotalHits;
            _modsOffset = result.NextOffset;

            foreach (var mod in result.Mods)
            {
                if (ModCards.Any(c =>
                        string.Equals(c.Model.ProjectId, mod.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var card = new ModCardViewModel(
                    mod,
                    _modrinth,
                    _translator,
                    ResolveModInstallContext);
                AttachModCard(card);
                ModCards.Add(card);
                card.StartEnrich(token);
            }

            RebuildVisibleModCards();
            UpdateModsFilterStatusText();
            ModsError = null;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            if (gen != _modsLoadGen)
                return;
            ModsError = $"Не удалось загрузить моды: {ex.Message}";
            if (ModCards.Count == 0)
                ModsStatusText = "Ошибка загрузки";
        }
        finally
        {
            if (gen == _modsLoadGen)
            {
                IsLoadingMods = false;
                IsLoadingMoreMods = false;
                OnPropertyChanged(nameof(CanLoadMoreMods));
                OnPropertyChanged(nameof(ModsVersionFilterHint));
            }
        }
    }

    private void AttachModCard(ModCardViewModel card)
    {
        card.PropertyChanged += OnModCardPropertyChanged;
    }

    private void OnModCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModCardViewModel.IsInstalled) && ModsInstallFilter != 0)
            Dispatcher.UIThread.Post(RebuildVisibleModCards);
    }

    private void RebuildVisibleModCards()
    {
        VisibleModCards.Clear();
        IEnumerable<ModCardViewModel> q = ModCards;
        if (ModsInstallFilter == 1)
            q = ModCards.Where(c => c.IsInstalled);
        else if (ModsInstallFilter == 2)
            q = ModCards.Where(c => !c.IsInstalled);

        foreach (var c in q)
            VisibleModCards.Add(c);
    }

    private void UpdateModsFilterStatusText()
    {
        var filterLabel = ModsInstallFilter switch
        {
            1 => "установленные",
            2 => "не установленные",
            _ => "все"
        };

        if (ModsInstallFilter == 1)
        {
            ModsStatusText = VisibleModCards.Count == 0
                ? "Нет установленных модов в папке mods"
                : $"Установлено: {VisibleModCards.Count}";
            return;
        }

        if (_modsTotalHits == 0 && ModCards.Count == 0)
        {
            ModsStatusText = "Ничего не найдено";
            return;
        }

        ModsStatusText =
            $"Показано {VisibleModCards.Count} ({filterLabel}) · " +
            $"всего в выдаче {ModCards.Count}" +
            (_modsTotalHits > 0 ? $" / {_modsTotalHits:N0}" : "") +
            $" · {ModsVersionFilterHint}";
    }

    /// <summary>Добавить в список локально установленные моды (папка mods + индекс).</summary>
    private async Task EnsureInstalledModsListedAsync()
    {
        try
        {
            var modsDir = ModrinthService.GetModsDirectory();
            if (!Directory.Exists(modsDir))
                return;

            var jars = Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly);
            if (jars.Length == 0)
                return;

            var hashes = new List<(string Path, string Sha1)>();
            foreach (var jar in jars)
            {
                try
                {
                    await using var fs = File.OpenRead(jar);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs))
                        .ToLowerInvariant();
                    hashes.Add((jar, hash));
                }
                catch
                {
                    // skip unreadable
                }
            }

            var byHash = await _modrinth
                .GetVersionsByHashesAsync(hashes.Select(h => h.Sha1))
                .ConfigureAwait(true);

            // Полные карточки проектов (название, иконка, описание) — не имя версии
            var projectIds = byHash.Values
                .Select(v => v.ProjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projects = await _modrinth
                .GetProjectsByIdsAsync(projectIds)
                .ConfigureAwait(true);

            var token = _modsCts?.Token ?? CancellationToken.None;

            foreach (var (path, sha1) in hashes)
            {
                var fileName = Path.GetFileName(path);
                ModrinthModInfo info;

                if (byHash.TryGetValue(sha1, out var ver) && !string.IsNullOrWhiteSpace(ver.ProjectId))
                {
                    if (ModCards.Any(c =>
                            string.Equals(c.Model.ProjectId, ver.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    {
                        var existing = ModCards.First(c =>
                            string.Equals(c.Model.ProjectId, ver.ProjectId, StringComparison.OrdinalIgnoreCase));
                        existing.IsInstalled = true;
                        continue;
                    }

                    info = BuildInstalledCardInfo(ver, projects, fileName, projectType: "mod");
                }
                else
                {
                    var title = Path.GetFileNameWithoutExtension(fileName);
                    if (ModCards.Any(c =>
                            string.Equals(c.Model.Title, title, StringComparison.OrdinalIgnoreCase) &&
                            c.IsInstalled))
                        continue;

                    info = new ModrinthModInfo
                    {
                        ProjectId = "local:" + fileName,
                        Slug = fileName,
                        Title = title,
                        Description = "Установлен локально (нет на Modrinth)",
                        Author = "локально",
                        Downloads = 0,
                        IconUrl = null,
                        ProjectType = "mod"
                    };
                }

                var card = new ModCardViewModel(info, _modrinth, _translator, ResolveModInstallContext)
                {
                    IsInstalled = true
                };
                AttachModCard(card);
                ModCards.Add(card);
                if (!info.ProjectId.StartsWith("local:", StringComparison.Ordinal))
                    card.StartEnrich(token);
            }
        }
        catch (Exception ex)
        {
            ModsError = $"Не удалось прочитать установленные моды: {ex.Message}";
        }
    }

    /// <summary>
    /// Карточка установленного файла: данные проекта с Modrinth + подпись установленной версии.
    /// </summary>
    private static ModrinthModInfo BuildInstalledCardInfo(
        ModrinthVersionInfo ver,
        IReadOnlyDictionary<string, ModrinthModInfo> projects,
        string fileName,
        string projectType)
    {
        projects.TryGetValue(ver.ProjectId, out var project);

        var title = project?.Title;
        if (string.IsNullOrWhiteSpace(title))
            title = !string.IsNullOrWhiteSpace(ver.Name) ? ver.Name : Path.GetFileNameWithoutExtension(fileName);

        var description = project?.Description;
        if (string.IsNullOrWhiteSpace(description))
            description = "Установлен локально";

        var versionHint = !string.IsNullOrWhiteSpace(ver.VersionNumber)
            ? ver.VersionNumber
            : ver.GameVersions.FirstOrDefault() ?? "";

        var categories = project?.Categories is { Count: > 0 } pc
            ? pc
            : (IReadOnlyList<string>)(ver.Loaders.Count > 0 ? ver.Loaders.ToList() : Array.Empty<string>());

        return new ModrinthModInfo
        {
            ProjectId = ver.ProjectId,
            Slug = project?.Slug ?? ver.ProjectId,
            Title = title!,
            Description = description!,
            Author = project?.Author ?? "—",
            Downloads = project?.Downloads ?? 0,
            Follows = project?.Follows ?? 0,
            IconUrl = project?.IconUrl,
            ProjectType = project?.ProjectType ?? projectType,
            Categories = categories,
            Versions = ver.GameVersions.Count > 0
                ? ver.GameVersions
                : project?.Versions ?? Array.Empty<string>(),
            LatestVersion = versionHint
        };
    }

    private void ClearModCards()
    {
        foreach (var card in ModCards)
        {
            card.PropertyChanged -= OnModCardPropertyChanged;
            card.DisposeResources();
        }
        ModCards.Clear();
        VisibleModCards.Clear();
        OnPropertyChanged(nameof(CanLoadMoreMods));
    }

    /// <summary>Открыть папку игры (%AppData%/ZLauncher/game).</summary>
    [RelayCommand]
    private void OpenGameFolder()
    {
        try
        {
            var dir = Path.Combine(_installs.GameRoot, "game");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось открыть папку игры:\n{ex.Message}";
        }
    }

    /// <summary>Открыть сайт ZLauncher в браузере.</summary>
    [RelayCommand]
    private void OpenWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.WebsiteUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось открыть сайт:\n{ex.Message}";
        }
    }

    /// <summary>Контекст установки мода: MC-версия + лоадер выбранной сборки.</summary>
    private (string? gameVersion, string? loader) ResolveModInstallContext()
    {
        var gameVersion = SelectedVersion?.Id ?? SelectedVariant?.GameVersionId;
        var loader = SelectedVariant?.Kind switch
        {
            LoaderKind.Fabric => "fabric",
            LoaderKind.Quilt => "quilt",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null
        };
        return (gameVersion, loader);
    }

    // ── Ресурспаки: загрузка / фильтры (зеркало модов) ───────────────────

    private async Task ApplyRpInstallFilterAsync()
    {
        if (RpInstallFilter == 1)
            await EnsureInstalledResourcePacksListedAsync().ConfigureAwait(true);

        RebuildVisibleResourcePackCards();
        UpdateRpFilterStatusText();
        OnPropertyChanged(nameof(CanLoadMoreResourcePacks));
    }

    private async Task LoadResourcePacksInternalAsync(bool reset)
    {
        if (RpInstallFilter == 1 && reset)
        {
            _rpCts?.Cancel();
            _rpCts = new CancellationTokenSource();
            ClearResourcePackCards();
            IsLoadingResourcePacks = true;
            IsLoadingMoreResourcePacks = false;
            RpError = null;
            RpStatusText = "Загрузка установленных…";
            try
            {
                await EnsureInstalledResourcePacksListedAsync().ConfigureAwait(true);
                RebuildVisibleResourcePackCards();
                UpdateRpFilterStatusText();
            }
            finally
            {
                IsLoadingResourcePacks = false;
                OnPropertyChanged(nameof(CanLoadMoreResourcePacks));
            }
            return;
        }

        if (reset)
        {
            _rpCts?.Cancel();
            _rpCts = new CancellationTokenSource();
            _rpOffset = 0;
            _rpTotalHits = 0;
            ClearResourcePackCards();
            IsLoadingResourcePacks = true;
            IsLoadingMoreResourcePacks = false;
            RpError = null;
            RpStatusText = "Загрузка с Modrinth…";
            OnPropertyChanged(nameof(CanLoadMoreResourcePacks));
        }
        else
        {
            if (IsLoadingResourcePacks || IsLoadingMoreResourcePacks)
                return;
            IsLoadingMoreResourcePacks = true;
            OnPropertyChanged(nameof(CanLoadMoreResourcePacks));
        }

        var token = _rpCts?.Token ?? CancellationToken.None;
        var gen = ++_rpLoadGen;
        var query = RpSearchQuery;
        var gameVersion = RpFilterBySelectedVersion ? SelectedVersion?.Id : null;
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        const int pageSize = 24;

        try
        {
            var result = await _modrinth
                .SearchResourcePacksAsync(query, gameVersion, index, _rpOffset, pageSize, token)
                .ConfigureAwait(true);

            if (gen != _rpLoadGen || token.IsCancellationRequested)
                return;

            _rpTotalHits = result.TotalHits;
            _rpOffset = result.NextOffset;

            foreach (var pack in result.Mods)
            {
                if (ResourcePackCards.Any(c =>
                        string.Equals(c.Model.ProjectId, pack.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var card = new ModCardViewModel(
                    pack,
                    _modrinth,
                    _translator,
                    ResolveModInstallContext);
                AttachResourcePackCard(card);
                ResourcePackCards.Add(card);
                card.StartEnrich(token);
            }

            RebuildVisibleResourcePackCards();
            UpdateRpFilterStatusText();
            RpError = null;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            if (gen != _rpLoadGen)
                return;
            RpError = $"Не удалось загрузить ресурспаки: {ex.Message}";
            if (ResourcePackCards.Count == 0)
                RpStatusText = "Ошибка загрузки";
        }
        finally
        {
            if (gen == _rpLoadGen)
            {
                IsLoadingResourcePacks = false;
                IsLoadingMoreResourcePacks = false;
                OnPropertyChanged(nameof(CanLoadMoreResourcePacks));
                OnPropertyChanged(nameof(RpVersionFilterHint));
            }
        }
    }

    private void AttachResourcePackCard(ModCardViewModel card)
    {
        card.PropertyChanged += OnResourcePackCardPropertyChanged;
    }

    private void OnResourcePackCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModCardViewModel.IsInstalled) && RpInstallFilter != 0)
            Dispatcher.UIThread.Post(RebuildVisibleResourcePackCards);
    }

    private void RebuildVisibleResourcePackCards()
    {
        VisibleResourcePackCards.Clear();
        IEnumerable<ModCardViewModel> q = ResourcePackCards;
        if (RpInstallFilter == 1)
            q = ResourcePackCards.Where(c => c.IsInstalled);
        else if (RpInstallFilter == 2)
            q = ResourcePackCards.Where(c => !c.IsInstalled);

        foreach (var c in q)
            VisibleResourcePackCards.Add(c);
    }

    private void UpdateRpFilterStatusText()
    {
        var filterLabel = RpInstallFilter switch
        {
            1 => "установленные",
            2 => "не установленные",
            _ => "все"
        };

        if (RpInstallFilter == 1)
        {
            RpStatusText = VisibleResourcePackCards.Count == 0
                ? "Нет установленных ресурспаков в папке resourcepacks"
                : $"Установлено: {VisibleResourcePackCards.Count}";
            return;
        }

        if (_rpTotalHits == 0 && ResourcePackCards.Count == 0)
        {
            RpStatusText = "Ничего не найдено";
            return;
        }

        RpStatusText =
            $"Показано {VisibleResourcePackCards.Count} ({filterLabel}) · " +
            $"всего в выдаче {ResourcePackCards.Count}" +
            (_rpTotalHits > 0 ? $" / {_rpTotalHits:N0}" : "") +
            $" · {RpVersionFilterHint}";
    }

    private async Task EnsureInstalledResourcePacksListedAsync()
    {
        try
        {
            var packsDir = ModrinthService.GetResourcePacksDirectory();
            if (!Directory.Exists(packsDir))
                return;

            var files = Directory.GetFiles(packsDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
            if (files.Length == 0)
                return;

            var hashes = new List<(string Path, string Sha1)>();
            foreach (var file in files)
            {
                try
                {
                    await using var fs = File.OpenRead(file);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs))
                        .ToLowerInvariant();
                    hashes.Add((file, hash));
                }
                catch
                {
                    // skip
                }
            }

            var byHash = await _modrinth
                .GetVersionsByHashesAsync(hashes.Select(h => h.Sha1))
                .ConfigureAwait(true);

            var projectIds = byHash.Values
                .Select(v => v.ProjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projects = await _modrinth
                .GetProjectsByIdsAsync(projectIds)
                .ConfigureAwait(true);

            var token = _rpCts?.Token ?? CancellationToken.None;

            foreach (var (path, sha1) in hashes)
            {
                var fileName = Path.GetFileName(path);
                ModrinthModInfo info;

                if (byHash.TryGetValue(sha1, out var ver) && !string.IsNullOrWhiteSpace(ver.ProjectId))
                {
                    if (ResourcePackCards.Any(c =>
                            string.Equals(c.Model.ProjectId, ver.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    {
                        var existing = ResourcePackCards.First(c =>
                            string.Equals(c.Model.ProjectId, ver.ProjectId, StringComparison.OrdinalIgnoreCase));
                        existing.IsInstalled = true;
                        continue;
                    }

                    info = BuildInstalledCardInfo(ver, projects, fileName, projectType: "resourcepack");
                }
                else
                {
                    var title = Path.GetFileNameWithoutExtension(fileName);
                    if (ResourcePackCards.Any(c =>
                            string.Equals(c.Model.Title, title, StringComparison.OrdinalIgnoreCase) &&
                            c.IsInstalled))
                        continue;

                    info = new ModrinthModInfo
                    {
                        ProjectId = "local:" + fileName,
                        Slug = fileName,
                        Title = title,
                        Description = "Установлен локально (нет на Modrinth)",
                        Author = "локально",
                        Downloads = 0,
                        IconUrl = null,
                        ProjectType = "resourcepack"
                    };
                }

                var card = new ModCardViewModel(info, _modrinth, _translator, ResolveModInstallContext)
                {
                    IsInstalled = true
                };
                AttachResourcePackCard(card);
                ResourcePackCards.Add(card);
                if (!info.ProjectId.StartsWith("local:", StringComparison.Ordinal))
                    card.StartEnrich(token);
            }
        }
        catch (Exception ex)
        {
            RpError = $"Не удалось прочитать установленные ресурспаки: {ex.Message}";
        }
    }

    private void ClearResourcePackCards()
    {
        foreach (var card in ResourcePackCards)
        {
            card.PropertyChanged -= OnResourcePackCardPropertyChanged;
            card.DisposeResources();
        }
        ResourcePackCards.Clear();
        VisibleResourcePackCards.Clear();
        OnPropertyChanged(nameof(CanLoadMoreResourcePacks));
    }

    // ── Шейдеры: загрузка / фильтры ────────────────────────────────────

    private async Task ApplyShInstallFilterAsync()
    {
        if (ShInstallFilter == 1)
            await EnsureInstalledShadersListedAsync().ConfigureAwait(true);

        RebuildVisibleShaderCards();
        UpdateShFilterStatusText();
        OnPropertyChanged(nameof(CanLoadMoreShaders));
    }

    private async Task LoadShadersInternalAsync(bool reset)
    {
        if (ShInstallFilter == 1 && reset)
        {
            _shCts?.Cancel();
            _shCts = new CancellationTokenSource();
            ClearShaderCards();
            IsLoadingShaders = true;
            IsLoadingMoreShaders = false;
            ShError = null;
            ShStatusText = "Загрузка установленных…";
            try
            {
                await EnsureInstalledShadersListedAsync().ConfigureAwait(true);
                RebuildVisibleShaderCards();
                UpdateShFilterStatusText();
            }
            finally
            {
                IsLoadingShaders = false;
                OnPropertyChanged(nameof(CanLoadMoreShaders));
            }
            return;
        }

        if (reset)
        {
            _shCts?.Cancel();
            _shCts = new CancellationTokenSource();
            _shOffset = 0;
            _shTotalHits = 0;
            ClearShaderCards();
            IsLoadingShaders = true;
            IsLoadingMoreShaders = false;
            ShError = null;
            ShStatusText = "Загрузка с Modrinth…";
            OnPropertyChanged(nameof(CanLoadMoreShaders));
        }
        else
        {
            if (IsLoadingShaders || IsLoadingMoreShaders)
                return;
            IsLoadingMoreShaders = true;
            OnPropertyChanged(nameof(CanLoadMoreShaders));
        }

        var token = _shCts?.Token ?? CancellationToken.None;
        var gen = ++_shLoadGen;
        var query = ShSearchQuery;
        var gameVersion = ShFilterBySelectedVersion ? SelectedVersion?.Id : null;
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        const int pageSize = 24;

        try
        {
            var result = await _modrinth
                .SearchShadersAsync(query, gameVersion, index, _shOffset, pageSize, token)
                .ConfigureAwait(true);

            if (gen != _shLoadGen || token.IsCancellationRequested)
                return;

            _shTotalHits = result.TotalHits;
            _shOffset = result.NextOffset;

            foreach (var pack in result.Mods)
            {
                if (ShaderCards.Any(c =>
                        string.Equals(c.Model.ProjectId, pack.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var card = new ModCardViewModel(
                    pack,
                    _modrinth,
                    _translator,
                    ResolveModInstallContext);
                AttachShaderCard(card);
                ShaderCards.Add(card);
                card.StartEnrich(token);
            }

            RebuildVisibleShaderCards();
            UpdateShFilterStatusText();
            ShError = null;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            if (gen != _shLoadGen)
                return;
            ShError = $"Не удалось загрузить шейдеры: {ex.Message}";
            if (ShaderCards.Count == 0)
                ShStatusText = "Ошибка загрузки";
        }
        finally
        {
            if (gen == _shLoadGen)
            {
                IsLoadingShaders = false;
                IsLoadingMoreShaders = false;
                OnPropertyChanged(nameof(CanLoadMoreShaders));
                OnPropertyChanged(nameof(ShVersionFilterHint));
            }
        }
    }

    private void AttachShaderCard(ModCardViewModel card) =>
        card.PropertyChanged += OnShaderCardPropertyChanged;

    private void OnShaderCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModCardViewModel.IsInstalled) && ShInstallFilter != 0)
            Dispatcher.UIThread.Post(RebuildVisibleShaderCards);
    }

    private void RebuildVisibleShaderCards()
    {
        VisibleShaderCards.Clear();
        IEnumerable<ModCardViewModel> q = ShaderCards;
        if (ShInstallFilter == 1)
            q = ShaderCards.Where(c => c.IsInstalled);
        else if (ShInstallFilter == 2)
            q = ShaderCards.Where(c => !c.IsInstalled);

        foreach (var c in q)
            VisibleShaderCards.Add(c);
    }

    private void UpdateShFilterStatusText()
    {
        var filterLabel = ShInstallFilter switch
        {
            1 => "установленные",
            2 => "не установленные",
            _ => "все"
        };

        if (ShInstallFilter == 1)
        {
            ShStatusText = VisibleShaderCards.Count == 0
                ? "Нет установленных шейдеров в папке shaderpacks"
                : $"Установлено: {VisibleShaderCards.Count}";
            return;
        }

        if (_shTotalHits == 0 && ShaderCards.Count == 0)
        {
            ShStatusText = "Ничего не найдено";
            return;
        }

        ShStatusText =
            $"Показано {VisibleShaderCards.Count} ({filterLabel}) · " +
            $"всего в выдаче {ShaderCards.Count}" +
            (_shTotalHits > 0 ? $" / {_shTotalHits:N0}" : "") +
            $" · {ShVersionFilterHint}";
    }

    private async Task EnsureInstalledShadersListedAsync()
    {
        try
        {
            var packsDir = ModrinthService.GetShaderPacksDirectory();
            if (!Directory.Exists(packsDir))
                return;

            var files = Directory.GetFiles(packsDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
            if (files.Length == 0)
                return;

            var hashes = new List<(string Path, string Sha1)>();
            foreach (var file in files)
            {
                try
                {
                    await using var fs = File.OpenRead(file);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs))
                        .ToLowerInvariant();
                    hashes.Add((file, hash));
                }
                catch
                {
                    // skip
                }
            }

            var byHash = await _modrinth
                .GetVersionsByHashesAsync(hashes.Select(h => h.Sha1))
                .ConfigureAwait(true);

            var projectIds = byHash.Values
                .Select(v => v.ProjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projects = await _modrinth
                .GetProjectsByIdsAsync(projectIds)
                .ConfigureAwait(true);

            var token = _shCts?.Token ?? CancellationToken.None;

            foreach (var (path, sha1) in hashes)
            {
                var fileName = Path.GetFileName(path);
                ModrinthModInfo info;

                if (byHash.TryGetValue(sha1, out var ver) && !string.IsNullOrWhiteSpace(ver.ProjectId))
                {
                    if (ShaderCards.Any(c =>
                            string.Equals(c.Model.ProjectId, ver.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    {
                        var existing = ShaderCards.First(c =>
                            string.Equals(c.Model.ProjectId, ver.ProjectId, StringComparison.OrdinalIgnoreCase));
                        existing.IsInstalled = true;
                        continue;
                    }

                    info = BuildInstalledCardInfo(ver, projects, fileName, projectType: "shader");
                }
                else
                {
                    var title = Path.GetFileNameWithoutExtension(fileName);
                    if (ShaderCards.Any(c =>
                            string.Equals(c.Model.Title, title, StringComparison.OrdinalIgnoreCase) &&
                            c.IsInstalled))
                        continue;

                    info = new ModrinthModInfo
                    {
                        ProjectId = "local:" + fileName,
                        Slug = fileName,
                        Title = title,
                        Description = "Установлен локально (нет на Modrinth)",
                        Author = "локально",
                        Downloads = 0,
                        IconUrl = null,
                        ProjectType = "shader"
                    };
                }

                var card = new ModCardViewModel(info, _modrinth, _translator, ResolveModInstallContext)
                {
                    IsInstalled = true
                };
                AttachShaderCard(card);
                ShaderCards.Add(card);
                if (!info.ProjectId.StartsWith("local:", StringComparison.Ordinal))
                    card.StartEnrich(token);
            }
        }
        catch (Exception ex)
        {
            ShError = $"Не удалось прочитать установленные шейдеры: {ex.Message}";
        }
    }

    private void ClearShaderCards()
    {
        foreach (var card in ShaderCards)
        {
            card.PropertyChanged -= OnShaderCardPropertyChanged;
            card.DisposeResources();
        }
        ShaderCards.Clear();
        VisibleShaderCards.Clear();
        OnPropertyChanged(nameof(CanLoadMoreShaders));
    }

    /// <summary>Выбор подверсии на вкладке «Версии» (полный список).</summary>
    [RelayCommand]
    private void ChooseSubVersion(VariantRowViewModel? row)
    {
        if (row is null || SelectedVersion is null)
            return;

        OnVariantSelected(SelectedVersion, row.Model);

        // Подсветить выбор и в полном списке
        foreach (var v in FullVariants)
            v.IsSelected = v.Key == row.Key;
    }

    /// <summary>
    /// Полный список подверсий: сразу показываем рекомендуемые (если уже есть),
    /// затем в фоне качаем все и подменяем пакетами — без лагов UI.
    /// </summary>
    private async Task LoadFullVariantsAsync()
    {
        var version = SelectedVersion;
        if (version is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FullVariants.Clear();
                _fullVariantsLoadedForId = null;
                FullVariantsError = null;
                IsLoadingFullVariants = false;
                IsRefreshingFullVariants = false;
            });
            return;
        }

        var gameId = version.Id;

        // Уже полный кэш для этой версии
        if (_fullVariantsLoadedForId == gameId && FullVariants.Count > 0)
        {
            SyncFullVariantsSelection();
            return;
        }

        var gen = ++_fullVariantsLoadGen;
        _fullVariantsCts?.Cancel();
        _fullVariantsCts?.Dispose();
        _fullVariantsCts = new CancellationTokenSource();
        var token = _fullVariantsCts.Token;

        FullVariantsError = null;

        // 1) Мгновенный seed: рекомендуемые из левого списка (если загружены)
        var hasSeed = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != _fullVariantsLoadGen)
                return;

            if (FullVariants.Count == 0 || _fullVariantsLoadedForId != gameId)
            {
                FullVariants.Clear();
                var seed = version.Variants;
                if (seed.Count > 0)
                {
                    var installed = GetInstalledKeySet();
                    var selectedKey = SelectedVariant?.Key;
                    for (var i = 0; i < seed.Count; i++)
                    {
                        var row = seed[i];
                        FullVariants.Add(new VariantRowViewModel(row.Model)
                        {
                            IsLast = i == seed.Count - 1,
                            IsInstalled = installed.Contains(row.Key),
                            IsSelected = selectedKey is not null &&
                                         string.Equals(selectedKey, row.Key, StringComparison.Ordinal)
                        });
                    }

                    hasSeed = true;
                }
            }

            // Если seed есть — список виден, крутим «догрузку»; иначе полный спиннер
            IsLoadingFullVariants = !hasSeed;
            IsRefreshingFullVariants = hasSeed;
        });

        try
        {
            // 2) Сеть / maven — не на UI
            var variants = await _loaders
                .GetVariantsAsync(gameId, recommendedOnly: false, token)
                .ConfigureAwait(false);

            if (token.IsCancellationRequested || gen != _fullVariantsLoadGen)
                return;
            if (SelectedVersion?.Id != gameId)
                return;

            // 3) Сборка VM + проверка installed — тоже вне UI
            var installedKeys = GetInstalledKeySet();
            var selectedKey2 = SelectedVariant?.Key;
            var rows = await Task.Run(() =>
            {
                var list = new List<VariantRowViewModel>(variants.Count);
                for (var i = 0; i < variants.Count; i++)
                {
                    var model = variants[i];
                    list.Add(new VariantRowViewModel(model)
                    {
                        IsLast = i == variants.Count - 1,
                        IsInstalled = installedKeys.Contains(model.Key),
                        IsSelected = selectedKey2 is not null &&
                                     string.Equals(selectedKey2, model.Key, StringComparison.Ordinal)
                    });
                }

                return list;
            }, token).ConfigureAwait(false);

            if (token.IsCancellationRequested || gen != _fullVariantsLoadGen)
                return;

            // 4) Пакетная подстановка на UI (не все сразу)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != _fullVariantsLoadGen || SelectedVersion?.Id != gameId)
                    return;

                FullVariants.Clear();
            });

            const int batchSize = 24;
            for (var offset = 0; offset < rows.Count; offset += batchSize)
            {
                if (token.IsCancellationRequested || gen != _fullVariantsLoadGen)
                    return;

                var batch = rows.Skip(offset).Take(batchSize).ToList();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (gen != _fullVariantsLoadGen || SelectedVersion?.Id != gameId)
                        return;

                    foreach (var row in batch)
                        FullVariants.Add(row);
                });

                // Дать Avalonia отрисовать кадр
                if (offset + batchSize < rows.Count)
                    await Task.Delay(8, token).ConfigureAwait(false);
            }

            if (gen == _fullVariantsLoadGen)
                _fullVariantsLoadedForId = gameId;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            if (gen != _fullVariantsLoadGen)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FullVariantsError = $"Не удалось загрузить подверсии: {ex.Message}";
                if (FullVariants.Count == 0)
                {
                    var vanilla = new VersionVariant
                    {
                        Key = $"vanilla:{gameId}",
                        GameVersionId = gameId,
                        Kind = LoaderKind.Vanilla,
                        DisplayName = "Vanilla",
                        LoaderVersion = gameId
                    };
                    FullVariants.Add(new VariantRowViewModel(vanilla)
                    {
                        IsLast = true,
                        IsInstalled = GetInstalledKeySet().Contains(vanilla.Key)
                    });
                    _fullVariantsLoadedForId = gameId;
                }
            });
        }
        finally
        {
            if (gen == _fullVariantsLoadGen)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsLoadingFullVariants = false;
                    IsRefreshingFullVariants = false;
                });
            }
        }
    }

    private HashSet<string> GetInstalledKeySet()
    {
        try
        {
            return _installs.ListInstalledBuilds()
                .Select(b => b.Key)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>GameVersionId, у которых есть хотя бы одна установленная сборка.</summary>
    private HashSet<string> GetInstalledGameVersionIdSet()
    {
        try
        {
            return _installs.ListInstalledBuilds()
                .Select(b => b.GameVersionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Обновить ✓ у версий и флаги подверсий после install/refresh.</summary>
    private void RefreshVersionsInstalledFlags()
    {
        var ids = GetInstalledGameVersionIdSet();
        foreach (var v in Versions)
            v.RefreshInstalledFlags(ids);
    }

    private void SyncFullVariantsSelection()
    {
        var key = SelectedVariant?.Key;
        var installed = GetInstalledKeySet();
        foreach (var v in FullVariants)
        {
            v.IsSelected = key is not null && v.Key == key;
            v.IsInstalled = installed.Contains(v.Key);
        }
    }

    [RelayCommand]
    private void SelectErrorsTab() => MainTabIndex = 5;

    /// <summary>После выбора на вкладке «Версии» — вернуться на главную.</summary>
    [RelayCommand]
    private void ConfirmVersionAndGoHome() => MainTabIndex = 0;

    /// <summary>Скопировать текст ошибки в буфер обмена.</summary>
    [RelayCommand]
    private async Task CopyErrorLogAsync()
    {
        var text = !string.IsNullOrWhiteSpace(ErrorMessage)
            ? ErrorMessage!
            : ErrorsPanelText;

        if (string.IsNullOrWhiteSpace(text))
            return;

        RequestCopyToClipboard?.Invoke(this, text);

        ErrorCopiedFeedback = true;
        try
        {
            await Task.Delay(1600).ConfigureAwait(true);
        }
        finally
        {
            ErrorCopiedFeedback = false;
        }
    }

    partial void OnErrorMessageChanged(string? value)
    {
        // При новой ошибке — сразу на вкладку «Ошибки»
        if (!string.IsNullOrWhiteSpace(value))
            MainTabIndex = 5;
    }

    partial void OnModsFilterBySelectedVersionChanged(bool value)
    {
        if (IsModsTab)
            _ = LoadModsInternalAsync(reset: true);
        OnPropertyChanged(nameof(ModsVersionFilterHint));
    }

    partial void OnRpFilterBySelectedVersionChanged(bool value)
    {
        if (IsResourcePacksTab)
            _ = LoadResourcePacksInternalAsync(reset: true);
        OnPropertyChanged(nameof(RpVersionFilterHint));
    }

    partial void OnShFilterBySelectedVersionChanged(bool value)
    {
        if (IsShadersTab)
            _ = LoadShadersInternalAsync(reset: true);
        OnPropertyChanged(nameof(ShVersionFilterHint));
    }

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private bool _showOldBeta;

    [ObservableProperty]
    private bool _showOldAlpha;

    /// <summary>Выбранная сборка установлена.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionEnabled))]
    private bool _isSelectedInstalled;

    /// <summary>Игра сейчас запущена — кнопка «ЗАПУЩЕНА».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionEnabled))]
    private bool _isGameRunning;

    /// <summary>Идёт pre-launch: Java / моды / зависимости.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionEnabled))]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStatus))]
    private bool _isPreparingLaunch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    [NotifyPropertyChangedFor(nameof(InstallFillWidth))]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStatus))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionEnabled))]
    [NotifyPropertyChangedFor(nameof(ErrorsPanelText))]
    private bool _isInstalling;

    /// <summary>Прогресс установки 0..1 (для заливки кнопки).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallFillWidth))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private double _installProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private long _downloadBytesDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private long _downloadBytesTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private double _downloadSpeedBps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTip))]
    private string _downloadStage = "";

    /// <summary>Ширина оранжевой заливки кнопки.</summary>
    public double InstallFillWidth =>
        IsInstalling
            ? Math.Clamp(InstallProgress, 0, 1) * PlayButtonWidth
            : PlayButtonWidth;

    // IsGameRunning тоже обновляет подпись/доступность кнопки

    /// <summary>Текст главной кнопки: ИГРАТЬ / УСТАНОВИТЬ / ОТМЕНА / ЗАПУЩЕНА.</summary>
    public string PrimaryActionText
    {
        get
        {
            if (IsInstalling)
                return "ОТМЕНА";
            if (IsPreparingLaunch)
                return "ПРОВЕРКА…";
            if (IsGameRunning)
                return "ЗАПУЩЕНА";
            return IsSelectedInstalled ? "ИГРАТЬ" : "УСТАНОВИТЬ";
        }
    }

    /// <summary>Подсказка при наведении на кнопку ИГРАТЬ / УСТАНОВИТЬ.</summary>
    public string PrimaryActionTip
    {
        get
        {
            if (IsLoading)
                return "Подождите, список версий ещё загружается…";

            if (IsPreparingLaunch)
            {
                var stage = string.IsNullOrWhiteSpace(DownloadStage)
                    ? "Проверка Java, модов и зависимостей…"
                    : DownloadStage;
                return stage;
            }

            if (IsInstalling)
            {
                var status = DownloadStatusText;
                var progress = $"{InstallProgress * 100:0}%";
                if (string.IsNullOrWhiteSpace(status))
                    return $"Установка… {progress}. Нажмите, чтобы отменить.";
                return $"{status} · {progress}. Нажмите, чтобы отменить.";
            }

            if (IsGameRunning)
                return "Игра уже запущена. Дождитесь завершения, чтобы запустить снова.";

            if (SelectedVariant is null)
                return "Сначала выберите версию Minecraft и сборку (Vanilla / Forge / OptiFine…).";

            if (!IsSelectedInstalled)
            {
                var name = SelectedVariant.DisplayName;
                return $"Скачать и установить «{name}» для {SelectedVariant.GameVersionId}.";
            }

            var nick = string.IsNullOrWhiteSpace(SelectedNickname) ? "Player" : SelectedNickname!;
            return $"Запустить {SelectedVariant.GameVersionId} ({SelectedVariant.KindLabel}) за аккаунт «{nick}».";
        }
    }

    /// <summary>Можно ли нажать главную кнопку (во время установки — для отмены).</summary>
    public bool IsPrimaryActionEnabled =>
        IsInstalling ||
        (!IsLoading &&
         !IsPreparingLaunch &&
         SelectedVariant is not null &&
         !IsGameRunning);

    /// <summary>Показывать статус рядом с «ZLauncher» (установка / pre-launch / update).</summary>
    public bool ShowDownloadStatus =>
        IsInstalling || IsPreparingLaunch || !string.IsNullOrWhiteSpace(UpdateStatusText);

    /// <summary>Тихая строка статуса рядом с ZLauncher.</summary>
    public string DownloadStatusText
    {
        get
        {
            if (IsPreparingLaunch)
            {
                var stage = string.IsNullOrEmpty(DownloadStage) ? "Проверка перед запуском" : DownloadStage;
                return stage;
            }

            if (IsInstalling)
            {
                var done = FormatBytes(DownloadBytesDone);
                var total = DownloadBytesTotal > 0 ? FormatBytes(DownloadBytesTotal) : "…";
                var speed = DownloadSpeedBps > 0 ? FormatBytes(DownloadSpeedBps) + "/s" : "—";
                var stageInstall = string.IsNullOrEmpty(DownloadStage) ? "Загрузка" : DownloadStage;
                return $"{stageInstall}  ·  {done} / {total}  ·  {speed}";
            }

            return UpdateStatusText ?? "";
        }
    }

    /// <summary>Выбранный аккаунт (обычный).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedNickname))]
    [NotifyPropertyChangedFor(nameof(SelectedAccountTypeLabel))]
    [NotifyPropertyChangedFor(nameof(SelectedAccountHint))]
    private PlayerAccount? _selectedAccount;

    /// <summary>Ник выбранного аккаунта (в игре).</summary>
    public string? SelectedNickname => SelectedAccount?.Username;

    /// <summary>«Обычный» и т.п.</summary>
    public string SelectedAccountTypeLabel => SelectedAccount?.TypeLabel ?? "Обычный";

    /// <summary>«Без входа · обычный».</summary>
    public string SelectedAccountHint => SelectedAccount?.TypeHint ?? "Без входа · обычный";

    /// <summary>Поле ввода имени для нового обычного аккаунта.</summary>
    [ObservableProperty]
    private string _newNicknameText = "";

    /// <summary>Превью скина выбранного аккаунта.</summary>
    [ObservableProperty]
    private Bitmap? _selectedSkinBitmap;

    /// <summary>Скин загружен (с зеркал/кэша).</summary>
    [ObservableProperty]
    private bool _hasSkinPreview;

    // ── Настройки запуска (сохраняются) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryMaxLabel))]
    [NotifyPropertyChangedFor(nameof(IsMemorySliderEnabled))]
    private int _memoryMaxMb = 4096;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryMaxLabel))]
    [NotifyPropertyChangedFor(nameof(IsMemorySliderEnabled))]
    private bool _autoMemory = true;

    [ObservableProperty]
    private string _jvmArguments = "";

    [ObservableProperty]
    private int _windowWidth = 1280;

    [ObservableProperty]
    private int _windowHeight = 720;

    [ObservableProperty]
    private bool _fullscreen;

    [ObservableProperty]
    private bool _minimizeOnLaunch;

    [ObservableProperty]
    private bool _closeOnLaunch;

    [ObservableProperty]
    private bool _launchAfterInstall;

    /// <summary>UI: свернуть окно лаунчера после успешного запуска.</summary>
    public event EventHandler? RequestMinimizeLauncher;

    /// <summary>UI: закрыть лаунчер после успешного запуска.</summary>
    public event EventHandler? RequestCloseLauncher;

    /// <summary>UI: скопировать текст в буфер (обработка в MainWindow).</summary>
    public event EventHandler<string>? RequestCopyToClipboard;

    /// <summary>Подпись RAM, например «4 ГБ» или «Авто · 8 ГБ».</summary>
    public string MemoryMaxLabel =>
        AutoMemory
            ? $"Авто · {FormatMemory(ComputeAutoMemoryMb())}"
            : FormatMemory(MemoryMaxMb);

    public bool IsMemorySliderEnabled => !AutoMemory;

    /// <summary>Максимум для слайдера (по объёму ОЗУ ПК, не больше 32 ГБ).</summary>
    public int MemorySliderMax { get; private set; } = 8192;

    private int _systemTotalMb = 8192;

    /// <param name="deferInit">
    /// true — только каркас (для splash): данные грузит <see cref="InitializeCoreAsync"/> и далее.
    /// false — полная инициализация как раньше (дизайн / fallback).
    /// </param>
    public MainViewModel(bool deferInit = false)
    {
        _settings = _settingsService.Load();
        _preLaunch = new PreLaunchService(_java, _modrinth);
        _launcher = new GameLaunchService(_installs, _java, _skins);
        _launcher.ProcessExited += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsGameRunning = false;
                // Быстрый краш (моды / Java): показать причину из latest.log
                try
                {
                    var tip = TryReadRecentGameCrashHint();
                    if (!string.IsNullOrWhiteSpace(tip) && string.IsNullOrWhiteSpace(ErrorMessage))
                        ErrorMessage = tip;
                }
                catch
                {
                    // ignore
                }
            });
        };

        if (deferInit)
            return;

        LoadNicknamesFromSettings();
        LoadLaunchSettingsFromSettings();
        _ = LoadVersionsAsync();
        _ = RefreshSkinPreviewAsync();
    }

    /// <summary>Настройки, аккаунты, RAM-слайдер.</summary>
    public async Task InitializeCoreAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        log?.Invoke("Чтение settings.json…");
        await Task.Run(() =>
        {
            // Load уже в ctor; повторно применяем к VM на UI
        }, ct).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LoadNicknamesFromSettings();
            LoadLaunchSettingsFromSettings();
        });

        log?.Invoke($"Аккаунтов: {Accounts.Count}");
        await Task.Delay(120, ct).ConfigureAwait(false);
    }

    /// <summary>Список версий Mojang + фильтр + выбор.</summary>
    public async Task InitializeVersionsAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        log?.Invoke("Запрос version_manifest…");
        await LoadVersionsAsync(showBanner: false).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var count = 0;
        await Dispatcher.UIThread.InvokeAsync(() => count = Versions.Count);
        log?.Invoke(count > 0
            ? $"Версий в списке: {count}"
            : "Список версий пуст или недоступен");
        await Task.Delay(100, ct).ConfigureAwait(false);
    }

    /// <summary>Скин, новости, финальная подготовка UI.</summary>
    public async Task InitializeUiAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        log?.Invoke("Загрузка превью скина…");
        await RefreshSkinPreviewAsync().ConfigureAwait(true);

        log?.Invoke("Загрузка новостей Minecraft…");
        // Не блокируем splash надолго: стартуем в фоне, UI подхватит
        _ = LoadNewsAsync();

        log?.Invoke("Интерфейс готов");
        await Task.Delay(80, ct).ConfigureAwait(false);
    }

    private void LoadLaunchSettingsFromSettings()
    {
        try
        {
            var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            if (totalMb > 1024)
            {
                _systemTotalMb = totalMb;
                MemorySliderMax = Math.Clamp(totalMb - 1024, 2048, 32768);
            }
        }
        catch
        {
            _systemTotalMb = 16384;
            MemorySliderMax = 16384;
        }

        _suppressSettingsSave = true;
        try
        {
            AutoMemory = _settings.AutoMemory;
            MemoryMaxMb = Math.Clamp(_settings.MemoryMaxMb, 1024, MemorySliderMax);
            JvmArguments = _settings.JvmArguments ?? "";
            WindowWidth = Math.Clamp(_settings.WindowWidth, 640, 7680);
            WindowHeight = Math.Clamp(_settings.WindowHeight, 480, 4320);
            Fullscreen = _settings.Fullscreen;
            MinimizeOnLaunch = _settings.MinimizeOnLaunch;
            CloseOnLaunch = _settings.CloseOnLaunch;
            LaunchAfterInstall = _settings.LaunchAfterInstall;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private void PersistLaunchSettings()
    {
        _settings.AutoMemory = AutoMemory;
        _settings.MemoryMaxMb = MemoryMaxMb;
        _settings.JvmArguments = JvmArguments ?? "";
        _settings.WindowWidth = WindowWidth;
        _settings.WindowHeight = WindowHeight;
        _settings.Fullscreen = Fullscreen;
        _settings.MinimizeOnLaunch = MinimizeOnLaunch;
        _settings.CloseOnLaunch = CloseOnLaunch;
        _settings.LaunchAfterInstall = LaunchAfterInstall;
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// Авто-RAM: ~половина ОЗУ, но оставляем ≥2 ГБ системе; не меньше 2 ГБ и не больше слайдера.
    /// </summary>
    private int ComputeAutoMemoryMb()
    {
        var total = Math.Max(_systemTotalMb, 4096);
        var leaveForOs = Math.Clamp(total / 4, 2048, 4096);
        var half = (total - leaveForOs);
        // Округлить к 512 МБ
        half = (half / 512) * 512;
        return Math.Clamp(half, 2048, MemorySliderMax);
    }

    private (int MinMb, int MaxMb) ResolveLaunchMemory()
    {
        var maxMb = AutoMemory ? ComputeAutoMemoryMb() : Math.Clamp(MemoryMaxMb, 1024, MemorySliderMax);
        // Xms: четверть от max, не меньше 512, не больше max
        var minMb = Math.Clamp(maxMb / 4, 512, maxMb);
        minMb = (minMb / 256) * 256;
        if (minMb < 512) minMb = 512;
        if (minMb > maxMb) minMb = maxMb;
        return (minMb, maxMb);
    }

    partial void OnMemoryMaxMbChanged(int value)
    {
        OnPropertyChanged(nameof(MemoryMaxLabel));
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    /// <summary>Округлить RAM к шагу 512 МБ (после отпускания слайдера).</summary>
    public void SnapMemoryMaxToStep()
    {
        if (AutoMemory)
            return;

        var snapped = SnapMemoryMb(MemoryMaxMb);
        if (snapped != MemoryMaxMb)
            MemoryMaxMb = snapped;
        else
            OnPropertyChanged(nameof(MemoryMaxLabel));
    }

    public static int SnapMemoryMb(int mb)
    {
        var snapped = (int)(Math.Round(mb / 512.0) * 512);
        return Math.Clamp(snapped, 1024, 32768);
    }

    partial void OnAutoMemoryChanged(bool value)
    {
        // При выключении авто — подставить расчётное значение на слайдер
        if (!value && !_suppressSettingsSave)
        {
            var auto = ComputeAutoMemoryMb();
            if (Math.Abs(MemoryMaxMb - auto) > 1)
                MemoryMaxMb = auto;
        }

        OnPropertyChanged(nameof(MemoryMaxLabel));
        OnPropertyChanged(nameof(IsMemorySliderEnabled));
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnJvmArgumentsChanged(string value)
    {
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnWindowWidthChanged(int value)
    {
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnWindowHeightChanged(int value)
    {
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnFullscreenChanged(bool value)
    {
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnMinimizeOnLaunchChanged(bool value)
    {
        // Закрытие важнее сворачивания — если включили close, minimize можно оставить,
        // но UI логика: при Close лаунчер закроется.
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnCloseOnLaunchChanged(bool value)
    {
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    partial void OnLaunchAfterInstallChanged(bool value)
    {
        if (!_suppressSettingsSave)
            PersistLaunchSettings();
    }

    private static string FormatMemory(int mb)
    {
        if (mb >= 1024)
        {
            var gb = mb / 1024.0;
            return gb % 1 == 0 ? $"{gb:0} ГБ" : $"{gb:0.0} ГБ";
        }

        return $"{mb} МБ";
    }

    private void LoadNicknamesFromSettings()
    {
        Accounts.Clear();

        // Миграция уже могла заполнить Accounts в AppSettingsService.Load
        foreach (var acc in _settings.Accounts
                     .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Username)))
        {
            if (string.IsNullOrWhiteSpace(acc.UuidCompact))
                acc.UuidCompact = SkinService.OfflineUuidCompact(acc.Username);
            Accounts.Add(acc);
        }

        if (Accounts.Count == 0)
        {
            var def = PlayerAccount.CreateOffline("Player", SkinService.OfflineUuidCompact("Player"));
            Accounts.Add(def);
            PersistAccounts();
        }

        PlayerAccount? selected = null;
        if (!string.IsNullOrWhiteSpace(_settings.SelectedAccountId))
        {
            selected = Accounts.FirstOrDefault(a =>
                string.Equals(a.Id, _settings.SelectedAccountId, StringComparison.Ordinal));
        }

        if (selected is null && !string.IsNullOrWhiteSpace(_settings.SelectedNickname))
        {
            selected = Accounts.FirstOrDefault(a =>
                string.Equals(a.Username, _settings.SelectedNickname, StringComparison.OrdinalIgnoreCase));
        }

        SelectedAccount = selected ?? Accounts[0];
        PersistAccounts();
    }

    private void PersistAccounts()
    {
        _settings.Accounts = Accounts.ToList();
        _settings.SelectedAccountId = SelectedAccount?.Id;
        _settings.SelectedNickname = SelectedAccount?.Username;
        _settings.Nicknames = Accounts.Select(a => a.Username).ToList();
        _settingsService.Save(_settings);
    }

    partial void OnSelectedAccountChanged(PlayerAccount? value)
    {
        if (!_suppressSettingsSave && value is not null)
        {
            if (_settings.SelectedAccountId != value.Id)
            {
                _settings.SelectedAccountId = value.Id;
                _settings.SelectedNickname = value.Username;
                _settingsService.Save(_settings);
            }
        }

        OnPropertyChanged(nameof(PrimaryActionTip));
        _ = RefreshSkinPreviewAsync();
    }

    private async Task RefreshSkinPreviewAsync()
    {
        var nick = SelectedNickname;
        if (string.IsNullOrWhiteSpace(nick))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedSkinBitmap?.Dispose();
                SelectedSkinBitmap = null;
                HasSkinPreview = false;
            });
            return;
        }

        _skinCts?.Cancel();
        _skinCts?.Dispose();
        _skinCts = new CancellationTokenSource();
        var token = _skinCts.Token;

        try
        {
            // Обычный аккаунт: offline UUID, скин с зеркал по нику
            var info = await _skins
                .ResolveAndFetchAsync(nick, token, forceOffline: true)
                .ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return;

            // Превью — только лицо (голова), не вся текстура скина
            Bitmap? bmp = null;
            var previewPath = info.HeadFilePath is not null && File.Exists(info.HeadFilePath)
                ? info.HeadFilePath
                : null;

            if (previewPath is null &&
                info.SkinFilePath is not null &&
                File.Exists(info.SkinFilePath))
            {
                previewPath = _skins.BuildHeadPreview(info.SkinFilePath, info.UuidCompact);
            }

            if (previewPath is not null && File.Exists(previewPath))
            {
                try
                {
                    var path = previewPath;
                    bmp = await Task.Run(() => new Bitmap(path), token).ConfigureAwait(false);
                }
                catch
                {
                    bmp = null;
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedSkinBitmap?.Dispose();
                SelectedSkinBitmap = bmp;
                HasSkinPreview = bmp is not null;
            });
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedSkinBitmap?.Dispose();
                SelectedSkinBitmap = null;
                HasSkinPreview = false;
            });
        }
    }

    /// <summary>Открыт ли выпадающий список аккаунтов.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NickListExpandedHeight))]
    [NotifyPropertyChangedFor(nameof(NickListOpacity))]
    [NotifyPropertyChangedFor(nameof(NickChevronAngle))]
    private bool _isNickListOpen;

    /// <summary>Макс. высота списка аккаунтов (viewport; воздух снизу — spacer в XAML).</summary>
    public double NickListMaxHeight => Math.Min(Math.Max(Accounts.Count, 1) * 38 + 12, 200);

    /// <summary>Текущая анимируемая высота (0 или NickListMaxHeight).</summary>
    public double NickListExpandedHeight => IsNickListOpen ? NickListMaxHeight : 0;

    public double NickListOpacity => IsNickListOpen ? 1 : 0;

    public double NickChevronAngle => IsNickListOpen ? 180 : 0;

    [RelayCommand]
    private void ToggleNickList()
    {
        IsNickListOpen = !IsNickListOpen;
    }

    [RelayCommand]
    private void CloseNickList()
    {
        IsNickListOpen = false;
    }

    [RelayCommand]
    private void SelectNickname(string? nick)
    {
        if (string.IsNullOrWhiteSpace(nick))
            return;

        var match = Accounts.FirstOrDefault(a =>
            string.Equals(a.Username, nick, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;

        SelectedAccount = match;
        IsNickListOpen = false;
    }

    [RelayCommand]
    private void SelectAccount(PlayerAccount? account)
    {
        if (account is null)
            return;

        SelectedAccount = account;
        IsNickListOpen = false;
    }

    /// <summary>
    /// Проверяет ввод для создания обычного аккаунта.
    /// isNew = true, если нужно создать; false — уже есть, только выбрать.
    /// </summary>
    public string? TryPrepareNicknameAdd(out bool isNew)
    {
        isNew = false;
        var nick = SanitizeNickname(NewNicknameText);
        if (nick is null)
            return null;

        if (Accounts.Any(a => string.Equals(a.Username, nick, StringComparison.OrdinalIgnoreCase)))
        {
            isNew = false;
            return nick;
        }

        isNew = true;
        return nick;
    }

    /// <summary>Создаёт обычный аккаунт после анимации.</summary>
    public void CommitNicknameAdd(string nick, bool isNew)
    {
        if (isNew)
        {
            if (!Accounts.Any(a => string.Equals(a.Username, nick, StringComparison.OrdinalIgnoreCase)))
            {
                var account = PlayerAccount.CreateOffline(nick, SkinService.OfflineUuidCompact(nick));
                Accounts.Add(account);
                OnPropertyChanged(nameof(NickListMaxHeight));
            }
        }

        var match = Accounts.First(a =>
            string.Equals(a.Username, nick, StringComparison.OrdinalIgnoreCase));
        SelectedAccount = match;
        NewNicknameText = "";
        PersistAccounts();
    }

    [RelayCommand]
    private void AddNickname()
    {
        var nick = TryPrepareNicknameAdd(out var isNew);
        if (nick is null)
            return;

        CommitNicknameAdd(nick, isNew);
    }

    /// <summary>Создать обычный аккаунт (алиас для UI).</summary>
    [RelayCommand]
    private void CreateOfflineAccount() => AddNickname();

    [RelayCommand]
    private void RemoveNickname(string? nick)
    {
        nick ??= SelectedNickname;
        if (string.IsNullOrWhiteSpace(nick))
            return;

        if (Accounts.Count <= 1)
            return;

        var match = Accounts.FirstOrDefault(a =>
            string.Equals(a.Username, nick, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;

        var wasSelected = ReferenceEquals(SelectedAccount, match) ||
                          string.Equals(SelectedNickname, match.Username, StringComparison.OrdinalIgnoreCase);
        Accounts.Remove(match);
        OnPropertyChanged(nameof(NickListMaxHeight));
        OnPropertyChanged(nameof(NickListExpandedHeight));

        if (wasSelected)
            SelectedAccount = Accounts[0];

        PersistAccounts();
    }

    [RelayCommand]
    private void RemoveAccount(PlayerAccount? account)
    {
        if (account is null)
            return;
        RemoveNickname(account.Username);
    }

    private static string? SanitizeNickname(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var nick = raw.Trim();
        // Minecraft: 3–16 символов, a-z A-Z 0-9 _
        if (nick.Length is < 3 or > 16)
            return null;

        foreach (var c in nick)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return null;
        }

        return nick;
    }

    partial void OnSelectedVersionChanged(MinecraftVersionItemViewModel? value)
    {
        foreach (var item in Versions)
            item.IsSelected = ReferenceEquals(item, value);

        // Если у версии есть выбранная подверсия — сверим установку
        if (value?.SelectedVariant is not null)
            SelectedVariant = value.SelectedVariant;
        else
            RefreshInstallState();

        // На вкладке «Версии» — перезагрузить полный список подверсий
        if (IsVersionsTab)
            _ = LoadFullVariantsAsync();
        else
        {
            // Сброс кэша: при следующем заходе на вкладку подгрузим заново
            _fullVariantsLoadedForId = null;
        }

        // На вкладках контента с фильтром по версии — обновить списки
        if (IsModsTab && ModsFilterBySelectedVersion)
            _ = LoadModsInternalAsync(reset: true);
        OnPropertyChanged(nameof(ModsVersionFilterHint));

        if (IsResourcePacksTab && RpFilterBySelectedVersion)
            _ = LoadResourcePacksInternalAsync(reset: true);
        OnPropertyChanged(nameof(RpVersionFilterHint));

        if (IsShadersTab && ShFilterBySelectedVersion)
            _ = LoadShadersInternalAsync(reset: true);
        OnPropertyChanged(nameof(ShVersionFilterHint));

        if (_suppressSettingsSave || value is null)
            return;

        if (_settings.SelectedVersionId != value.Id)
        {
            _settings.SelectedVersionId = value.Id;
            _settingsService.Save(_settings);
        }
    }

    partial void OnSelectedVariantChanged(VersionVariant? value)
    {
        RefreshInstallState();
        OnPropertyChanged(nameof(SelectedBuildSummary));
        OnPropertyChanged(nameof(SelectedBuildHint));

        if (_suppressSettingsSave || value is null)
            return;

        if (_settings.SelectedVariantKey == value.Key)
            return;

        _settings.SelectedVariantKey = value.Key;
        _settings.SelectedVersionId = value.GameVersionId;
        _settingsService.Save(_settings);
    }

    private void RefreshInstallState()
    {
        if (IsInstalling)
            return;

        IsSelectedInstalled = _installs.IsInstalled(SelectedVariant);
        InstallProgress = IsSelectedInstalled ? 1 : 0;
        OnPropertyChanged(nameof(InstallFillWidth));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionTip));
        OnPropertyChanged(nameof(SelectedBuildHint));
        OnPropertyChanged(nameof(SelectedBuildSummary));
    }

    /// <summary>Для splash: обновить флаги установки после загрузки версий.</summary>
    public void RefreshInstallStatePublic() => RefreshInstallState();

    /// <summary>
    /// AllowConcurrentExecutions: пока идёт await установки, стандартный AsyncRelayCommand
    /// держит CanExecute=false — кнопка «ОТМЕНА» серая и не нажимается.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PrimaryActionAsync()
    {
        // Во время установки — отмена по повторному нажатию (не ждём install)
        if (IsInstalling)
        {
            CancelInstall();
            return;
        }

        if (IsGameRunning || IsLoading)
            return;

        if (SelectedVariant is null || SelectedVersion is null)
            return;

        if (IsSelectedInstalled)
        {
            await LaunchGameAsync().ConfigureAwait(true);
            return;
        }

        // Не await'им всю установку внутри команды: иначе (даже с concurrent)
        // повторный клик зависит от CanExecute, а «ОТМЕНА» должна срабатывать сразу.
        // InstallSelectedAsync сам выставляет IsInstalling и ловит исключения.
        _ = InstallSelectedAsync();
    }

    /// <summary>Отменить текущую загрузку / установку.</summary>
    [RelayCommand]
    private void CancelInstall()
    {
        if (!IsInstalling && _installCts is null)
            return;

        try
        {
            _installCts?.Cancel();
            DownloadStage = "Отмена…";
            OnPropertyChanged(nameof(PrimaryActionTip));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Краткий разбор latest.log после внезапного выхода игры.</summary>
    private static string? TryReadRecentGameCrashHint()
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ZLauncher", "game", "logs", "latest.log");
            if (!File.Exists(logPath))
                return null;

            // Только свежий лог (краш в последние 2 минуты)
            var age = DateTime.Now - File.GetLastWriteTime(logPath);
            if (age > TimeSpan.FromMinutes(2))
                return null;

            var text = File.ReadAllText(logPath);
            if (text.Length > 200_000)
                text = text[^200_000..];

            // Missing Mods / dependency
            var missing = Regex.Matches(
                text,
                @"Missing Mods:\s*(?<body>(?:.+\r?\n)+?)(?:\r?\n\r?\n|at )",
                RegexOptions.IgnoreCase);
            if (missing.Count > 0)
            {
                var body = missing[^1].Groups["body"].Value.Trim();
                if (body.Length > 400)
                    body = body[..400] + "…";
                return
                    "Игра закрылась из‑за несовместимых или недостающих модов.\n" +
                    "Несовместимые jar отключаются при следующем запуске автоматически.\n\n" +
                    body;
            }

            if (text.Contains("There were errors previously", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("net.minecraftforge.fml.common.MissingModsException", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MissingModsException", StringComparison.OrdinalIgnoreCase))
            {
                return
                    "Игра закрылась: Forge не смог загрузить моды (несовместимая версия или нет зависимости).\n" +
                    "При следующем запуске лаунчер отключит чужие jar и подтянет зависимости.\n" +
                    "Подробности: game/logs/latest.log";
            }

            if (text.Contains("#@!@# Game crashed", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("---- Minecraft Crash Report ----", StringComparison.OrdinalIgnoreCase))
            {
                return
                    "Игра аварийно завершилась (crash report).\n" +
                    "Смотри game/crash-reports и game/logs/latest.log";
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task LaunchGameAsync()
    {
        if (SelectedVariant is null || SelectedVersion is null)
            return;

        _launcher.ClearIfExited();
        if (_launcher.IsRunning)
        {
            IsGameRunning = true;
            return;
        }

        ErrorMessage = null;
        IsPreparingLaunch = true;
        DownloadStage = "Проверка перед запуском…";
        InstallProgress = 0;
        OnPropertyChanged(nameof(DownloadStatusText));

        try
        {
            var account = SelectedAccount;
            var nick = account?.Username;
            if (string.IsNullOrWhiteSpace(nick))
                nick = "Player";

            var (minMb, maxMb) = ResolveLaunchMemory();

            // 1) Java  2) совместимость модов  3) зависимости
            var prepProgress = new Progress<InstallProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!string.IsNullOrEmpty(p.Stage))
                        DownloadStage = p.Stage;
                    InstallProgress = Math.Clamp(p.Progress, 0, 1);
                    OnPropertyChanged(nameof(DownloadStatusText));
                    OnPropertyChanged(nameof(PrimaryActionTip));
                });
            });

            var report = await _preLaunch
                .PrepareAsync(
                    SelectedVersion.Id,
                    SelectedVariant.Kind,
                    mainClass: null,
                    prepProgress)
                .ConfigureAwait(true);

            // Краткий отчёт в статус (не ошибка)
            DownloadStage = report.Summary;
            OnPropertyChanged(nameof(DownloadStatusText));

            if (report.Warnings.Count > 0)
            {
                // Не блокируем запуск — предупреждения можно посмотреть в ОШИБКИ как info? оставим в summary
            }

            DownloadStage = "Запуск игры…";
            OnPropertyChanged(nameof(DownloadStatusText));

            await _launcher.LaunchAsync(
                    SelectedVersion.Version,
                    SelectedVariant,
                    nick,
                    minMb,
                    maxMb,
                    JvmArguments,
                    WindowWidth,
                    WindowHeight,
                    Fullscreen,
                    offlineAccount: account is null || account.Type == AccountType.Offline,
                    offlineUuidCompact: account?.UuidCompact)
                .ConfigureAwait(true);

            IsGameRunning = true;

            // После успешного старта — закрыть или свернуть лаунчер
            if (CloseOnLaunch)
                RequestCloseLauncher?.Invoke(this, EventArgs.Empty);
            else if (MinimizeOnLaunch)
                RequestMinimizeLauncher?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsGameRunning = false;
            var detail = string.IsNullOrWhiteSpace(ex.Message)
                ? (ex.InnerException?.Message ?? ex.GetType().Name)
                : ex.Message.Trim();
            if (detail.Length > 2500)
                detail = detail[..2500] + "…";
            ErrorMessage = $"Не удалось запустить игру:\n{detail}";
        }
        finally
        {
            IsPreparingLaunch = false;
            DownloadStage = "";
            InstallProgress = IsSelectedInstalled ? 1 : 0;
            OnPropertyChanged(nameof(DownloadStatusText));
            OnPropertyChanged(nameof(PrimaryActionText));
            OnPropertyChanged(nameof(PrimaryActionTip));
        }
    }

    private async Task InstallSelectedAsync()
    {
        if (SelectedVariant is null || SelectedVersion is null)
            return;

        var gameVersion = SelectedVersion.Version;
        var variant = SelectedVariant;

        _installCts?.Cancel();
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        var token = _installCts.Token;

        // Перед каждой установкой — чистый лог (не копим память между версиями)
        DebugLogService.Instance.Clear(
            $"=== Установка · {gameVersion.Id} · {variant.Kind} · {variant.DisplayName} ===");

        IsInstalling = true;
        InstallProgress = 0;
        DownloadBytesDone = 0;
        DownloadBytesTotal = 0;
        DownloadSpeedBps = 0;
        DownloadStage = "Подготовка";
        ErrorMessage = null;

        var progress = new Progress<InstallProgress>(p =>
        {
            // Progress может прийти с фонового потока
            Dispatcher.UIThread.Post(() =>
            {
                InstallProgress = p.Progress;
                DownloadBytesDone = p.BytesDownloaded;
                DownloadBytesTotal = p.TotalBytes;
                if (p.SpeedBytesPerSecond > 0)
                    DownloadSpeedBps = p.SpeedBytesPerSecond;
                if (!string.IsNullOrEmpty(p.Stage))
                    DownloadStage = p.Stage;
            });
        });

        try
        {
            await _installs
                .InstallAsync(gameVersion, variant, progress, token)
                .ConfigureAwait(true);

            // Если отменили в самый конец — не помечаем как успешную установку
            token.ThrowIfCancellationRequested();

            DebugLogService.Instance.Log("=== Установка завершена успешно ===");

            var shouldLaunch = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                InstallProgress = 1;
                IsInstalling = false;
                RefreshInstallState();
                RefreshVersionsInstalledFlags();
                SyncFullVariantsSelection();
                DownloadStage = "";
                DownloadSpeedBps = 0;
                shouldLaunch = LaunchAfterInstall && IsSelectedInstalled;
            });

            if (shouldLaunch)
                await LaunchGameAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (token.IsCancellationRequested || IsCancelException(ex))
        {
            DebugLogService.Instance.Log("=== Установка отменена пользователем ===");
            // Отмена пользователем — без красной ошибки
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsInstalling = false;
                InstallProgress = 0;
                DownloadStage = "";
                DownloadStatusReset();
                RefreshInstallState();
                ErrorMessage = null;
            });
        }
        catch (Exception ex)
        {
            var msg = ex is AggregateException agg
                ? string.Join(" | ", agg.Flatten().InnerExceptions.Select(e => e.Message).Take(3))
                : ex.Message;

            // Внутренние HttpRequestException часто в InnerException
            if (ex.InnerException is not null && msg.Length < 40)
                msg = $"{msg} ({ex.InnerException.Message})";

            DebugLogService.Instance.Log($"=== ОШИБКА установки: {msg} ===");
            if (!string.IsNullOrEmpty(ex.StackTrace))
                DebugLogService.Instance.Log(ex.StackTrace);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsInstalling = false;
                InstallProgress = 0;
                DownloadStage = "";
                DownloadStatusReset();
                RefreshInstallState();
                ErrorMessage = $"Ошибка установки: {msg}";
            });
        }
        finally
        {
            try { _installCts?.Dispose(); } catch { /* ignore */ }
            _installCts = null;
        }
    }

    private static bool IsCancelException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OperationCanceledException or TaskCanceledException)
                return true;
            if (ex is AggregateException agg)
            {
                var inners = agg.Flatten().InnerExceptions;
                if (inners.Count > 0 && inners.All(IsCancelExceptionLeaf))
                    return true;
            }

            // HttpClient / IO часто оборачивают отмену
            var msg = ex.Message ?? "";
            if (msg.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("отмен", StringComparison.OrdinalIgnoreCase))
                return true;

            ex = ex.InnerException;
        }

        return false;
    }

    private static bool IsCancelExceptionLeaf(Exception ex) =>
        ex is OperationCanceledException or TaskCanceledException;

    private void DownloadStatusReset()
    {
        DownloadBytesDone = 0;
        DownloadBytesTotal = 0;
        DownloadSpeedBps = 0;
        OnPropertyChanged(nameof(DownloadStatusText));
        OnPropertyChanged(nameof(InstallFillWidth));
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024)
            return $"{bytes:0} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024:0.0} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024 * 1024):0.0} MB";
        return $"{bytes / (1024 * 1024 * 1024):0.00} GB";
    }

    // Сначала анимация чекбокса, потом пересборка списка (чтобы UI не лагал)
    partial void OnShowSnapshotsChanged(bool value)
    {
        if (!_isRestoringSelection)
            ScheduleFilterApply();
    }

    partial void OnShowOldBetaChanged(bool value)
    {
        if (!_isRestoringSelection)
            ScheduleFilterApply();
    }

    partial void OnShowOldAlphaChanged(bool value)
    {
        if (!_isRestoringSelection)
            ScheduleFilterApply();
    }

    private void ScheduleFilterApply()
    {
        _filterApplyCts?.Cancel();
        _filterApplyCts?.Dispose();
        _filterApplyCts = new CancellationTokenSource();
        var token = _filterApplyCts.Token;
        _ = ApplyFilterAfterAnimationAsync(token);
    }

    private async Task ApplyFilterAfterAnimationAsync(CancellationToken token)
    {
        try
        {
            // Даём чекбоксу доиграть анимацию выбора
            await Task.Delay(FilterAnimationDelayMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                ApplyFilter(preserveSelection: true);
            });
        }
        catch (OperationCanceledException)
        {
            // следующий выбор фильтра отменил этот
        }
    }

    [RelayCommand]
    private async Task ReloadVersionsAsync()
    {
        await LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync(bool showBanner = true)
    {
        if (IsLoading)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            if (showBanner)
            {
                LoadingBannerVisible = true;
                LoadingOpacity = 1;
            }

            ErrorMessage = null;
        });

        try
        {
            var mojang = await _mojang.GetVersionsAsync().ConfigureAwait(false);
            var knownIds = new HashSet<string>(
                mojang.Select(v => v.Id),
                StringComparer.OrdinalIgnoreCase);
            var custom = _installs.DiscoverCustomVersions(knownIds);

            // Кастомные сверху, затем манифест Mojang
            var versions = custom.Concat(mojang).ToList();

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _allVersions = versions;
                ApplyFilter(preserveSelection: false);
                await RestoreExpandedSelectionAsync();
            });
        }
        catch (Exception ex)
        {
            // Даже без Mojang покажем локальные custom-версии
            try
            {
                var customOnly = _installs.DiscoverCustomVersions();
                if (customOnly.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        ErrorMessage = $"Mojang недоступен: {ex.Message}. Показаны локальные версии.";
                        _allVersions = customOnly;
                        ApplyFilter(preserveSelection: false);
                        await RestoreExpandedSelectionAsync();
                    });
                    return;
                }
            }
            catch
            {
                // fall through
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = $"Не удалось загрузить версии: {ex.Message}";
                _allVersions = Array.Empty<MinecraftVersion>();
                Versions.Clear();

                _suppressSettingsSave = true;
                SelectedVersion = null;
                SelectedVariant = null;
                _suppressSettingsSave = false;
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                LoadingOpacity = 0;
            });

            if (showBanner)
            {
                try
                {
                    await Task.Delay(300).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (LoadingOpacity <= 0.01)
                        LoadingBannerVisible = false;
                });
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => LoadingBannerVisible = false);
            }
        }
    }

    private async Task OnExpandRequestedAsync(MinecraftVersionItemViewModel item)
    {
        // Сначала мгновенно свернуть других (без анимации → без борьбы со скроллом)
        foreach (var other in Versions)
        {
            if (!ReferenceEquals(other, item))
                other.CollapseInstant();
        }

        // Дождаться layout после collapse, затем один раз поправить скролл
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        VersionExpanding?.Invoke(this, item);

        _suppressSettingsSave = true;
        SelectedVersion = item;
        _suppressSettingsSave = false;

        if (_settings.SelectedVersionId != item.Id)
        {
            _settings.SelectedVersionId = item.Id;
            _settingsService.Save(_settings);
        }

        // Раскрытие — с анимацией MaxHeight/Opacity (якорь скролла не трогаем во время анимации)
        await item.ExpandAsync().ConfigureAwait(true);

        item.PreferVariantKey(_settings.SelectedVariantKey);
        SelectedVariant = item.SelectedVariant
                          ?? item.Variants.FirstOrDefault()?.Model;

        if (SelectedVariant is not null)
            item.SelectedVariant = SelectedVariant;

        RefreshInstallState();
    }

    private void OnVariantSelected(MinecraftVersionItemViewModel item, VersionVariant variant)
    {
        foreach (var other in Versions)
            other.IsSelected = ReferenceEquals(other, item);

        _suppressSettingsSave = true;
        SelectedVersion = item;
        item.SelectedVariant = variant;
        SelectedVariant = variant;
        _suppressSettingsSave = false;

        _settings.SelectedVersionId = item.Id;
        _settings.SelectedVariantKey = variant.Key;
        _settingsService.Save(_settings);

        RefreshInstallState();
    }

    private void ApplyFilter(bool preserveSelection)
    {
        var preferredId = preserveSelection
            ? SelectedVersion?.Id ?? _settings.SelectedVersionId
            : _settings.SelectedVersionId ?? SelectedVersion?.Id;

        if (!preserveSelection && preferredId is not null)
            EnsureFilterForVersion(preferredId);

        var filtered = _allVersions.Where(MatchesFilter).ToList();

        // Один снимок установленных GameVersionId на весь список
        var installedGameIds = GetInstalledGameVersionIdSet();

        Versions.Clear();
        foreach (var version in filtered)
        {
            Versions.Add(new MinecraftVersionItemViewModel(
                version,
                _loaders,
                v => _installs.IsInstalled(v),
                id => installedGameIds.Contains(id),
                OnExpandRequestedAsync,
                OnVariantSelected));
        }

        _suppressSettingsSave = true;
        try
        {
            if (preferredId is not null)
            {
                SelectedVersion = Versions.FirstOrDefault(v => v.Id == preferredId)
                                  ?? Versions.FirstOrDefault();
            }
            else
            {
                SelectedVersion = Versions.FirstOrDefault();
            }

            if (SelectedVersion is not null &&
                _settings.SelectedVersionId != SelectedVersion.Id)
            {
                _settings.SelectedVersionId = SelectedVersion.Id;
                _settingsService.Save(_settings);
            }
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private async Task RestoreExpandedSelectionAsync()
    {
        if (SelectedVersion is null)
            return;

        foreach (var other in Versions)
        {
            if (!ReferenceEquals(other, SelectedVersion))
                other.CollapseInstant();
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        VersionExpanding?.Invoke(this, SelectedVersion);

        await SelectedVersion.ExpandAsync().ConfigureAwait(true);
        SelectedVersion.PreferVariantKey(_settings.SelectedVariantKey);

        _suppressSettingsSave = true;
        SelectedVariant = SelectedVersion.SelectedVariant
                          ?? SelectedVersion.Variants.FirstOrDefault()?.Model;
        if (SelectedVariant is not null)
            SelectedVersion.SelectedVariant = SelectedVariant;
        _suppressSettingsSave = false;

        RefreshInstallState();
    }

    private void EnsureFilterForVersion(string versionId)
    {
        var version = _allVersions.FirstOrDefault(v => v.Id == versionId);
        if (version is null || MatchesFilter(version))
            return;

        // release всегда вкл.; для остальных — включим нужный тип без отложенного Apply
        _isRestoringSelection = true;
        try
        {
            switch (version.Type)
            {
                case "snapshot":
                    ShowSnapshots = true;
                    break;
                case "old_beta":
                    ShowOldBeta = true;
                    break;
                case "old_alpha":
                    ShowOldAlpha = true;
                    break;
                case "custom":
                    // всегда видны
                    break;
            }
        }
        finally
        {
            _isRestoringSelection = false;
        }
    }

    private bool MatchesFilter(MinecraftVersion version) => version.Type switch
    {
        "release" => true, // релизы всегда видны
        "custom" => true,  // пользовательские папки из versions/
        "snapshot" => ShowSnapshots,
        "old_beta" => ShowOldBeta,
        "old_alpha" => ShowOldAlpha,
        _ => false
    };

    // ─── Обновления лаунчера (GitHub Releases) ─────────────────────────

    /// <summary>Фоновое сообщение об обновлении (поверх install status).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadStatus))]
    private string? _updateStatusText;

    [ObservableProperty]
    private bool _updateAvailable;

    public async Task CheckForUpdatesAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            log?.Invoke($"Проверка обновлений (v{AppInfo.Version})…");
            var result = await _updates.CheckAsync(ct).ConfigureAwait(false);
            _pendingUpdate = result;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.LatestVersion))
                {
                    UpdateAvailable = true;
                    UpdateStatusText = $"Доступно обновление v{result.LatestVersion}";
                }
                else
                {
                    UpdateAvailable = false;
                    // Не затираем статус установки пустым, если обновлений нет
                    if (UpdateStatusText is not null &&
                        UpdateStatusText.StartsWith("Доступно", StringComparison.Ordinal))
                        UpdateStatusText = null;
                }
            });

            log?.Invoke(result.UpdateAvailable
                ? $"Найдена версия {result.LatestVersion}"
                : result.Error is not null
                    ? $"Обновления: {result.Error}"
                    : "Установлена актуальная версия");
        }
        catch (Exception ex)
        {
            log?.Invoke("Обновления: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ApplyLauncherUpdateAsync()
    {
        var info = _pendingUpdate;
        if (info is null || !info.UpdateAvailable)
        {
            await CheckForUpdatesAsync().ConfigureAwait(true);
            info = _pendingUpdate;
        }

        if (info is null || !info.UpdateAvailable)
        {
            ErrorMessage = "Обновлений нет.";
            return;
        }

        try
        {
            IsInstalling = true;
            DownloadStage = "Обновление лаунчера";
            InstallProgress = 0;
            UpdateStatusText = "Принудительное обновление…";

            if (!string.IsNullOrWhiteSpace(info.PortableDownloadUrl))
            {
                var prog = new Progress<(double Progress, string Status)>(x =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        InstallProgress = x.Progress;
                        UpdateStatusText = x.Status;
                    });
                });
                // Exit внутри при успехе
                await _updates
                    .ApplyPortableUpdateAndRestartAsync(info.PortableDownloadUrl, prog)
                    .ConfigureAwait(true);
                return;
            }

            var setupUrl = info.SetupDownloadUrl;
            if (string.IsNullOrWhiteSpace(setupUrl))
            {
                LauncherUpdateService.OpenReleasesPage(info.ReleaseUrl);
                return;
            }

            var p2 = new Progress<double>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    InstallProgress = p;
                    UpdateStatusText = $"Скачивание Setup… {(int)(p * 100)}%";
                });
            });
            await _updates.DownloadAndRunSetupAsync(setupUrl, p2).ConfigureAwait(true);
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desk)
            {
                desk.Shutdown(0);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось обновить: " + ex.Message;
            LauncherUpdateService.OpenReleasesPage(info.ReleaseUrl);
        }
        finally
        {
            IsInstalling = false;
            InstallProgress = 0;
            DownloadStage = "";
        }
    }
}
