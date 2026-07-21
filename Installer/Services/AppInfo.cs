namespace ZLauncher.Installer.Services;

/// <summary>Синхронизируй с ZLauncher.Services.AppInfo при релизе.</summary>
public static class AppInfo
{
    public const string Version = "1.0.1";
    public const string ProductName = "ZLauncher";
    public const string GitHubOwner = "exteriya1337";
    public const string GitHubRepo = "ZLauncher";

    public static string ReleasesPageUrl =>
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
}
