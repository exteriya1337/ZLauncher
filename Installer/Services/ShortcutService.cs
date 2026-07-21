using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ZLauncher.Installer.Services;

/// <summary>Создание .lnk через WScript.Shell COM.</summary>
public static class ShortcutService
{
    public static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string? workingDirectory = null,
        string? description = null,
        string? iconPath = null)
    {
        var dir = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell недоступен.");

        dynamic shell = Activator.CreateInstance(shellType)
                        ?? throw new InvalidOperationException("Не удалось создать WScript.Shell.");

        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(targetPath) ?? "";
            if (!string.IsNullOrEmpty(description))
                shortcut.Description = description;
            shortcut.IconLocation = !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)
                ? iconPath
                : targetPath;
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static string DesktopShortcutPath(string name = "ZLauncher.lnk")
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, name);
    }

    public static string StartMenuShortcutPath(string name = "ZLauncher.lnk")
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var folder = Path.Combine(programs, "ZLauncher");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }
}
