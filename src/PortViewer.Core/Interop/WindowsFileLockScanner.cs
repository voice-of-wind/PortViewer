using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PortViewer.Core.Models;
using PortViewer.Core.Services;

namespace PortViewer.Core.Interop;

public sealed class WindowsFileLockScanner
{
    private const int MaxHits = 10000;
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x0002;
    private const uint FileTypeDisk = 0x0001;

    public FileLockScanResult Scan(string path)
    {
        var target = FileLockPath.Normalize(path);
        var isDirectory = Directory.Exists(target);
        if (!isDirectory && !File.Exists(target)) throw new FileNotFoundException("文件或文件夹不存在。", target);

        var result = new FileLockScanResult { TargetPath = target, IsDirectory = isDirectory };
        if (!isDirectory)
        {
            try { ReadRestartManager(target, result); }
            catch (Exception ex) { result.IsPartial = true; result.Error = $"Restart Manager 查询失败：{ex.Message}"; }
        }

        try
        {
            ReadHandles(target, isDirectory, result);
            result.HandleScanAvailable = true;
        }
        catch (Exception ex)
        {
            result.IsPartial = true;
            result.Error = string.IsNullOrEmpty(result.Error)
                ? $"句柄扫描不可用：{ex.Message}"
                : $"{result.Error}；句柄扫描不可用：{ex.Message}";
        }

        result.Hits = result.Hits
            .GroupBy(h => (h.ProcessId, h.Path, h.Source))
            .Select(g => g.First()).ToList();
        return result;
    }

    private static void ReadHandles(string target, bool isDirectory, FileLockScanResult result)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("句柄扫描需要 x64 进程。");
        if (!NativeLibrary.TryLoad("ntdll.dll", out var module))
            throw new PlatformNotSupportedException("无法载入 Windows 系统信息接口。");

