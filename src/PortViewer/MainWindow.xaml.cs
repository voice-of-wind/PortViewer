using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PortViewer.Core.Interop;
using PortViewer.Core.Models;
using PortViewer.Core.Services;

namespace PortViewer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<EndpointRecord> _visibleRows = new();
    private readonly PortSnapshotService _snapshotService;
    private readonly ProcessInfoProvider _processInfoProvider = new();
    private readonly ProcessTerminationService _terminationService = new();
    private readonly RefreshCoordinator _refreshCoordinator = new();
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<EndpointRecord> _allRows = Array.Empty<EndpointRecord>();
    private bool _isClosing;
    private readonly string? _initialFilePath;

    public MainWindow(string? initialFilePath = null)
    {
        InitializeComponent();
        _initialFilePath = initialFilePath;
        DataContext = _visibleRows;
        _snapshotService = new PortSnapshotService(
            new WindowsNetworkTableReader(),
            new WindowsServiceLookup(),
            _processInfoProvider);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            FilePathBox.Text = _initialFilePath;
            ShowFilePage();
            await ScanFileAsync();
            return;
        }
        if (AutoRefreshCheckBox.IsChecked == true)
        {
            _timer.Start();
        }
        await RefreshAsync(true);
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _timer.Stop();
    }

    private async Task RefreshAsync(bool userInitiated)
    {
        if (_isClosing || !_refreshCoordinator.TryEnter())
        {
            if (userInitiated)
            {
                FooterStatusText.Text = "已有一次刷新正在进行。";
            }
            return;
        }

        var selectedKey = (EndpointGrid.SelectedItem as EndpointRecord)?.IdentityKey;
        HeaderStatusText.Text = "正在读取系统端口表…";
        try
        {
            var snapshot = await Task.Run(_snapshotService.Capture);
            if (_isClosing) return;
            _allRows = snapshot;
            ApplyFilter(selectedKey);
            var now = DateTime.Now;
            HeaderStatusText.Text = $"已更新 {now:HH:mm:ss}";
            FooterStatusText.Text = $"共读取 {_allRows.Count} 条记录，当前显示 {_visibleRows.Count} 条";
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "刷新失败";
            FooterStatusText.Text = $"无法读取系统端口：{ex.Message}";
            if (userInitiated)
            {
                MessageBox.Show(this, $"读取端口信息失败：\n\n{ex.Message}", "PortViewer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _refreshCoordinator.Exit();
        }
    }

    private void ApplyFilter(string? selectedKey = null)
    {
        selectedKey ??= (EndpointGrid.SelectedItem as EndpointRecord)?.IdentityKey;
        var showAll = ScopeComboBox.SelectedIndex == 1;
        var filtered = EndpointFilter.Apply(_allRows, showAll, SearchBox.Text).ToArray();
        _visibleRows.Clear();
        foreach (var row in filtered)
        {
            _visibleRows.Add(row);
        }

        if (selectedKey is not null)
        {
            EndpointGrid.SelectedItem = _visibleRows.FirstOrDefault(x => x.IdentityKey == selectedKey);
        }
        FooterStatusText.Text = $"共读取 {_allRows.Count} 条记录，当前显示 {_visibleRows.Count} 条";
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (EndpointGrid is not null) ApplyFilter();
    }

    private void ScopeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointGrid is not null) ApplyFilter();
    }

    private void AutoRefreshCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_timer is null) return;
        if (AutoRefreshCheckBox.IsChecked == true && PortPage.Visibility == Visibility.Visible)
        {
            _timer.Start();
            HeaderStatusText.Text = "自动刷新已开启";
        }
        else
        {
            _timer.Stop();
            HeaderStatusText.Text = "自动刷新已暂停";
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync(true);

    private void EndpointGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        KillButton.IsEnabled = EndpointGrid.SelectedItem is EndpointRecord;
    }

    private async void EndpointGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EndpointGrid.SelectedItem is EndpointRecord)
        {
            await ShowSelectedDetailsAsync();
        }
    }

    private async Task ShowSelectedDetailsAsync()
    {
        if (EndpointGrid.SelectedItem is not EndpointRecord row) return;
        var affected = _allRows.Where(x => x.ProcessId == row.ProcessId)
            .OrderBy(x => x.LocalPort).ToArray();
        var detail = BuildImpactText(row, affected, includeQuestion: false);
        await Dispatcher.InvokeAsync(() => MessageBox.Show(this, detail, "端口详情",
            MessageBoxButton.OK, MessageBoxImage.Information));
    }

    private async void KillButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (EndpointGrid.SelectedItem is not EndpointRecord selected) return;

        var blocked = ProcessTerminationService.ValidateBasicTarget(selected.ProcessId, Environment.ProcessId);
        if (blocked is not null)
        {
            ShowResult(blocked);
            return;
        }

        KillButton.IsEnabled = false;
        FooterStatusText.Text = "正在重新核对端口归属…";
        try
        {
            var fresh = await Task.Run(_snapshotService.Capture);
            var current = fresh.FirstOrDefault(x => SameOwnedEndpoint(x, selected));
            if (current is null)
            {
                MessageBox.Show(this, "所选端口的归属已经变化，操作已取消。请刷新后重新选择。",
                    "端口已变化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var processInfo = _processInfoProvider.GetInfo(current.ProcessId);
            if (!processInfo.StartTimeUtcTicks.HasValue)
            {
                MessageBox.Show(this, "无法确认目标进程的启动时间。为防止 PID 复用导致误杀，操作已取消。",
                    "无法验证进程身份", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var affected = fresh.Where(x => x.ProcessId == current.ProcessId)
                .OrderBy(x => x.LocalPort).ThenBy(x => x.Protocol).ToArray();
            var answer = MessageBox.Show(this, BuildImpactText(current, affected, includeQuestion: true),
                "确认强制结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                FooterStatusText.Text = "用户已取消关闭操作。";
                return;
            }

            var finalSnapshot = await Task.Run(_snapshotService.Capture);
            if (!finalSnapshot.Any(x => SameOwnedEndpoint(x, current)))
            {
                MessageBox.Show(this, "确认期间端口归属发生了变化，操作已安全取消。",
                    "端口已变化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            FooterStatusText.Text = $"正在结束 PID {current.ProcessId}…";
            var result = await Task.Run(() => _terminationService.Terminate(
                current.ProcessId, processInfo.StartTimeUtcTicks.Value));

            if (result.Status == TerminationStatus.RequiresElevation)
            {
                result = await RunElevatedKillAsync(current.ProcessId, processInfo.StartTimeUtcTicks.Value);
            }

            ShowResult(result);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"关闭操作失败：\n\n{ex.Message}", "PortViewer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            KillButton.IsEnabled = EndpointGrid.SelectedItem is EndpointRecord;
        }
    }

    private static bool SameOwnedEndpoint(EndpointRecord left, EndpointRecord right)
    {
        return left.ProcessId == right.ProcessId && left.Protocol == right.Protocol &&
               left.LocalPort == right.LocalPort &&
               string.Equals(left.LocalAddress, right.LocalAddress, StringComparison.OrdinalIgnoreCase) &&
               left.RemotePort == right.RemotePort &&
               string.Equals(left.RemoteAddress, right.RemoteAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildImpactText(EndpointRecord selected, IReadOnlyCollection<EndpointRecord> affected,
        bool includeQuestion)
    {
        var service = string.IsNullOrWhiteSpace(selected.ServiceNames) ? "无关联服务" : selected.ServiceNames;
        var endpoints = affected.Take(18).Select(x =>
            $"  • {x.ProtocolText,-3} {x.LocalEndpoint}  {x.DisplayState}");
        var extra = affected.Count > 18 ? $"\n  …另有 {affected.Count - 18} 条记录" : string.Empty;
        var serviceWarning = string.IsNullOrWhiteSpace(selected.ServiceNames)
            ? string.Empty
            : "\n\n警告：这是 Windows 服务进程。强制结束可能中断系统功能，且服务可能自动重启。";
        var question = includeQuestion ? "\n\n是否确认强制结束该进程？" : string.Empty;

        return $"所选端口：{selected.ProtocolText} {selected.LocalEndpoint}\n" +
               $"目标进程：{selected.ProcessName}（PID {selected.ProcessId}）\n" +
               $"关联服务：{service}\n" +
               $"进程路径：{(string.IsNullOrWhiteSpace(selected.ExecutablePath) ? "无法读取" : selected.ExecutablePath)}\n\n" +
               $"该进程当前共有 {affected.Count} 条端口/连接记录，结束进程将同时影响：\n" +
               string.Join("\n", endpoints) + extra + serviceWarning + question;
    }

    private static async Task<TerminationResult> RunElevatedKillAsync(int processId, long expectedStartTicks)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new TerminationResult { Status = TerminationStatus.Failed, Message = "无法定位 PortViewer 可执行文件。" };
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--elevated-kill {processId} {expectedStartTicks}",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var helper = Process.Start(startInfo);
            if (helper is null)
            {
                return new TerminationResult { Status = TerminationStatus.Failed, Message = "无法启动管理员权限子进程。" };
            }
            await helper.WaitForExitAsync();
            return ElevatedKillExitCodes.ToResult(helper.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new TerminationResult { Status = TerminationStatus.Failed, Message = "用户取消了管理员权限请求。" };
        }
        catch (Exception ex)
        {
            return new TerminationResult { Status = TerminationStatus.Failed, Message = $"提权失败：{ex.Message}" };
        }
    }

    private void ShowResult(TerminationResult result)
    {
        FooterStatusText.Text = result.Message;
        MessageBox.Show(this, result.Message, result.IsSuccess ? "操作完成" : "操作未执行",
            MessageBoxButton.OK, result.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
