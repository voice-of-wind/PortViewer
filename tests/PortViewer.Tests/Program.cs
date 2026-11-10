using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PortViewer.Core.Interop;
using PortViewer.Core.Models;
using PortViewer.Core.Services;

if (args.Length == 1 && args[0] == "--kill-target")
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
    Console.Out.Flush();
    await Task.Delay(TimeSpan.FromMinutes(5));
    listener.Stop();
    return 0;
}

if (args.Length == 3 && args[0] == "--hold-path")
{
    using var held = args[1] == "directory"
        ? OpenDirectory(args[2])
        : File.OpenHandle(args[2], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    Console.WriteLine("ready");
    Console.Out.Flush();
    await Task.Delay(TimeSpan.FromMinutes(5));
    return 0;
}

if (args.Length == 3 && args[0] == "--scan-handles" && args[1] == "--hang-test")
{
    await Task.Delay(TimeSpan.FromMinutes(5));
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("默认范围包含 TCP 监听和 UDP", TestDefaultScope),
    ("全部范围包含已建立连接", TestAllScope),
    ("搜索覆盖端口 PID 进程 服务 地址", TestSearch),
    ("默认排序按本地端口", TestSort),
    ("快照按 PID 缓存进程信息并关联服务", TestSnapshotEnrichment),
    ("刷新协调器阻止重入", TestRefreshCoordinator),
    ("安全策略阻止核心 PID 和自身", TestSafetyRules),
    ("启动时间校验阻止 PID 复用", TestIdentityMatching),
    ("提权退出代码映射", TestElevationResultMapping),
    ("真实 IPv4 TCP/UDP 监听可被原生 API 枚举", TestNativeIpv4Enumeration),
    ("真实 IPv6 TCP/UDP 监听可被原生 API 枚举", TestNativeIpv6Enumeration),
    ("受控监听进程可被安全终止且端口消失", TestControlledTermination),
    ("文件夹路径匹配不会误命中相似前缀", TestFilePathMatching),
    ("受控文件句柄与目录句柄可定位且释放后消失", TestControlledFileLocks),
    ("确认期间占用或进程身份变化会取消操作", TestFileScanGuard),
    ("句柄扫描超时会结束辅助进程并返回不完整结果", TestFileScanTimeout)
};

var passed = 0;
var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {test.Name}\n       {ex}");
        failed++;
    }
}

Console.WriteLine($"\n结果：{passed} 通过，{failed} 失败，共 {tests.Length} 项。");
return failed == 0 ? 0 : 1;

static Task TestDefaultScope()
{
    var rows = SampleRows();
    var actual = EndpointFilter.Apply(rows, false, null).ToArray();
    Assert(actual.Length == 2, "默认应只保留监听 TCP 与 UDP。");
    Assert(actual.Any(x => x.Protocol == NetworkProtocol.Udp), "UDP 绑定应显示。");
    Assert(!actual.Any(x => x.State == "已建立"), "默认不应显示已建立连接。");
    return Task.CompletedTask;
}

static Task TestAllScope()
{
    var actual = EndpointFilter.Apply(SampleRows(), true, null).ToArray();
    Assert(actual.Length == 3, "全部范围应保留所有记录。");
    Assert(actual.Any(x => x.State == "已建立"), "全部范围应包含已建立连接。");
    return Task.CompletedTask;
}

static Task TestSearch()
{
    var row = SampleRows()[0];
    foreach (var term in new[] { "8080", "321", "demo", "示例服务", "127.0.0.1", "tcp", "监听" })
    {
        Assert(EndpointFilter.Matches(row, term), $"搜索应匹配：{term}");
    }
    Assert(!EndpointFilter.Matches(row, "not-present"), "无关文本不应匹配。");
    Assert(EndpointFilter.Matches(SampleRows()[2], "绑定"), "UDP 显示状态应可搜索。");
    return Task.CompletedTask;
}

static Task TestSort()
{
    var actual = EndpointFilter.Apply(SampleRows().Reverse(), true, null).ToArray();
    Assert(actual.Select(x => x.LocalPort).SequenceEqual(new[] { 53, 443, 8080 }), "端口应升序排列。");
    return Task.CompletedTask;
}