        try
        {
            if (!NativeLibrary.TryGetExport(module, "NtQuerySystemInformation", out var address))
                throw new PlatformNotSupportedException("当前 Windows 未提供句柄枚举接口。");
            var query = Marshal.GetDelegateForFunctionPointer<NtQuerySystemInformationDelegate>(address);
            using var reference = File.Open(Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位程序文件。"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var ownHandle = reference.SafeFileHandle.DangerousGetHandle().ToInt64();
            IntPtr buffer = IntPtr.Zero;
            var bufferSize = 4 * 1024 * 1024;
            try
            {
                int status;
                while (true)
                {
                    buffer = Marshal.AllocHGlobal(bufferSize);
                    status = query(SystemExtendedHandleInformation, buffer, bufferSize, out var needed);
                    if (status == 0) break;
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    if (status != StatusInfoLengthMismatch || bufferSize >= 256 * 1024 * 1024)
                        throw new Win32Exception($"系统句柄枚举失败：NTSTATUS 0x{status:X8}。");
                    bufferSize = Math.Max(bufferSize * 2, needed + 65536);
                }

                var count = Marshal.ReadInt64(buffer);
                if (count < 0 || count > (bufferSize - 16L) / 40)
                    throw new InvalidDataException("系统句柄表格式不受支持。");

                ushort fileTypeIndex = 0;
                for (long i = 0; i < count; i++)
                {
                    var entry = IntPtr.Add(buffer, checked((int)(16 + i * 40)));
                    if (Marshal.ReadIntPtr(entry, 8).ToInt64() == Environment.ProcessId &&
                        Marshal.ReadIntPtr(entry, 16).ToInt64() == ownHandle)
                    {
                        fileTypeIndex = unchecked((ushort)Marshal.ReadInt16(entry, 30));
                        break;
                    }
                }
                if (fileTypeIndex == 0) throw new InvalidDataException("无法识别文件句柄类型。");

                var processes = new Dictionary<int, IntPtr>();
                var denied = new HashSet<int>();
                try
                {
                    for (long i = 0; i < count; i++)
                    {
                        var entry = IntPtr.Add(buffer, checked((int)(16 + i * 40)));
                        if (unchecked((ushort)Marshal.ReadInt16(entry, 30)) != fileTypeIndex) continue;
                        var rawPid = Marshal.ReadIntPtr(entry, 8).ToInt64();
                        if (rawPid <= 0 || rawPid > int.MaxValue || rawPid == Environment.ProcessId) continue;
                        var pid = (int)rawPid;
                        if (denied.Contains(pid)) { result.UnreadableHandles++; continue; }
                        if (!processes.TryGetValue(pid, out var source))
                        {
                            source = OpenProcess(ProcessDuplicateHandle, false, pid);
                            if (source == IntPtr.Zero)
                            {
                                result.UnreadableHandles++;
                                denied.Add(pid);
                                continue;
                            }
                            processes.Add(pid, source);
                        }
                        if (!DuplicateHandle(source, Marshal.ReadIntPtr(entry, 16), GetCurrentProcess(),
                                out var duplicated, 0, false, DuplicateSameAccess))
                        {
                            result.UnreadableHandles++;
                            continue;
                        }
                        using (duplicated)
                        {
                            if (GetFileType(duplicated) != FileTypeDisk) continue;
                            var pathBuffer = new StringBuilder(32768);
                            var length = GetFinalPathNameByHandle(duplicated, pathBuffer, pathBuffer.Capacity, 0);
                            if (length == 0 || length >= pathBuffer.Capacity)
                            {
                                result.UnreadableHandles++;
                                continue;
                            }
                            var actualPath = pathBuffer.ToString();
                            if (FileLockPath.Matches(target, isDirectory, actualPath))
                            {
                                result.Hits.Add(new FileLockHit { ProcessId = pid, Path = FileLockPath.Normalize(actualPath), Source = "文件句柄" });
                                if (result.Hits.Count >= MaxHits)
                                {
                                    result.IsPartial = true;
                                    result.Error = "匹配结果超过 10000 条，请选择更具体的文件夹。";
                                    break;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var process in processes.Values) CloseHandle(process);
                }
                if (result.UnreadableHandles > 0) result.IsPartial = true;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        finally { NativeLibrary.Free(module); }
    }

    private static void ReadRestartManager(string target, FileLockScanResult result)
    {
        var key = new StringBuilder(33);
        var error = RmStartSession(out var session, 0, key);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            error = RmRegisterResources(session, 1, new[] { target }, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (error != 0) throw new Win32Exception(error);
            uint needed = 0, count = 0, rebootReasons;
            error = RmGetList(session, out needed, ref count, null, out rebootReasons);
            if (error == 0) return;
            if (error != 234) throw new Win32Exception(error);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var processes = new RmProcessInfo[needed];
                count = needed;
                error = RmGetList(session, out needed, ref count, processes, out rebootReasons);
                if (error == 234) continue;
                if (error != 0) throw new Win32Exception(error);
                foreach (var process in processes.Take(checked((int)count)))
                {
                    if (process.Process.ProcessId == Environment.ProcessId) continue;
                    var fileTime = ((long)process.Process.StartTime.High << 32) |
                                   (uint)process.Process.StartTime.Low;
                    long? ticks = null;
                    try { ticks = DateTime.FromFileTimeUtc(fileTime).Ticks; } catch { }
                    result.Hits.Add(new FileLockHit
                    {
                        ProcessId = process.Process.ProcessId, StartTimeUtcTicks = ticks,
                        Path = target, Source = "Restart Manager"
                    });
                }
                return;
            }
            throw new InvalidOperationException("占用进程列表持续变化，请重试。");
        }
        finally { RmEndSession(session); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NtQuerySystemInformationDelegate(int informationClass, IntPtr information,
        int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out SafeFileHandle targetHandle, uint access, bool inheritHandle, uint options);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmFileTime { public int Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int ProcessId; public RmFileTime StartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
        uint appCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] RmProcessInfo[]? processes, out uint rebootReasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
