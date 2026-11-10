using PortViewer.Core.Models;

namespace PortViewer.Core.Services;

public sealed class PortSnapshotService
{
    private readonly INetworkTableReader _networkReader;
    private readonly IServiceLookup _serviceLookup;
    private readonly IProcessInfoProvider _processInfoProvider;

    public PortSnapshotService(INetworkTableReader networkReader, IServiceLookup serviceLookup,
        IProcessInfoProvider processInfoProvider)
    {
        _networkReader = networkReader;
        _serviceLookup = serviceLookup;
        _processInfoProvider = processInfoProvider;
    }

    public IReadOnlyList<EndpointRecord> Capture()
    {
        var raw = _networkReader.ReadEndpoints();
        IReadOnlyDictionary<int, IReadOnlyList<string>> services;
        try { services = _serviceLookup.GetServicesByProcessId(); }
        catch { services = new Dictionary<int, IReadOnlyList<string>>(); }

        var processCache = new Dictionary<int, ProcessInfoRecord>();
        return raw.Select(row =>
        {
            if (!processCache.TryGetValue(row.ProcessId, out var process))
            {
                process = _processInfoProvider.GetInfo(row.ProcessId);
                processCache[row.ProcessId] = process;
            }

            services.TryGetValue(row.ProcessId, out var serviceNames);
            return new EndpointRecord
            {
                Protocol = row.Protocol,
                AddressFamily = row.AddressFamily,
                LocalAddress = row.LocalAddress,
                LocalPort = row.LocalPort,
                RemoteAddress = row.RemoteAddress,
                RemotePort = row.RemotePort,
                State = row.State,
                ProcessId = row.ProcessId,
                ProcessName = process.Name,
                ExecutablePath = process.ExecutablePath,
                ServiceNames = serviceNames is null ? string.Empty : string.Join("；", serviceNames),
                CollectedAt = row.CollectedAt
            };
        }).ToArray();
    }
}
