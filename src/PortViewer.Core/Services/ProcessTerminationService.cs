using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PortViewer.Core.Services;

public enum TerminationStatus
{
    Success,
    TargetGone,
    IdentityChanged,
    Protected,
    RequiresElevation,
    Failed
}

public sealed class TerminationResult
{
    public TerminationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsSuccess => Status == TerminationStatus.Success;
}

public static class ElevatedKillExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 10;
    public const int Protected = 11;
    public const int IdentityChanged = 12;
    public const int AccessDenied = 13;
    public const int TargetGone = 14;
    public const int Failed = 15;

    public static int FromResult(TerminationResult result) => result.Status switch
    {
        TerminationStatus.Success => Success,
        TerminationStatus.Protected => Protected,
        TerminationStatus.IdentityChanged => IdentityChanged,
        TerminationStatus.RequiresElevation => AccessDenied,
        TerminationStatus.TargetGone => TargetGone,
        _ => Failed
    };

    public static TerminationResult ToResult(int exitCode) => exitCode switch
    {
        Success => Result(TerminationStatus.Success, "进程已由管理员权限结束。"),
        Protected => Result(TerminationStatus.Protected, "安全策略拒绝结束该系统关键进程。"),
        IdentityChanged => Result(TerminationStatus.IdentityChanged, "PID 已被其他进程复用，操作已取消。"),
        AccessDenied => Result(TerminationStatus.RequiresElevation, "管理员权限仍不足，无法结束目标进程。"),
        TargetGone => Result(TerminationStatus.TargetGone, "目标进程已经退出。"),
        InvalidArguments => Result(TerminationStatus.Failed, "提权请求参数无效。"),
        _ => Result(TerminationStatus.Failed, $"提权操作失败，退出代码：{exitCode}。")
    };

    private static TerminationResult Result(TerminationStatus status, string message) =>
        new() { Status = status, Message = message };
}

public sealed class ProcessTerminationService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;

    public TerminationResult Terminate(int processId, long expectedStartTimeUtcTicks)
    {
        var basicBlock = ValidateBasicTarget(processId, Environment.ProcessId);
        if (basicBlock is not null)
        {
            return basicBlock;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return Result(TerminationStatus.TargetGone, "目标进程已经退出。", processId);
        }
        catch (Exception ex)
        {
            return Result(TerminationStatus.Failed, $"无法读取目标进程：{ex.Message}", processId);
        }

        using (process)
        {
            long actualStartTicks;
            try
            {
                actualStartTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
            {
                return Result(TerminationStatus.RequiresElevation, "读取进程身份需要管理员权限。", processId);
            }
            catch (InvalidOperationException)
            {
                return Result(TerminationStatus.TargetGone, "目标进程已经退出。", processId);
            }

            if (!IdentityMatches(expectedStartTimeUtcTicks, actualStartTicks))
            {
                return Result(TerminationStatus.IdentityChanged, "PID 已被其他进程复用，操作已取消。", processId);
            }

            var criticalStatus = TryIsCritical(processId, out var isCritical);
            if (criticalStatus == CriticalQueryStatus.AccessDenied)
            {
                return Result(TerminationStatus.RequiresElevation, "检查关键进程状态需要管理员权限。", processId);
            }

            if (criticalStatus == CriticalQueryStatus.Failed)
            {
                return Result(TerminationStatus.Protected, "无法确认目标是否为系统关键进程，已安全取消。", processId);
            }

            if (isCritical)
            {
                return Result(TerminationStatus.Protected, "Windows 将目标标记为关键进程，已拒绝结束。", processId);
            }

            try
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
                return Result(TerminationStatus.Success, "进程已结束，其占用端口将随之释放。", processId);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
            {
                return Result(TerminationStatus.RequiresElevation, "结束该进程需要管理员权限。", processId);
            }
            catch (InvalidOperationException)
            {
                return Result(TerminationStatus.TargetGone, "目标进程已经退出。", processId);
            }
            catch (Exception ex)
            {
                return Result(TerminationStatus.Failed, $"结束进程失败：{ex.Message}", processId);
            }
        }
    }

    public static bool IdentityMatches(long expectedStartTimeUtcTicks, long actualStartTimeUtcTicks)
    {
        return expectedStartTimeUtcTicks > 0 && expectedStartTimeUtcTicks == actualStartTimeUtcTicks;
    }

    public static TerminationResult? ValidateBasicTarget(int processId, int currentProcessId)
    {
        if (processId is 0 or 4)
        {
            return Result(TerminationStatus.Protected, "系统核心 PID 0/4 不允许结束。", processId);
        }

        if (processId < 0)
        {
            return Result(TerminationStatus.Protected, "无效的进程标识。", processId);
        }

        if (processId == currentProcessId)
        {
            return Result(TerminationStatus.Protected, "PortViewer 不能结束自身。", processId);
        }

        return null;
    }

    private static CriticalQueryStatus TryIsCritical(int processId, out bool critical)
    {
        critical = false;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)processId));
        if (handle == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() == ErrorAccessDenied
                ? CriticalQueryStatus.AccessDenied
                : CriticalQueryStatus.Failed;
        }

        try
        {
            if (!IsProcessCritical(handle, out critical))
            {
                return Marshal.GetLastWin32Error() == ErrorAccessDenied
                    ? CriticalQueryStatus.AccessDenied
                    : CriticalQueryStatus.Failed;
            }
            return CriticalQueryStatus.Success;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static TerminationResult Result(TerminationStatus status, string message, int processId) =>
        new() { Status = status, Message = $"PID {processId}：{message}" };

    private enum CriticalQueryStatus
    {
        Success,
        AccessDenied,
        Failed
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr processHandle, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
