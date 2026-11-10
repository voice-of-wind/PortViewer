using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using PortViewer.Core.Models;
using PortViewer.Core.Services;

namespace PortViewer.Core.Interop;

public sealed class WindowsNetworkTableReader : INetworkTableReader
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int ErrorInsufficientBuffer = 122;

    public IReadOnlyList<EndpointRecord> ReadEndpoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("端口枚举仅支持 Windows。");
        }

        var collectedAt = DateTime.Now;
        var rows = new List<EndpointRecord>();
        ReadTcp4(rows, collectedAt);
        ReadTcp6(rows, collectedAt);
        ReadUdp4(rows, collectedAt);
        ReadUdp6(rows, collectedAt);
        return rows;
    }

    private static void ReadTcp4(List<EndpointRecord> target, DateTime collectedAt)
    {
        ReadNativeTable(
            (IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, true, AfInet,
                TcpTableClass.OwnerPidAll, 0),
            buffer =>
            {
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var rowPointer = IntPtr.Add(buffer, sizeof(uint));
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                    target.Add(new EndpointRecord
                    {
                        Protocol = NetworkProtocol.Tcp,
                        AddressFamily = "IPv4",
                        LocalAddress = ToIpv4(row.LocalAddress),
                        LocalPort = ToPort(row.LocalPort),
                        RemoteAddress = ToIpv4(row.RemoteAddress),
                        RemotePort = ToPort(row.RemotePort),
                        State = ToTcpState(row.State),
                        ProcessId = unchecked((int)row.OwningProcessId),
                        CollectedAt = collectedAt
                    });
                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
            });
    }

    private static void ReadTcp6(List<EndpointRecord> target, DateTime collectedAt)
    {
        ReadNativeTable(
            (IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, true, AfInet6,
                TcpTableClass.OwnerPidAll, 0),
            buffer =>
            {
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                var rowPointer = IntPtr.Add(buffer, sizeof(uint));
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPointer);
                    target.Add(new EndpointRecord
                    {
                        Protocol = NetworkProtocol.Tcp,
                        AddressFamily = "IPv6",
                        LocalAddress = ToIpv6(row.LocalAddress, row.LocalScopeId),
                        LocalPort = ToPort(row.LocalPort),
                        RemoteAddress = ToIpv6(row.RemoteAddress, row.RemoteScopeId),
                        RemotePort = ToPort(row.RemotePort),
                        State = ToTcpState(row.State),
                        ProcessId = unchecked((int)row.OwningProcessId),
                        CollectedAt = collectedAt
                    });
                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
            });
    }

    private static void ReadUdp4(List<EndpointRecord> target, DateTime collectedAt)
    {
        ReadNativeTable(
            (IntPtr buffer, ref int size) => GetExtendedUdpTable(buffer, ref size, true, AfInet,
                UdpTableClass.OwnerPid, 0),
            buffer =>
            {
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
                var rowPointer = IntPtr.Add(buffer, sizeof(uint));
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPointer);
                    target.Add(new EndpointRecord
                    {
                        Protocol = NetworkProtocol.Udp,
                        AddressFamily = "IPv4",
                        LocalAddress = ToIpv4(row.LocalAddress),
                        LocalPort = ToPort(row.LocalPort),
                        ProcessId = unchecked((int)row.OwningProcessId),
                        CollectedAt = collectedAt
                    });
                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
            });
    }

    private static void ReadUdp6(List<EndpointRecord> target, DateTime collectedAt)
    {
        ReadNativeTable(
            (IntPtr buffer, ref int size) => GetExtendedUdpTable(buffer, ref size, true, AfInet6,
                UdpTableClass.OwnerPid, 0),
            buffer =>
            {
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
                var rowPointer = IntPtr.Add(buffer, sizeof(uint));
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPointer);
                    target.Add(new EndpointRecord
                    {
                        Protocol = NetworkProtocol.Udp,
                        AddressFamily = "IPv6",
                        LocalAddress = ToIpv6(row.LocalAddress, row.LocalScopeId),
                        LocalPort = ToPort(row.LocalPort),
                        ProcessId = unchecked((int)row.OwningProcessId),
                        CollectedAt = collectedAt
                    });
                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
            });
    }

    private delegate uint NativeTableCall(IntPtr buffer, ref int size);

    private static void ReadNativeTable(NativeTableCall call, Action<IntPtr> parse)
    {
        var size = 0;
        var result = call(IntPtr.Zero, ref size);
        if (result != ErrorInsufficientBuffer && result != 0)
        {
            throw new Win32Exception(unchecked((int)result));
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                buffer = Marshal.AllocHGlobal(size);
                result = call(buffer, ref size);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != 0)
                {
                    throw new Win32Exception(unchecked((int)result));
                }

                parse(buffer);
                return;
            }

            throw new InvalidOperationException("系统端口表在读取期间持续变化，请稍后重试。");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string ToIpv4(uint address) => new IPAddress(BitConverter.GetBytes(address)).ToString();

    private static string ToIpv6(byte[] address, uint scopeId)
    {
        var ip = new IPAddress(address, scopeId);
        return ip.ToString();
    }

    private static int ToPort(uint port)
    {
        return unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)port)));
    }

    private static string ToTcpState(uint state) => state switch
    {
        1 => "已关闭",
        2 => "监听",
        3 => "SYN 已发送",
        4 => "SYN 已接收",
        5 => "已建立",
        6 => "FIN 等待 1",
        7 => "FIN 等待 2",
        8 => "关闭等待",
        9 => "正在关闭",
        10 => "最后确认",
        11 => "时间等待",
        12 => "删除中",
        _ => $"未知 ({state})"
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool order,
        int addressFamily, TcpTableClass tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr udpTable, ref int size, bool order,
        int addressFamily, UdpTableClass tableClass, uint reserved);

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddress;
        public uint LocalPort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningProcessId;
    }
}
