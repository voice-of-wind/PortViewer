using PortViewer.Core.Models;

namespace PortViewer.Core.Services;

public static class EndpointFilter
{
    public static IEnumerable<EndpointRecord> Apply(
        IEnumerable<EndpointRecord> source,
        bool showAllTcpConnections,
        string? searchText)
    {
        var query = source.Where(x => showAllTcpConnections ||
                                      x.Protocol == NetworkProtocol.Udp ||
                                      string.Equals(x.State, "监听", StringComparison.OrdinalIgnoreCase));

        var search = searchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(x => Matches(x, search));
        }

        return query
            .OrderBy(x => x.LocalPort)
            .ThenBy(x => x.Protocol)
            .ThenBy(x => x.ProcessId)
            .ThenBy(x => x.LocalAddress, StringComparer.OrdinalIgnoreCase);
    }

    public static bool Matches(EndpointRecord row, string search)
    {
        return Contains(row.ProtocolText, search) ||
               Contains(row.AddressFamily, search) ||
               Contains(row.LocalAddress, search) ||
               row.LocalPort.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Contains(row.LocalEndpoint, search) ||
               Contains(row.RemoteAddress, search) ||
               (row.RemotePort?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               Contains(row.RemoteEndpoint, search) ||
               Contains(row.DisplayState, search) ||
               row.ProcessId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Contains(row.ProcessName, search) ||
               Contains(row.ExecutablePath, search) ||
               Contains(row.ServiceNames, search);
    }

    private static bool Contains(string? value, string search)
    {
        return value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }
}
