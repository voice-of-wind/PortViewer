using PortViewer.Core.Models;

namespace PortViewer.Core.Services;

public interface INetworkTableReader
{
    IReadOnlyList<EndpointRecord> ReadEndpoints();
}

public interface IServiceLookup
{
    IReadOnlyDictionary<int, IReadOnlyList<string>> GetServicesByProcessId();
}

public interface IProcessInfoProvider
{
    ProcessInfoRecord GetInfo(int processId);
}
