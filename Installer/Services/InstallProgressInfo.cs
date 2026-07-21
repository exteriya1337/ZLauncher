namespace ZLauncher.Installer.Services;

public sealed class InstallProgressInfo
{
    public double Progress { get; init; }
    public string Stage { get; init; } = "";
    public string? CurrentFile { get; init; }
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
}
