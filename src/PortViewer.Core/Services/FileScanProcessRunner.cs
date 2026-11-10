using System.Diagnostics;
using System.Text.Json;
using PortViewer.Core.Models;

namespace PortViewer.Core.Services;

public sealed class FileScanProcessRunner
{
    public async Task<FileLockScanResult> ScanAsync(string executable, string path, TimeSpan timeout)
    {
        var output = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--scan-handles");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动文件扫描进程。");
            using var cancellation = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                return new FileLockScanResult
                {
                    TargetPath = path, IsPartial = true,
                    Error = $"句柄扫描超过 {timeout.TotalSeconds:0} 秒，已停止扫描进程。可缩小目标范围后重试。"
                };
            }
            var json = await File.ReadAllTextAsync(output);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException($"扫描进程未返回结果（退出代码 {process.ExitCode}）。");
            return JsonSerializer.Deserialize<FileLockScanResult>(json)
                   ?? throw new InvalidOperationException("扫描结果无法读取。");
        }
        finally { try { File.Delete(output); } catch { } }
    }
}
