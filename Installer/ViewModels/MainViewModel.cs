using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZLauncher.Installer.Services;

namespace ZLauncher.Installer.ViewModels;

public enum InstallerStep
{
    Welcome,
    Options,
    Installing,
    Finished,
    Error
}

public partial class MainViewModel : ViewModelBase
{
    private readonly InstallEngine _engine = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome))]
    [NotifyPropertyChangedFor(nameof(IsOptions))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowBack))]
    [NotifyPropertyChangedFor(nameof(ShowCancel))]
    private InstallerStep _step = InstallerStep.Welcome;

    [ObservableProperty]
    private string _installPath = PayloadLocator.GetDefaultInstallDirectory();

    [ObservableProperty]
    private bool _createDesktopShortcut = true;

    [ObservableProperty]
    private bool _createStartMenuShortcut = true;

    [ObservableProperty]
    private bool _launchAfterInstall = true;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _detailText = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private string _payloadStatus = "";

    private PayloadLocator.PayloadSource _payloadSource =
        new(PayloadLocator.PayloadKind.None, null, "не найден");

    public bool IsWelcome => Step == InstallerStep.Welcome;
    public bool IsOptions => Step == InstallerStep.Options;
    public bool IsInstalling => Step == InstallerStep.Installing;
    public bool IsFinished => Step == InstallerStep.Finished;
    public bool IsError => Step == InstallerStep.Error;

    public bool CanGoBack => Step is InstallerStep.Options;
    public bool ShowBack => CanGoBack;
    public bool ShowCancel => Step is InstallerStep.Welcome or InstallerStep.Options or InstallerStep.Installing;
    public bool ShowPrimary => Step is not InstallerStep.Installing;

    public string PrimaryButtonText => Step switch
    {
        InstallerStep.Welcome => "Далее",
        InstallerStep.Options => "Установить",
        InstallerStep.Finished => "Готово",
        InstallerStep.Error => "Закрыть",
        _ => "Далее"
    };

    public string WelcomeTitle => "Установка ZLauncher";
    public string WelcomeBody =>
        "Добро пожаловать в мастер установки ZLauncher — лаунчера Minecraft.\n\n" +
        "Будут скопированы файлы приложения, созданы ярлыки и запись для удаления.";

    /// <summary>Выбор папки — окно подписывается и показывает picker.</summary>
    public event EventHandler? RequestBrowseFolder;
    public event EventHandler? RequestClose;

    public MainViewModel()
    {
        RefreshPayload();
    }

    public void RefreshPayload()
    {
        _payloadSource = PayloadLocator.Find();
        PayloadStatus = _payloadSource.Kind switch
        {
            PayloadLocator.PayloadKind.EmbeddedZip =>
                "Пакет лаунчера: встроен в установщик (один файл)",
            PayloadLocator.PayloadKind.ZipFile =>
                $"Пакет: {_payloadSource.Description}",
            PayloadLocator.PayloadKind.Directory =>
                $"Папка: {_payloadSource.Description}",
            _ =>
                "⚠ Пакет не найден. Запусти tools\\Pack-Payload.ps1 (создаст payload\\payload.zip)"
        };
    }

    [RelayCommand]
    private void Primary()
    {
        switch (Step)
        {
            case InstallerStep.Welcome:
                RefreshPayload();
                Step = InstallerStep.Options;
                break;
            case InstallerStep.Options:
                _ = StartInstallAsync();
                break;
            case InstallerStep.Finished:
            case InstallerStep.Error:
                RequestClose?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step == InstallerStep.Options)
            Step = InstallerStep.Welcome;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Step == InstallerStep.Installing)
        {
            _cts?.Cancel();
            StatusText = "Отмена…";
            return;
        }

        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Browse()
    {
        RequestBrowseFolder?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyPickedFolder(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            InstallPath = path;
    }

    private async Task StartInstallAsync()
    {
        RefreshPayload();
        if (_payloadSource.Kind == PayloadLocator.PayloadKind.None)
        {
            ErrorText = "Не найден пакет лаунчера.\n" +
                        "Собери payload.zip: tools\\Pack-Payload.ps1\n" +
                        "или положи ZLauncher-Portable на рабочий стол.";
            Step = InstallerStep.Error;
            return;
        }

        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            ErrorText = "Укажи папку установки.";
            Step = InstallerStep.Error;
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Step = InstallerStep.Installing;
        Progress = 0;
        StatusText = "Начало установки…";
        DetailText = "";
        ErrorText = "";

        var options = new InstallOptions
        {
            InstallDirectory = InstallPath.Trim(),
            CreateDesktopShortcut = CreateDesktopShortcut,
            CreateStartMenuShortcut = CreateStartMenuShortcut,
            LaunchAfterInstall = LaunchAfterInstall
        };

        var progress = new Progress<InstallProgressInfo>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Progress = p.Progress;
                StatusText = p.Stage;
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    DetailText = p.FilesTotal > 0
                        ? $"{p.FilesDone}/{p.FilesTotal}  ·  {p.CurrentFile}"
                        : p.CurrentFile!;
                else if (p.FilesTotal > 0)
                    DetailText = $"{p.FilesDone}/{p.FilesTotal}";
            });
        });

        try
        {
            await _engine.InstallAsync(_payloadSource, options, progress, token)
                .ConfigureAwait(true);

            token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Progress = 1;
                StatusText = "Установка завершена";
                DetailText = InstallPath;
                Step = InstallerStep.Finished;
            });

            if (LaunchAfterInstall)
            {
                try
                {
                    InstallEngine.LaunchLauncher(InstallPath.Trim());
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested ||
                                   ex is OperationCanceledException or TaskCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorText = "Установка отменена.";
                Step = InstallerStep.Error;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorText = ex.Message;
                Step = InstallerStep.Error;
            });
        }
        finally
        {
            try { _cts?.Dispose(); } catch { /* ignore */ }
            _cts = null;
        }
    }
}
