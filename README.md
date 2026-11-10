# PortViewer 端口占用查看器

PortViewer 是一个 Windows WPF 桌面工具，用来排查端口以及文件、文件夹的相关进程，并在明确影响范围后结束占用进程。

## 功能

- 原生读取 TCP/UDP 的 IPv4 与 IPv6 端口表，不解析 `netstat` 文本。
- 默认显示 TCP 监听端口与全部 UDP 绑定，可切换到全部 TCP 连接。
- 按端口、PID、进程、服务、协议、地址、状态即时搜索。
- 每 3 秒自动刷新，也可暂停或手动刷新。
- 结束进程前显示该进程的全部端口影响范围。
- 阻止 PID 0、PID 4、自身及 Windows 关键进程；权限不足时按需请求 UAC。
- “文件占用”页面支持选择或粘贴本机文件、文件夹路径，显示相关进程、命中路径、服务和端口记录。
- 文件查询结合 Restart Manager 与系统句柄扫描；文件夹查询覆盖文件夹本身和内部文件的打开句柄，不递归遍历目录。
- 句柄扫描在独立进程中执行，超过 30 秒自动停止；权限不足或接口不可用时报告扫描不完整。
- 强制结束前重新核对进程与目标路径的关联；需提权时在管理员窗口重新扫描并确认。

## 构建和测试

```powershell
dotnet restore .\PortViewer.sln
dotnet build .\PortViewer.sln -c Release --no-restore
dotnet run --project .\tests\PortViewer.Tests\PortViewer.Tests.csproj -c Release --no-build
```

## 发布 Windows x64 单文件

```powershell
dotnet restore .\src\PortViewer\PortViewer.csproj -r win-x64
dotnet publish .\src\PortViewer\PortViewer.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o .\publish\PortViewer-Windows-x64 --no-restore
```

## 安全说明

Windows 没有通用机制可以从外部只关闭另一个进程的某个端口。PortViewer 的“关闭占用进程”会强制结束整个目标进程，因此该进程的其他端口、连接和未保存工作也会同时受到影响。请在确认窗口中核对 PID、进程、服务及完整端口清单。

文件页面显示的是“相关进程”：打开文件或文件夹句柄并不一定意味着该进程阻止了操作。建议先在相应程序中正常关闭文件；需要强制结束时逐个核对进程和匹配路径。PortViewer 不会直接关闭单个句柄，也不会代替文件管理器重试删除或移动。系统驱动、远程共享和受保护进程可能无法完整识别。
