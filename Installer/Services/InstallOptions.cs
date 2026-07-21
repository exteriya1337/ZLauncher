namespace ZLauncher.Installer.Services;

public sealed class InstallOptions
{
    public string InstallDirectory { get; set; } = "";
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
}
