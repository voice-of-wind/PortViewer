using System.Diagnostics;
using PortViewer.Core.Models;

namespace PortViewer.Core.Services;

public sealed class ProcessInfoProvider : IProcessInfoProvider
{
    public ProcessInfoRecord GetInfo(int processId)
    {
        if (processId <= 0)
        {
            return new ProcessInfoRecord { ProcessId = processId, Name = processId == 0 ? "System Idle Process" : "系统" };
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            string path;
            try { path = process.MainModule?.FileName ?? string.Empty; }
            catch { path = string.Empty; }

            long? ticks;
            try { ticks = process.StartTime.ToUniversalTime().Ticks; }
            catch { ticks = null; }

            return new ProcessInfoRecord
            {
                ProcessId = processId,
                Name = process.ProcessName,
                ExecutablePath = path,
                StartTimeUtcTicks = ticks
            };
        }
        catch
        {
            return new ProcessInfoRecord { ProcessId = processId, Name = "无法访问" };
        }
    }
}
