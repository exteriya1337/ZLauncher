using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ZLauncher.Installer.Services;

/// <summary>
/// Источник файлов лаунчера: встроенный zip → payload.zip рядом → папка → Portable.
/// </summary>
public static class PayloadLocator
{
    public const string ExeName = "ZLauncher.exe";
    public const string ZipFileName = "payload.zip";
    public const string EmbeddedResourceName = "payload.zip";

    public enum PayloadKind
    {
        None,
        EmbeddedZip,
        ZipFile,
        Directory
    }

    public sealed record PayloadSource(PayloadKind Kind, string? Path, string Description);

    public static PayloadSource Find()
    {
        // 1) Вшитый в exe zip (релиз single-file)
        if (HasEmbeddedZip())
            return new PayloadSource(PayloadKind.EmbeddedZip, null, "встроено в установщик");

        var baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 2) payload.zip рядом с Setup
        foreach (var zip in GetZipCandidates(baseDir))
        {
            if (File.Exists(zip) && new FileInfo(zip).Length > 1000)
                return new PayloadSource(PayloadKind.ZipFile, zip, zip);
        }

        // 3) Папка с ZLauncher.exe
        foreach (var dir in GetDirectoryCandidates(baseDir))
        {
            if (IsValidDirectory(dir))
                return new PayloadSource(PayloadKind.Directory, dir, dir);
        }

        return new PayloadSource(PayloadKind.None, null, "не найден");
    }

    public static bool HasEmbeddedZip()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Any(n => n.Equals(EmbeddedResourceName, StringComparison.OrdinalIgnoreCase) ||
                      n.EndsWith("." + EmbeddedResourceName, StringComparison.OrdinalIgnoreCase) ||
                      n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
    }

    public static Stream? OpenEmbeddedZip()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n =>
                n.Equals(EmbeddedResourceName, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("." + EmbeddedResourceName, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    public static bool IsValidDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;
        return File.Exists(Path.Combine(dir, ExeName));
    }

    public static string GetDefaultInstallDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Programs", "ZLauncher");
    }

    private static IEnumerable<string> GetZipCandidates(string baseDir)
    {
        yield return Path.Combine(baseDir, ZipFileName);
        yield return Path.Combine(baseDir, "payload", ZipFileName);

        var parent = Directory.GetParent(baseDir)?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            yield return Path.Combine(parent, ZipFileName);
            yield return Path.Combine(parent, "payload", ZipFileName);
        }

        // Dev: payload рядом с исходниками (когда BaseDirectory = bin\...)
        string? dev = null;
        try
        {
            dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "payload", ZipFileName));
        }
        catch
        {
            // ignore
        }

        if (!string.IsNullOrEmpty(dev))
            yield return dev;
    }

    private static IEnumerable<string> GetDirectoryCandidates(string baseDir)
    {
        yield return Path.Combine(baseDir, "payload");
        yield return baseDir;

        var parent = Directory.GetParent(baseDir)?.FullName;
        if (!string.IsNullOrEmpty(parent))
            yield return Path.Combine(parent, "payload");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(desktop, "ZLauncher-Portable");
    }
}
