namespace PortViewer.Core.Models;

public sealed class FileLockHit
{
    public int ProcessId { get; set; }
    public long? StartTimeUtcTicks { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class FileLockScanResult
{
    public string TargetPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool HandleScanAvailable { get; set; }
    public bool IsPartial { get; set; }
    public int UnreadableHandles { get; set; }
    public string Error { get; set; } = string.Empty;
    public List<FileLockHit> Hits { get; set; } = new();

    public bool StillOwnedBy(int processId, long expectedStartTicks)
    {
        if (expectedStartTicks <= 0) return false;
        return Hits.Any(hit => hit.ProcessId == processId &&
            (!hit.StartTimeUtcTicks.HasValue || hit.StartTimeUtcTicks.Value == expectedStartTicks) &&
            PortViewer.Core.Services.FileLockPath.Matches(TargetPath, IsDirectory, hit.Path));
    }
}
