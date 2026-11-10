using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PortViewer.Core.Services;

namespace PortViewer.Core.Interop;

public sealed class WindowsServiceLookup : IServiceLookup
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;
    private const int ErrorMoreData = 234;

    public IReadOnlyDictionary<int, IReadOnlyList<string>> GetServicesByProcessId()
    {
        var result = new Dictionary<int, List<string>>();
        if (!OperatingSystem.IsWindows())
        {
            return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        }

        using var manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager.IsInvalid)
        {
            return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        }

        uint bytesNeeded = 0;
        uint servicesReturned = 0;
        uint resumeHandle = 0;
        EnumServicesStatusEx(manager, 0, ServiceWin32, ServiceStateAll, IntPtr.Zero, 0,
            out bytesNeeded, out servicesReturned, ref resumeHandle, null);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorMoreData || bytesNeeded == 0)
        {
            return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        }

        var buffer = Marshal.AllocHGlobal(unchecked((int)bytesNeeded));
        try
        {
            resumeHandle = 0;
            if (!EnumServicesStatusEx(manager, 0, ServiceWin32, ServiceStateAll, buffer, bytesNeeded,
                    out bytesNeeded, out servicesReturned, ref resumeHandle, null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var itemSize = Marshal.SizeOf<EnumServiceStatusProcess>();
            var pointer = buffer;
            for (var i = 0; i < servicesReturned; i++)
            {
                var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(pointer);
                var pid = unchecked((int)item.Status.ProcessId);
                if (pid > 0)
                {
                    var name = Marshal.PtrToStringUni(item.ServiceName) ?? string.Empty;
                    var displayName = Marshal.PtrToStringUni(item.DisplayName) ?? string.Empty;
                    var label = string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase) ||
                                string.IsNullOrWhiteSpace(displayName)
                        ? name
                        : $"{displayName} ({name})";
                    if (!result.TryGetValue(pid, out var names))
                    {
                        names = new List<string>();
                        result[pid] = names;
                    }
                    names.Add(label);
                }
                pointer = IntPtr.Add(pointer, itemSize);
            }
        }
        catch (Win32Exception)
        {
            // Service information is optional; endpoint enumeration can still succeed.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<string>)x.Value.OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(SafeServiceHandle manager, int infoLevel,
        uint serviceType, uint serviceState, IntPtr services, uint bufferSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess Status;
    }
}