static Task TestSnapshotEnrichment()
{
    var processProvider = new FakeProcessProvider();
    var service = new PortSnapshotService(new FakeNetworkReader(), new FakeServiceLookup(), processProvider);
    var rows = service.Capture();
    Assert(processProvider.CallCount == 1, "同一 PID 只应读取一次进程信息。");
    Assert(rows.All(x => x.ProcessName == "demo"), "应补全进程名。");
    Assert(rows.All(x => x.ServiceNames.Contains("DemoService")), "应关联服务名。");
    return Task.CompletedTask;
}

static Task TestRefreshCoordinator()
{
    var coordinator = new RefreshCoordinator();
    Assert(coordinator.TryEnter(), "首次进入应成功。");
    Assert(!coordinator.TryEnter(), "刷新期间应拒绝重入。");
    coordinator.Exit();
    Assert(coordinator.TryEnter(), "退出后应允许下一次刷新。");
    coordinator.Exit();
    return Task.CompletedTask;
}

static Task TestSafetyRules()
{
    Assert(ProcessTerminationService.ValidateBasicTarget(0, 999) is not null, "PID 0 必须阻止。");
    Assert(ProcessTerminationService.ValidateBasicTarget(4, 999) is not null, "PID 4 必须阻止。");
    Assert(ProcessTerminationService.ValidateBasicTarget(999, 999) is not null, "自身 PID 必须阻止。");
    Assert(ProcessTerminationService.ValidateBasicTarget(1000, 999) is null, "普通 PID 应通过基础校验。");
    return Task.CompletedTask;
}

static Task TestIdentityMatching()
{
    Assert(ProcessTerminationService.IdentityMatches(123, 123), "相同启动时间应匹配。");
    Assert(!ProcessTerminationService.IdentityMatches(123, 124), "不同启动时间应判定 PID 复用。");
    Assert(!ProcessTerminationService.IdentityMatches(0, 0), "缺失启动时间不得匹配。");
    return Task.CompletedTask;
}

static Task TestElevationResultMapping()
{
    var success = ElevatedKillExitCodes.ToResult(ElevatedKillExitCodes.Success);
    var protectedResult = ElevatedKillExitCodes.ToResult(ElevatedKillExitCodes.Protected);
    Assert(success.IsSuccess, "成功退出代码应映射成功。");
    Assert(protectedResult.Status == TerminationStatus.Protected, "保护退出代码应正确映射。");
    Assert(ElevatedKillExitCodes.FromResult(protectedResult) == ElevatedKillExitCodes.Protected,
        "结果应能映射回退出代码。");
    return Task.CompletedTask;
}

static Task TestNativeIpv4Enumeration()
{
    var tcp = new TcpListener(IPAddress.Loopback, 0);
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    try
    {
        tcp.Start();
        var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var rows = new WindowsNetworkTableReader().ReadEndpoints();
        Assert(rows.Any(x => x.Protocol == NetworkProtocol.Tcp && x.AddressFamily == "IPv4" &&
                             x.LocalPort == tcpPort && x.ProcessId == Environment.ProcessId && x.State == "监听"),
            $"未找到测试 TCP 监听端口 {tcpPort}。");
        Assert(rows.Any(x => x.Protocol == NetworkProtocol.Udp && x.AddressFamily == "IPv4" &&
                             x.LocalPort == udpPort && x.ProcessId == Environment.ProcessId),
            $"未找到测试 UDP 绑定端口 {udpPort}。");
    }
    finally
    {
        tcp.Stop();
    }
    return Task.CompletedTask;
}

static Task TestNativeIpv6Enumeration()
{
    if (!Socket.OSSupportsIPv6)
    {
        return Task.CompletedTask;
    }

    var tcp = new TcpListener(IPAddress.IPv6Loopback, 0);
    using var udp = new UdpClient(AddressFamily.InterNetworkV6);
    try
    {
        tcp.Server.DualMode = false;
        tcp.Start();
        udp.Client.DualMode = false;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var rows = new WindowsNetworkTableReader().ReadEndpoints();
        Assert(rows.Any(x => x.Protocol == NetworkProtocol.Tcp && x.AddressFamily == "IPv6" &&
                             x.LocalPort == tcpPort && x.ProcessId == Environment.ProcessId),
            $"未找到测试 IPv6 TCP 监听端口 {tcpPort}。");
        Assert(rows.Any(x => x.Protocol == NetworkProtocol.Udp && x.AddressFamily == "IPv6" &&
                             x.LocalPort == udpPort && x.ProcessId == Environment.ProcessId),
            $"未找到测试 IPv6 UDP 绑定端口 {udpPort}。");
    }
    finally
    {
        tcp.Stop();
    }
    return Task.CompletedTask;
}

