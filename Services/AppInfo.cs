namespace ZLauncher.Services;

/// <summary>
/// Версия приложения и координаты GitHub-релизов.
/// При релизе меняй <see cref="Version"/> и тег vX.Y.Z.
/// </summary>
public static class AppInfo
{
    /// <summary>Семантическая версия лаунчера (совпадает с AssemblyVersion / git tag без v).</summary>
    public const string Version = "1.0.3";

    public const string ProductName = "ZLauncher";

    public const string GitHubOwner = "exteriya1337";
    public const string GitHubRepo = "ZLauncher";

    /// <summary>Лендинг (GitHub Pages).</summary>
    public const string WebsiteUrl = "https://exteriya1337.github.io/ZLauncher/";

    public static string ReleasesApiUrl =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string ReleasesPageUrl =>
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string RepoUrl =>
        $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    /// <summary>Имя ассета установщика в релизе.</summary>
    public const string SetupAssetName = "ZLauncher.Setup.exe";

    /// <summary>Имя portable-zip в релизе.</summary>
    public const string PortableAssetName = "ZLauncher-Portable.zip";
}
