using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZLauncher.Services;

namespace ZLauncher.ViewModels;

/// <summary>
/// Экран загрузки: настройки → Java → версии → каталоги → скин → прогрев UI.
/// </summary>
public partial class SplashViewModel : ViewModelBase
{
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    private string _statusText = "Запуск…";

    [ObservableProperty]
    private double _progress; // 0..1

    public event EventHandler<MainViewModel>? Ready;

    public event EventHandler<string>? Failed;

    /// <summary>
    /// Опционально: прогреть MainWindow (создать, отложить layout), пока splash ещё виден.
    /// </summary>
    public Func<MainViewModel, IProgress<string>?, CancellationToken, Task>? WarmMainWindowAsync { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await StepAsync(0.04, "Инициализация…", cancellationToken).ConfigureAwait(true);
            await Task.Delay(120, cancellationToken).ConfigureAwait(true);

            // ── 0. Принудительное обновление (до UI) ─────────────
            await StepAsync(0.08, "Проверка обновлений…", cancellationToken).ConfigureAwait(true);
            await ForceUpdateIfNeededAsync(
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                p => Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Status;
                    Progress = Math.Clamp(0.08 + p.Progress * 0.25, 0.08, 0.32);
                }),
                cancellationToken).ConfigureAwait(true);

            // ── 1. Настройки / VM ─────────────────────────────────
            await StepAsync(0.34, "Загрузка настроек…", cancellationToken).ConfigureAwait(true);
            var vm = await Dispatcher.UIThread.InvokeAsync(() => new MainViewModel(deferInit: true));
            await vm.InitializeCoreAsync(
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                cancellationToken).ConfigureAwait(true);
            await StepAsync(0.38, "Настройки готовы", cancellationToken).ConfigureAwait(true);

            // ── 2. Каталоги игры ──────────────────────────────────
            await StepAsync(0.42, "Проверка каталогов…", cancellationToken).ConfigureAwait(true);
            await EnsureGameFoldersAsync(msg => Dispatcher.UIThread.Post(() => AppendLog(msg)), cancellationToken)
                .ConfigureAwait(true);

            // ── 3. Java (лёгкая проверка, без полной загрузки) ────
            await StepAsync(0.48, "Проверка Java…", cancellationToken).ConfigureAwait(true);
            await ProbeJavaAsync(msg => Dispatcher.UIThread.Post(() => AppendLog(msg)), cancellationToken)
                .ConfigureAwait(true);

            // ── 4. Версии Minecraft ───────────────────────────────
            await StepAsync(0.56, "Загрузка списка версий…", cancellationToken).ConfigureAwait(true);
            await vm.InitializeVersionsAsync(
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                cancellationToken).ConfigureAwait(true);

            await StepAsync(0.68, "Установленные сборки…", cancellationToken).ConfigureAwait(true);
            await CountInstalledAsync(vm, msg => Dispatcher.UIThread.Post(() => AppendLog(msg)), cancellationToken)
                .ConfigureAwait(true);

            // ── 5. Скин / UI-данные ───────────────────────────────
            await StepAsync(0.78, "Подготовка интерфейса…", cancellationToken).ConfigureAwait(true);
            await vm.InitializeUiAsync(
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                cancellationToken).ConfigureAwait(true);

            // ── 6. Прогрев окна (XAML, layout, стили) ─────────────
            await StepAsync(0.88, "Сборка окна лаунчера…", cancellationToken).ConfigureAwait(true);
            if (WarmMainWindowAsync is not null)
            {
                var warmLog = new Progress<string>(msg =>
                    Dispatcher.UIThread.Post(() => AppendLog(msg)));
                await WarmMainWindowAsync(vm, warmLog, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                AppendLog("Прогрев окна: пропущен");
            }

            await StepAsync(0.94, "Почти готово…", cancellationToken).ConfigureAwait(true);
            await Task.Delay(160, cancellationToken).ConfigureAwait(true);

            await StepAsync(1.0, "Запуск лаунчера…", cancellationToken).ConfigureAwait(true);
            AppendLog("Готово.");
            await Task.Delay(120, cancellationToken).ConfigureAwait(true);

            Ready?.Invoke(this, vm);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Отменено.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            Failed?.Invoke(this, ex.Message);
        }
    }

    private static async Task EnsureGameFoldersAsync(Action<string> log, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ZLauncher");

            var dirs = new[]
            {
                root,
                Path.Combine(root, "game"),
                Path.Combine(root, "game", "mods"),
                Path.Combine(root, "game", "resourcepacks"),
                Path.Combine(root, "game", "shaderpacks"),
                Path.Combine(root, "game", "logs"),
                Path.Combine(root, "versions"),
                Path.Combine(root, "libraries"),
                Path.Combine(root, "assets"),
                Path.Combine(root, "runtime"),
                Path.Combine(root, "skins"),
            };

            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(d);
            }

            log($"Каталоги: {dirs.Length} OK");
        }, ct).ConfigureAwait(false);
    }

    private static async Task ProbeJavaAsync(Action<string> log, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var java = new JavaRuntimeService();
                var path = java.FindJavaExecutable()
                           ?? java.FindJavaExecutable(JavaRuntimeService.ComponentJava21)
                           ?? java.FindJavaExecutable(JavaRuntimeService.ComponentJava17)
                           ?? java.FindJavaExecutable(JavaRuntimeService.ComponentJava8);

                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var name = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
                    log($"Java: найдена ({name})");
                }
                else
                {
                    log("Java: будет скачана при установке/запуске");
                }
            }
            catch (Exception ex)
            {
                log("Java: " + ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    private static async Task CountInstalledAsync(
        MainViewModel vm,
        Action<string> log,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var installs = new GameInstallService();
                var builds = installs.ListInstalledBuilds();
                log(builds.Count > 0
                    ? $"Установлено сборок: {builds.Count}"
                    : "Установленных сборок пока нет");

                // Моды / ресурспаки / шейдеры — только счётчики
                var mods = CountJars(ModrinthService.GetModsDirectory());
                var rps = CountPacks(ModrinthService.GetResourcePacksDirectory());
                var sh = CountPacks(ModrinthService.GetShaderPacksDirectory());
                if (mods + rps + sh > 0)
                    log($"Контент: моды {mods} · RP {rps} · шейдеры {sh}");
            }
            catch (Exception ex)
            {
                log("Сборки: " + ex.Message);
            }
        }, ct).ConfigureAwait(false);

        // Прогреть выбранную сборку (флаги «установлено» на UI)
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => vm.RefreshInstallStatePublic());
        }
        catch
        {
            // ignore
        }

        await Task.Delay(60, ct).ConfigureAwait(false);
    }

    private static int CountJars(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.jar", SearchOption.TopDirectoryOnly).Length;
        }
        catch { return 0; }
    }

    private static int CountPacks(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f =>
                {
                    var e = Path.GetExtension(f);
                    return e.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                           || e.Equals(".jar", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch { return 0; }
    }

    /// <summary>
    /// Если на GitHub версия новее — скачиваем Portable и перезапускаемся.
    /// Без сети / без релиза — продолжаем (не блокируем офлайн).
    /// </summary>
    private static async Task ForceUpdateIfNeededAsync(
        Action<string> log,
        Action<(double Progress, string Status)> ui,
        CancellationToken ct)
    {
        var updates = new LauncherUpdateService();
        log($"Версия лаунчера: {AppInfo.Version}");
        ui((0, "Запрос GitHub releases/latest…"));

        var check = await updates.CheckAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(check.Error))
        {
            log("Обновления: " + check.Error + " (продолжаем)");
            return;
        }

        if (!check.UpdateAvailable)
        {
            log(string.IsNullOrEmpty(check.LatestVersion)
                ? "Обновлений нет"
                : $"Актуальная версия (v{check.LatestVersion})");
            return;
        }

        log($"Найдена v{check.LatestVersion} — принудительное обновление");
        ui((0.05, $"Обновление до v{check.LatestVersion}…"));

        var url = check.PortableDownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // Setup теперь stub — предпочитаем portable; иначе setup
            url = check.SetupDownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException(
                    $"Доступна v{check.LatestVersion}, но нет файла обновления в релизе.");

            log("Portable нет — запуск Setup…");
            var setupProg = new Progress<double>(p => ui((p, "Скачивание Setup…")));
            await updates.DownloadAndRunSetupAsync(url, setupProg, ct)
                .ConfigureAwait(false);
            Environment.Exit(0);
            return;
        }

        var prog = new Progress<(double Progress, string Status)>(x =>
        {
            ui(x);
            log(x.Status);
        });

        // Не возвращается при успехе (Exit)
        await updates.ApplyPortableUpdateAndRestartAsync(url, prog, ct).ConfigureAwait(false);
    }

    private async Task StepAsync(double progress, string status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Progress = progress;
            StatusText = status;
            AppendLog(status);
        });
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        void Add()
        {
            LogLines.Add(line.Trim());
            while (LogLines.Count > 14)
                LogLines.RemoveAt(0);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Add();
        else
            Dispatcher.UIThread.Post(Add);
    }
}
