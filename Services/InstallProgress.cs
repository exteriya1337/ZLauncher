namespace ZLauncher.Services;

public sealed class InstallProgress
{
    public double Progress { get; init; }
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public string Stage { get; init; } = "";
    public string? CurrentFile { get; init; }
}
