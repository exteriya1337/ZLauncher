using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZLauncher.Installer.Services;

/// <summary>
/// Распаковывает payload (zip / папка), ярлыки, uninstaller.
/// </summary>
public sealed class InstallEngine
{
    public const string ProductName = "ZLauncher";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZLauncher";

    public async Task InstallAsync(
        PayloadLocator.PayloadSource source,
        InstallOptions options,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken ct)
    {
        if (source.Kind == PayloadLocator.PayloadKind.None)
            throw new InvalidOperationException("Файлы лаунчера не найдены (payload).");

        var target = options.InstallDirectory.Trim();
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Не указана папка установки.");

        Directory.CreateDirectory(target);

        Report(progress, 0.02, "Подготовка", null, 0, 0);

        int fileCount;
        switch (source.Kind)
        {
            case PayloadLocator.PayloadKind.EmbeddedZip:
                fileCount = await ExtractEmbeddedZipAsync(target, progress, ct).ConfigureAwait(false);
                break;
            case PayloadLocator.PayloadKind.ZipFile:
                fileCount = await ExtractZipFileAsync(source.Path!, target, progress, ct)
                    .ConfigureAwait(false);
                break;
            case PayloadLocator.PayloadKind.Directory:
                fileCount = await CopyDirectoryAsync(source.Path!, target, progress, ct)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Неизвестный тип payload.");
        }

        Report(progress, 0.88, "Ярлыки", null, fileCount, fileCount);

        var exePath = Path.Combine(target, PayloadLocator.ExeName);
        if (!File.Exists(exePath))
            throw new InvalidOperationException($"После установки нет {PayloadLocator.ExeName}.");

        var iconPath = Path.Combine(target, "ZLauncher.ico");
        if (!File.Exists(iconPath))
            iconPath = exePath;

        if (options.CreateDesktopShortcut)
        {
            ShortcutService.CreateShortcut(
                ShortcutService.DesktopShortcutPath(),
                exePath, target, "ZLauncher — Minecraft launcher", iconPath);
        }

        if (options.CreateStartMenuShortcut)
        {
            ShortcutService.CreateShortcut(
                ShortcutService.StartMenuShortcutPath(),
                exePath, target, "ZLauncher — Minecraft launcher", iconPath);
        }

        Report(progress, 0.93, "Деинсталлятор", null, fileCount, fileCount);
        WriteUninstaller(target);
        WriteInstallMeta(target, options, fileCount);

        Report(progress, 0.97, "Реестр", null, fileCount, fileCount);
        try { WriteUninstallRegistry(target, exePath); }
        catch { /* HKCU optional */ }

        Report(progress, 1, "Готово", null, fileCount, fileCount);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public static void LaunchLauncher(string installDir)
    {
        var exe = Path.Combine(installDir, PayloadLocator.ExeName);
        if (!File.Exists(exe)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = installDir,
            UseShellExecute = true
        });
    }

    private static async Task<int> ExtractEmbeddedZipAsync(
        string target,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken ct)
    {
        await using var stream = PayloadLocator.OpenEmbeddedZip()
            ?? throw new InvalidOperationException("Встроенный payload.zip не найден.");

        // Копируем во временный файл — ZipArchive лучше работает с seekable stream
        var temp = Path.Combine(Path.GetTempPath(), $"zlauncher_payload_{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                Report(progress, 0.05, "Чтение пакета", null, 0, 0);
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            return await ExtractZipFileAsync(temp, target, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private static async Task<int> ExtractZipFileAsync(
        string zipPath,
        string target,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken ct)
    {
        await Task.Yield();

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Where(e => !e.FullName.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (entries.Length == 0)
            throw new InvalidOperationException("payload.zip пуст.");

        var done = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            // защита от zip-slip
            var dest = Path.GetFullPath(Path.Combine(target, rel));
            if (!dest.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Некорректный путь в архиве: " + entry.FullName);

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            entry.ExtractToFile(dest, overwrite: true);

            done++;
            var p = 0.08 + 0.78 * done / entries.Length;
            Report(progress, p, "Распаковка", rel, done, entries.Length);

            // не блокируем UI на тысячах мелких файлов
            if (done % 20 == 0)
                await Task.Yield();
        }

        return entries.Length;
    }

    private static async Task<int> CopyDirectoryAsync(
        string sourceDir,
        string target,
        IProgress<InstallProgressInfo>? progress,
        CancellationToken ct)
    {
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !name.Equals("README.txt", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals(PayloadLocator.ZipFileName, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        if (files.Length == 0)
            throw new InvalidOperationException("Папка payload пуста.");

        var done = 0;
        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceDir, src);
            var dest = Path.Combine(target, rel);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);

            done++;
            Report(progress, 0.08 + 0.78 * done / files.Length, "Копирование", rel, done, files.Length);
        }

        return files.Length;
    }

    private static void WriteUninstaller(string installDir)
    {
        var desktop = ShortcutService.DesktopShortcutPath();
        var startMenu = ShortcutService.StartMenuShortcutPath();
        var startMenuDir = Path.GetDirectoryName(startMenu) ?? "";

        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("chcp 65001 >nul");
        bat.AppendLine("echo Removing ZLauncher...");
        bat.AppendLine($"if exist \"{desktop}\" del /f /q \"{desktop}\"");
        bat.AppendLine($"if exist \"{startMenu}\" del /f /q \"{startMenu}\"");
        bat.AppendLine($"if exist \"{startMenuDir}\" rd \"{startMenuDir}\" 2>nul");
        bat.AppendLine(
            @"reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZLauncher"" /f >nul 2>&1");
        bat.AppendLine("cd /d \"%TEMP%\"");
        bat.AppendLine($"start \"\" cmd /c \"timeout /t 1 /nobreak >nul & rd /s /q \"{installDir}\"\"");
        bat.AppendLine("exit");

        File.WriteAllText(Path.Combine(installDir, "Uninstall.bat"), bat.ToString(), Encoding.UTF8);
    }

    private static void WriteInstallMeta(string installDir, InstallOptions options, int fileCount)
    {
        var meta = new
        {
            Product = ProductName,
            Version = "1.0.0",
            InstalledAt = DateTime.UtcNow.ToString("o"),
            InstallDirectory = installDir,
            FileCount = fileCount,
            DesktopShortcut = options.CreateDesktopShortcut,
            StartMenuShortcut = options.CreateStartMenuShortcut
        };
        File.WriteAllText(
            Path.Combine(installDir, "install.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static void WriteUninstallRegistry(string installDir, string exePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
        if (key is null) return;

        var uninstallBat = Path.Combine(installDir, "Uninstall.bat");
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("Publisher", "ZLauncher");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{uninstallBat}\"");
        key.SetValue("DisplayVersion", "1.0.0");
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(installDir), Microsoft.Win32.RegistryValueKind.DWord);
    }

    private static int EstimateSizeKb(string dir)
    {
        try
        {
            long bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch { return 0; }
    }

    private static void Report(
        IProgress<InstallProgressInfo>? progress,
        double p, string stage, string? file, int done, int total)
    {
        progress?.Report(new InstallProgressInfo
        {
            Progress = Math.Clamp(p, 0, 1),
            Stage = stage,
            CurrentFile = file,
            FilesDone = done,
            FilesTotal = total
        });
    }
}