static async Task TestControlledTermination()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位测试程序。");
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = executable,
        Arguments = "--kill-target",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    using var child = System.Diagnostics.Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动受控监听子进程。");
    try
    {
        var line = await child.StandardOutput.ReadLineAsync();
        Assert(int.TryParse(line, out var port), "子进程未返回有效监听端口。");
        var startTicks = child.StartTime.ToUniversalTime().Ticks;
        var before = new WindowsNetworkTableReader().ReadEndpoints();
        Assert(before.Any(x => x.ProcessId == child.Id && x.LocalPort == port && x.State == "监听"),
            "终止前应能找到子进程监听端口。");

        var result = new ProcessTerminationService().Terminate(child.Id, startTicks);
        Assert(result.IsSuccess, $"安全终止应成功：{result.Message}");
        await child.WaitForExitAsync();

        var after = new WindowsNetworkTableReader().ReadEndpoints();
        Assert(!after.Any(x => x.ProcessId == child.Id && x.LocalPort == port),
            "终止后端口记录应消失。");
    }
    finally
    {
        if (!child.HasExited)
        {
            child.Kill();
            await child.WaitForExitAsync();
        }
    }
}

static Task TestFilePathMatching()
{
    var root = Path.Combine(Path.GetTempPath(), "portviewer-match");
    Assert(FileLockPath.Matches(root, true, Path.Combine(root, "child.txt")), "目录内文件应命中。");
    Assert(FileLockPath.Matches(root, true, root), "目录自身应命中。");
    Assert(!FileLockPath.Matches(root, true, root + "-other\\child.txt"), "相似前缀不得命中。");
    Assert(!FileLockPath.Matches(root, false, Path.Combine(root, "child.txt")), "文件模式应精确匹配。");
    Assert(!FileLockPath.Matches(root, true, @"\\server\share\child.txt"), "网络共享路径不应误命中。");
    Assert(!FileLockPath.Matches(root, true, "relative.txt"), "相对路径不应误命中。");
    return Task.CompletedTask;
}

static Task TestFileScanGuard()
{
    var root = Path.Combine(Path.GetTempPath(), "portviewer-guard");
    var scan = new FileLockScanResult
    {
        TargetPath = root, IsDirectory = true,
        Hits = new List<FileLockHit>
        {
            new() { ProcessId = 123, StartTimeUtcTicks = 456, Path = Path.Combine(root, "held.txt") }
        }
    };
    Assert(scan.StillOwnedBy(123, 456), "进程身份及路径一致时允许继续核对。");
    Assert(!scan.StillOwnedBy(123, 457), "进程启动时间变化时必须取消。");
    Assert(!scan.StillOwnedBy(124, 456), "PID 变化时必须取消。");
    scan.Hits.Clear();
    Assert(!scan.StillOwnedBy(123, 456), "确认期间句柄消失时必须取消。");
    return Task.CompletedTask;
}

static async Task TestFileScanTimeout()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位测试程序。");
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var result = await new FileScanProcessRunner().ScanAsync(executable, "--hang-test",
        TimeSpan.FromMilliseconds(200));
    Assert(result.IsPartial && result.Error.Contains("超", StringComparison.Ordinal),
        "超时必须返回不完整结果。");
    Assert(timer.Elapsed < TimeSpan.FromSeconds(10), "超时后辅助进程应尽快退出。");
}

