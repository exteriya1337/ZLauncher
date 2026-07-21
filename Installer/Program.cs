using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;

namespace ZLauncher.Installer;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            try
            {
                var log = Path.Combine(
                    Path.GetTempPath(),
                    "ZLauncher.Setup.error.txt");
                File.WriteAllText(log, ex.ToString());
                MessageBoxW(
                    IntPtr.Zero,
                    "ZLauncher Setup не смог запуститься:\n\n" + ex.Message +
                    "\n\nПодробности: " + log,
                    "ZLauncher Setup",
                    0x00000010 /* MB_ICONERROR */);
            }
            catch
            {
                // ignore
            }

            Environment.Exit(1);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}