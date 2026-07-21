using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZLauncher.ViewModels;
using ZLauncher.Views;

namespace ZLauncher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Пока живёт splash — не гасим процесс при его закрытии
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splashVm = new SplashViewModel();
            var splash = new SplashWindow { DataContext = splashVm };

            MainWindow? warmedMain = null;

            // Прогрев MainWindow во время splash: XAML, стили, layout
            splashVm.WarmMainWindowAsync = async (mainVm, log, ct) =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    log?.Report("Создание MainWindow…");
                    warmedMain = new MainWindow
                    {
                        DataContext = mainVm,
                        Opacity = 0,
                        ShowInTaskbar = false
                    };
                    // Ещё не MainWindow lifetime — splash остаётся «главным» для пользователя
                    warmedMain.Show();
                });

                // Несколько кадров layout/render, чтобы Styles + вкладки «прогрелись»
                log?.Report("Компоновка интерфейса…");
                for (var i = 0; i < 4; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Task.Delay(40, ct).ConfigureAwait(true);
                }

                log?.Report("Интерфейс прогрет");
            };

            void RevealMain(MainViewModel mainVm)
            {
                try
                {
                    MainWindow main;
                    if (warmedMain is not null &&
                        ReferenceEquals(warmedMain.DataContext, mainVm))
                    {
                        main = warmedMain;
                        warmedMain = null;
                    }
                    else
                    {
                        // fallback: окно ещё не создано
                        try { warmedMain?.Close(); } catch { /* ignore */ }
                        warmedMain = null;
                        main = new MainWindow { DataContext = mainVm };
                        main.Show();
                    }

                    desktop.MainWindow = main;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                    main.ShowInTaskbar = true;
                    main.Opacity = 1;
                    main.Activate();

                    try { splash.Close(); } catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    try
                    {
                        Console.Error.WriteLine("MainWindow failed: " + ex);
                    }
                    catch { /* ignore */ }

                    desktop.Shutdown(1);
                }
            }

            splashVm.Ready += (_, mainVm) =>
            {
                Dispatcher.UIThread.Post(() => RevealMain(mainVm), DispatcherPriority.Normal);
            };

            splashVm.Failed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        try { warmedMain?.Close(); } catch { /* ignore */ }
                        warmedMain = null;
                        RevealMain(new MainViewModel(deferInit: false));
                    }
                    catch
                    {
                        desktop.Shutdown(1);
                    }
                }, DispatcherPriority.Normal);
            };

            desktop.MainWindow = splash;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