static async Task TestControlledFileLocks()
{
    var root = Path.Combine(Path.GetTempPath(), "PortViewer-LockTest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "held.txt");
    File.WriteAllText(file, "controlled file lock");
    var children = new List<System.Diagnostics.Process>();
    try
    {
        children.Add(await StartHolder("file", file));
        children.Add(await StartHolder("file", file));
        children.Add(await StartHolder("directory", root));
        var scanner = new WindowsFileLockScanner();
        var fileResult = scanner.Scan(file);
        Assert(fileResult.HandleScanAvailable, $"文件句柄扫描不可用：{fileResult.Error}");
        Assert(children.Take(2).All(child => fileResult.Hits.Any(h => h.ProcessId == child.Id)),
            "文件扫描应定位两个受控进程。");
        var folderResult = scanner.Scan(root);
        Assert(folderResult.HandleScanAvailable, $"目录句柄扫描不可用：{folderResult.Error}");
        Assert(children.All(child => folderResult.Hits.Any(h => h.ProcessId == child.Id)),
            "目录扫描应定位目录自身及内部文件的持有进程。");
        var firstPid = children[0].Id;
        children[0].Kill();
        await children[0].WaitForExitAsync();
        var after = scanner.Scan(file);
        Assert(!after.Hits.Any(h => h.ProcessId == firstPid), "进程退出后应不再命中。");
    }
    finally
    {
        foreach (var child in children)
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            child.Dispose();
        }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(root, true); break; }
            catch (IOException) when (attempt < 9) { await Task.Delay(100); }
        }
    }
}

static async Task<System.Diagnostics.Process> StartHolder(string mode, string path)
{
    var start = new System.Diagnostics.ProcessStartInfo
    {
        FileName = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位测试程序。"),
        UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
    };
    start.ArgumentList.Add("--hold-path");
    start.ArgumentList.Add(mode);
    start.ArgumentList.Add(path);
    var child = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法启动占用测试进程。");
    var ready = await child.StandardOutput.ReadLineAsync();
    Assert(ready == "ready", "占用测试进程未准备好。");
    return child;
}

static SafeFileHandle OpenDirectory(string path)
{
    var handle = CreateFile(path, 0x80000000, 0x00000001 | 0x00000002, IntPtr.Zero,
        3, 0x02000000, IntPtr.Zero);
    if (handle.IsInvalid) throw new InvalidOperationException("无法打开受控目录句柄。");
    return handle;
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
    uint creation, uint flags, IntPtr template);

static EndpointRecord[] SampleRows() => new[]
{
    new EndpointRecord
    {
        Protocol = NetworkProtocol.Tcp, AddressFamily = "IPv4", LocalAddress = "127.0.0.1",
        LocalPort = 8080, State = "监听", ProcessId = 321, ProcessName = "demo",
        ServiceNames = "示例服务 (DemoService)"
    },
    new EndpointRecord
    {
        Protocol = NetworkProtocol.Tcp, AddressFamily = "IPv4", LocalAddress = "10.0.0.2",
        LocalPort = 443, RemoteAddress = "10.0.0.3", RemotePort = 52000, State = "已建立",
        ProcessId = 111, ProcessName = "browser"
    },
    new EndpointRecord
    {
        Protocol = NetworkProtocol.Udp, AddressFamily = "IPv6", LocalAddress = "::",
        LocalPort = 53, ProcessId = 222, ProcessName = "dns"
    }
};

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeNetworkReader : INetworkTableReader
{
    public IReadOnlyList<EndpointRecord> ReadEndpoints() => new[]
    {
        new EndpointRecord { Protocol = NetworkProtocol.Tcp, LocalPort = 1000, ProcessId = 42 },
        new EndpointRecord { Protocol = NetworkProtocol.Udp, LocalPort = 1001, ProcessId = 42 }
    };
}

sealed class FakeServiceLookup : IServiceLookup
{
    public IReadOnlyDictionary<int, IReadOnlyList<string>> GetServicesByProcessId() =>
        new Dictionary<int, IReadOnlyList<string>> { [42] = new[] { "DemoService" } };
}

sealed class FakeProcessProvider : IProcessInfoProvider
{
    public int CallCount { get; private set; }
    public ProcessInfoRecord GetInfo(int processId)
    {
        CallCount++;
        return new ProcessInfoRecord { ProcessId = processId, Name = "demo", ExecutablePath = "demo.exe" };
    }
}
