namespace PortViewer.Core.Models;

public sealed class ProcessInfoRecord
{
    public int ProcessId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public long? StartTimeUtcTicks { get; init; }
}
