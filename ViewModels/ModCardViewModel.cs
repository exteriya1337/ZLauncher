using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZLauncher.Models;
using ZLauncher.Services;

namespace ZLauncher.ViewModels;

public partial class ModCardViewModel : ViewModelBase
{
    private readonly ModrinthService _modrinth;
    private readonly TranslationService _translator;
    private readonly Func<(string? gameVersion, string? loader)> _resolveInstallContext;
    private CancellationTokenSource? _enrichCts;
    private CancellationTokenSource? _installCts;
    private int _descAnimGen;

    public ModrinthModInfo Model { get; }

    public string Title => Model.Title;
    public string Author => Model.Author;
    public string PageUrl => Model.PageUrl;
    public string DownloadsText => FormatCount(Model.Downloads);
    public string DownloadsLabel => "↓ " + DownloadsText;
    public bool HasIcon => Icon is not null;
    public bool IsResourcePack => Model.IsResourcePack;
    public bool IsShader => Model.IsShader;

    /// <summary>Буква-заглушка: M = мод, R = ресурспак, S = шейдер.</summary>
    public string TypeGlyph =>
        IsShader ? "S" : IsResourcePack ? "R" : "M";

    public string CategoriesText
    {
        get
        {
            if (Model.Categories.Count == 0)
                return "";
            return string.Join(" · ", Model.Categories);
        }
    }

    public string GameVersionHint =>
        !string.IsNullOrWhiteSpace(Model.LatestVersion)
            ? Model.LatestVersion!
            : "";

    [ObservableProperty]
    private string _descriptionRu = "";

    /// <summary>Прозрачность описания (fade при смене перевода).</summary>
    [ObservableProperty]
    private double _descriptionOpacity = 1;

