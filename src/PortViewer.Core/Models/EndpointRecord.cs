namespace PortViewer.Core.Models;

public enum NetworkProtocol
{
    Tcp,
    Udp
}

public sealed class EndpointRecord
{
    public NetworkProtocol Protocol { get; init; }
    public string AddressFamily { get; init; } = string.Empty;
    public string LocalAddress { get; init; } = string.Empty;
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = string.Empty;
    public int? RemotePort { get; init; }
    public string State { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string ServiceNames { get; init; } = string.Empty;
    public DateTime CollectedAt { get; init; }

    public string ProtocolText => Protocol == NetworkProtocol.Tcp ? "TCP" : "UDP";
    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);
    public string RemoteEndpoint => RemotePort.HasValue ? FormatEndpoint(RemoteAddress, RemotePort.Value) : "—";
    public string DisplayState => string.IsNullOrWhiteSpace(State) ? "绑定" : State;

    public string IdentityKey => string.Join("|", Protocol, AddressFamily, LocalAddress, LocalPort,
        RemoteAddress, RemotePort, State, ProcessId);

    private static string FormatEndpoint(string address, int port)
    {
        return address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
    }
}