    [ObservableProperty]
    private bool _isDescriptionLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _isIconLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallIconGlyph))]
    [NotifyPropertyChangedFor(nameof(InstallTip))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallIconGlyph))]
    [NotifyPropertyChangedFor(nameof(InstallTip))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalled;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string? _installStatus;

    public bool CanInstall => !IsInstalling;

    /// <summary>⬇ установка · … прогресс · ✓ готово</summary>
    public string InstallIconGlyph => IsInstalled && !IsInstalling ? "✓" : IsInstalling ? "…" : "⬇";

    public string InstallTip
    {
        get
        {
            if (IsInstalling)
                return string.IsNullOrWhiteSpace(InstallStatus) ? "Установка…" : InstallStatus!;

            if (IsInstalled)
            {
                if (IsShader)
                    return "Установлен в shaderpacks (нажмите, чтобы переустановить)";
                if (IsResourcePack)
                    return "Установлен в resourcepacks (нажмите, чтобы переустановить)";
                return "Установлен в папку mods (нажмите, чтобы переустановить)";
            }

            if (IsShader)
                return "Установить в папку shaderpacks";
            if (IsResourcePack)
                return "Установить в папку resourcepacks";
            return "Установить в папку mods";
        }
    }

    public ModCardViewModel(
        ModrinthModInfo model,
        ModrinthService modrinth,
        TranslationService translator,
        Func<(string? gameVersion, string? loader)> resolveInstallContext)
    {
        Model = model;
        _modrinth = modrinth;
        _translator = translator;
        _resolveInstallContext = resolveInstallContext;
        DescriptionRu = string.IsNullOrWhiteSpace(model.Description)
            ? "Описание отсутствует"
            : model.Description;
        DescriptionOpacity = 1;

        try
        {
            if (IsShader)
            {
                IsInstalled = _modrinth.IsShaderInstalled(model.ProjectId)
                              || _modrinth.IsShaderInstalled(model.Slug);
            }
            else if (IsResourcePack)
            {
                IsInstalled = _modrinth.IsResourcePackInstalled(model.ProjectId)
                              || _modrinth.IsResourcePackInstalled(model.Slug);
            }
            else
            {
                IsInstalled = _modrinth.IsModInstalled(model.ProjectId)
                              || _modrinth.IsModInstalled(model.Slug);
            }
        }
        catch
        {
            IsInstalled = false;
        }
    }

    public void StartEnrich(CancellationToken parentToken)
    {
        _enrichCts?.Cancel();
        _enrichCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var token = _enrichCts.Token;
        _ = EnrichAsync(token);
    }

    private async Task EnrichAsync(CancellationToken token)
    {
        var iconTask = LoadIconAsync(token);
        var descTask = TranslateAsync(token);
        await Task.WhenAll(iconTask, descTask).ConfigureAwait(false);
    }

    private async Task LoadIconAsync(CancellationToken token)
    {
        try
        {
            var bmp = await _modrinth.LoadIconAsync(Model.IconUrl, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                bmp?.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    bmp?.Dispose();
                    return;
                }

                Icon?.Dispose();
                Icon = bmp;
                IsIconLoading = false;
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsIconLoading = false);
        }
    }

    private async Task TranslateAsync(CancellationToken token)
    {
        try
        {
            var source = Model.Description?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(source))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    DescriptionRu = "Описание отсутствует";
                    DescriptionOpacity = 1;
                    IsDescriptionLoading = false;
                });
                return;
            }

            // Уже по-русски / короткое локальное — без анимации
            if (LooksMostlyRussian(source) || source.StartsWith("Установлен", StringComparison.Ordinal))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    DescriptionRu = source;
                    DescriptionOpacity = 1;
                    IsDescriptionLoading = false;
                });
                return;
            }

            var ru = await _translator
                .ToRussianAsync(source, token)
                .ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return;

            var target = string.IsNullOrWhiteSpace(ru) ? source : ru.Trim();
            await AnimateDescriptionToAsync(target, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDescriptionLoading = false;
                DescriptionOpacity = 1;
            });
        }
    }

    /// <summary>
    /// Плавная замена описания: fade-out → посимвольный набор перевода → fade-in.
    /// </summary>
    private async Task AnimateDescriptionToAsync(string target, CancellationToken token)
    {
        var gen = ++_descAnimGen;
        var current = DescriptionRu ?? "";

        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            IsDescriptionLoading = false;
            DescriptionOpacity = 1;
            return;
        }

        // 1) Плавно гасим английский
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != _descAnimGen || token.IsCancellationRequested) return;
            DescriptionOpacity = 0;
        });

        try
        {
            await Task.Delay(160, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (gen != _descAnimGen || token.IsCancellationRequested)
            return;

        // 2) Начинаем с пустой строки и набираем перевод
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != _descAnimGen || token.IsCancellationRequested) return;
            DescriptionRu = "";
            DescriptionOpacity = 1;
            IsDescriptionLoading = false;
        });

        // Длинные тексты — крупнее шаг, чтобы не тянуть UI
        var step = target.Length > 160 ? 4 : target.Length > 80 ? 3 : 2;
        var delayMs = target.Length > 160 ? 8 : 10;

        for (var i = 0; i < target.Length; i += step)
        {
            if (gen != _descAnimGen || token.IsCancellationRequested)
                return;

            var end = Math.Min(i + step, target.Length);
            var slice = target[..end];
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != _descAnimGen || token.IsCancellationRequested) return;
                DescriptionRu = slice;
            });

            try
            {
                await Task.Delay(delayMs, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (gen != _descAnimGen || token.IsCancellationRequested)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != _descAnimGen) return;
            DescriptionRu = target;
            DescriptionOpacity = 1;
            IsDescriptionLoading = false;
        });
    }

    /// <summary>Грубая эвристика: текст уже на кириллице — не гоняем через переводчик.</summary>
    private static bool LooksMostlyRussian(string text)
    {
        var letters = 0;
        var cyr = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                letters++;
                if (ch is >= 'А' and <= 'я' or 'ё' or 'Ё')
                    cyr++;
            }
        }

        return letters > 0 && cyr >= letters * 0.45;
    }

    [RelayCommand]
    private void OpenOnModrinth()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = PageUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallModAsync()
    {
        if (IsInstalling)
            return;

        _installCts?.Cancel();
        _installCts = new CancellationTokenSource();
        var token = _installCts.Token;

        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = "Поиск версии…";
        InstallModCommand.NotifyCanExecuteChanged();

        try
        {
            var (gameVersion, loader) = _resolveInstallContext();
            var id = !string.IsNullOrWhiteSpace(Model.ProjectId) ? Model.ProjectId : Model.Slug;

            var progress = new Progress<double>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    InstallProgress = p;
                    InstallStatus = p >= 1 ? "Готово" : $"Скачивание {(int)(p * 100)}%";
                });
            });

            ModInstallResult result;
            if (IsShader)
            {
                result = await _modrinth
                    .InstallShaderAsync(id, gameVersion, progress, token)
                    .ConfigureAwait(true);
            }
            else if (IsResourcePack)
            {
                result = await _modrinth
                    .InstallResourcePackAsync(id, gameVersion, progress, token)
                    .ConfigureAwait(true);
            }
            else
            {
                result = await _modrinth
                    .InstallModAsync(id, gameVersion, loader, progress, token)
                    .ConfigureAwait(true);
            }

            IsInstalled = true;
            InstallProgress = 1;
            InstallStatus = $"Установлен: {result.FileName}";
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "Отменено";
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Length > 120)
                msg = msg[..117] + "…";
            InstallStatus = msg;
        }
        finally
        {
            IsInstalling = false;
            InstallModCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(InstallTip));
            OnPropertyChanged(nameof(InstallIconGlyph));
        }
    }

    public void DisposeResources()
    {
        _enrichCts?.Cancel();
        _installCts?.Cancel();
        _descAnimGen++;
        Icon?.Dispose();
        Icon = null;
    }

    private static string FormatCount(long n)
    {
        if (n >= 1_000_000)
            return (n / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000)
            return (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return n.ToString(CultureInfo.InvariantCulture);
    }
}
