using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PortViewer.Core.Interop;
using PortViewer.Core.Models;
using PortViewer.Core.Services;

namespace PortViewer;

public partial class MainWindow
{
    private string? _scannedFilePath;
    private bool _fileBusy;
    private readonly List<FileProcessRow> _fileRows = new();
    private readonly FileScanProcessRunner _fileScanRunner = new();

    private Task<FileLockScanResult> RunFileScanAsync(string target) =>
        _fileScanRunner.ScanAsync(Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 PortViewer 程序文件。"),
            target, TimeSpan.FromSeconds(30));

    private async void PortPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        PortPage.Visibility = Visibility.Visible;
        FilePage.Visibility = Visibility.Collapsed;
        PortPageButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
        PortPageButton.Foreground = System.Windows.Media.Brushes.White;
        FilePageButton.Background = System.Windows.Media.Brushes.White;
        FilePageButton.Foreground = (System.Windows.Media.Brush)FindResource("InkBrush");
        if (AutoRefreshCheckBox.IsChecked == true) _timer.Start();
        HeaderStatusText.Text = "端口占用页面";
        FooterStatusText.Text = $"共读取 {_allRows.Count} 条端口记录";
        if (_allRows.Count == 0) await RefreshAsync(true);
    }

    private void FilePageButton_OnClick(object sender, RoutedEventArgs e) => ShowFilePage();

    private void ShowFilePage()
    {
        _timer.Stop();
        PortPage.Visibility = Visibility.Collapsed;
        FilePage.Visibility = Visibility.Visible;
        FilePageButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
        FilePageButton.Foreground = System.Windows.Media.Brushes.White;
        PortPageButton.Background = System.Windows.Media.Brushes.White;
        PortPageButton.Foreground = (System.Windows.Media.Brush)FindResource("InkBrush");
        HeaderStatusText.Text = "文件占用页面";
        FooterStatusText.Text = "选择文件或文件夹，查看相关进程。";
    }

    private void FilePathBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _scannedFilePath = null;
        _fileRows.Clear();
        if (FileProcessGrid is not null) FileProcessGrid.ItemsSource = null;
        if (FileDetailsButton is not null) FileDetailsButton.IsEnabled = false;
        if (FileKillButton is not null) FileKillButton.IsEnabled = false;
        if (FileScanStatusText is not null) FileScanStatusText.Text = "路径已变化，请重新扫描。";
    }

    private void PickFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择要排查的文件", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) FilePathBox.Text = picker.FileName;
    }

    private void PickFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要排查的文件夹", UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            FilePathBox.Text = picker.SelectedPath;
    }

    private async void ScanFileButton_OnClick(object sender, RoutedEventArgs e) => await ScanFileAsync();

    private async Task ScanFileAsync()
    {
        if (_fileBusy) return;
        string target;
        try
        {
            target = FileLockPath.Normalize(FilePathBox.Text);
            if (!File.Exists(target) && !Directory.Exists(target))
                throw new FileNotFoundException("该文件或文件夹不存在。", target);
        }
        catch (Exception ex)
        {
            FileScanStatusText.Text = ex.Message;
            return;
        }

        _fileBusy = true;
        ScanFileButton.IsEnabled = false;
        FilePathBox.IsEnabled = PickFileButton.IsEnabled = PickFolderButton.IsEnabled = false;
        FileKillButton.IsEnabled = false;
        FileScanStatusText.Text = "正在扫描打开的文件句柄…";
        HeaderStatusText.Text = "正在排查文件占用…";
        try
        {
            var scan = await RunFileScanAsync(target);
            if (_isClosing || !string.Equals(target, FileLockPath.Normalize(FilePathBox.Text), StringComparison.OrdinalIgnoreCase))
                return;
            var processInfo = await Task.Run(() => BuildFileRows(scan));
            _fileRows.Clear();
            _fileRows.AddRange(processInfo);
            FileProcessGrid.ItemsSource = null;
            FileProcessGrid.ItemsSource = _fileRows;
            _scannedFilePath = target;
            var state = scan.IsPartial ? "扫描不完整" : "扫描完成";
            var detail = string.IsNullOrWhiteSpace(scan.Error) ? string.Empty : $"；{scan.Error}";
            if (scan.IsDirectory && !scan.HandleScanAvailable)
                FileScanStatusText.Text = $"无法扫描文件夹句柄{detail}";
            else if (_fileRows.Count == 0)
                FileScanStatusText.Text = $"{state}：未定位到相关进程。未找到不等于没有占用{detail}";
            else
                FileScanStatusText.Text = $"{state}：找到 {_fileRows.Count} 个相关进程。" +
                    (scan.UnreadableHandles > 0 ? $"有 {scan.UnreadableHandles} 个句柄无法读取。" : string.Empty) + detail;
            HeaderStatusText.Text = state;
            FooterStatusText.Text = "可先正常关闭相关程序；需要强制结束时请逐个核对。";
        }
        catch (Exception ex)
        {
            FileScanStatusText.Text = $"扫描失败：{ex.Message}";
            HeaderStatusText.Text = "文件扫描失败";
        }
        finally
        {
            _fileBusy = false;
            ScanFileButton.IsEnabled = true;
            FilePathBox.IsEnabled = PickFileButton.IsEnabled = PickFolderButton.IsEnabled = true;
            FileKillButton.IsEnabled = FileProcessGrid.SelectedItem is FileProcessRow row && row.StartTimeUtcTicks.HasValue;
        }
    }

    private List<FileProcessRow> BuildFileRows(FileLockScanResult scan)
    {
        var services = new WindowsServiceLookup().GetServicesByProcessId();
        IReadOnlyList<EndpointRecord> endpoints;
        try { endpoints = _snapshotService.Capture(); }
        catch { endpoints = Array.Empty<EndpointRecord>(); }
        return scan.Hits.GroupBy(h => h.ProcessId).Select(group =>
        {
            var info = _processInfoProvider.GetInfo(group.Key);
            var paths = group.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToArray();
            var ports = endpoints.Where(p => p.ProcessId == group.Key).ToArray();
            return new FileProcessRow
            {
                ProcessId = group.Key,
                StartTimeUtcTicks = info.StartTimeUtcTicks ?? group.Select(h => h.StartTimeUtcTicks).FirstOrDefault(t => t.HasValue),
                Name = info.Name,
                ExecutablePath = info.ExecutablePath,
                Services = services.TryGetValue(group.Key, out var names) ? string.Join("、", names) : "—",
                Paths = paths,
                Sources = group.Select(h => h.Source).Distinct().ToArray(),
                Ports = ports
            };
        }).OrderBy(r => r.Name).ThenBy(r => r.ProcessId).ToList();
    }

    private void FileProcessGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = FileProcessGrid.SelectedItem as FileProcessRow;
        FileDetailsButton.IsEnabled = row is not null;
        FileKillButton.IsEnabled = !_fileBusy && row?.StartTimeUtcTicks.HasValue == true;
    }

    private void FileDetailsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (FileProcessGrid.SelectedItem is not FileProcessRow row) return;
        MessageBox.Show(this, BuildFileImpactText(row, false), "相关进程详情",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string BuildFileImpactText(FileProcessRow row, bool ask)
    {
        var paths = string.Join("\n", row.Paths.Take(18).Select(p => "  • " + p));
        if (row.Paths.Length > 18) paths += $"\n  …另有 {row.Paths.Length - 18} 条路径";
        var ports = row.Ports.Length == 0 ? "  未查到端口记录" :
            string.Join("\n", row.Ports.Take(18).Select(p => $"  • {p.ProtocolText} {p.LocalEndpoint} {p.DisplayState}"));
        if (row.Ports.Length > 18) ports += $"\n  …另有 {row.Ports.Length - 18} 条记录";
        return $"进程：{row.Name}（PID {row.ProcessId}）\n" +
               $"程序路径：{(string.IsNullOrWhiteSpace(row.ExecutablePath) ? "无法读取" : row.ExecutablePath)}\n" +
               $"关联服务：{row.Services}\n来源：{string.Join("、", row.Sources)}\n\n" +
               $"匹配的路径：\n{paths}\n\n当前端口记录：\n{ports}\n\n" +
               "这些句柄是排查线索，不能单凭它们断定该进程阻止了操作。" +
               (ask ? "\n\n强制结束会关闭整个进程，影响其所有文件、端口与未保存的工作。确认继续？" : string.Empty);
    }

    private async void FileKillButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_fileBusy || FileProcessGrid.SelectedItem is not FileProcessRow selected ||
            selected.StartTimeUtcTicks is not long startTicks || _scannedFilePath is null) return;
        var blocked = ProcessTerminationService.ValidateBasicTarget(selected.ProcessId, Environment.ProcessId);
        if (blocked is not null) { ShowResult(blocked); return; }
        var target = _scannedFilePath;
        _fileBusy = true;
        FileKillButton.IsEnabled = false;
        ScanFileButton.IsEnabled = false;
        FilePathBox.IsEnabled = PickFileButton.IsEnabled = PickFolderButton.IsEnabled = false;
        try
        {
            FileScanStatusText.Text = "正在复查所选路径与进程…";
            var before = await RunFileScanAsync(target);
            if (!before.StillOwnedBy(selected.ProcessId, startTicks))
            {
                MessageBox.Show(this, "所选进程不再持有匹配资源，操作已取消。请重新扫描。",
                    "占用情况已变化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            IReadOnlyList<EndpointRecord> freshPorts = await Task.Run(_snapshotService.Capture);
            var reviewed = new FileProcessRow
            {
                ProcessId = selected.ProcessId, StartTimeUtcTicks = startTicks,
                Name = selected.Name, ExecutablePath = selected.ExecutablePath,
                Services = selected.Services,
                Paths = before.Hits.Where(h => h.ProcessId == selected.ProcessId)
                    .Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Sources = before.Hits.Where(h => h.ProcessId == selected.ProcessId)
                    .Select(h => h.Source).Distinct().ToArray(),
                Ports = freshPorts.Where(p => p.ProcessId == selected.ProcessId).ToArray()
            };
            if (MessageBox.Show(this, BuildFileImpactText(reviewed, true), "确认强制结束进程",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            var final = await RunFileScanAsync(target);
            if (!final.StillOwnedBy(selected.ProcessId, startTicks))
            {
                MessageBox.Show(this, "确认期间占用情况已变化，操作已取消。请重新扫描。",
                    "占用情况已变化", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var result = await Task.Run(() => _terminationService.Terminate(selected.ProcessId, startTicks));
            if (result.Status == TerminationStatus.RequiresElevation)
            {
                MessageBox.Show(this, "结束该进程需要管理员权限。请点“管理员模式重新扫描”，重新核对目标后再操作。",
                    "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else ShowResult(result);
            await ScanFileAsyncAfterKill();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"操作未执行：{ex.Message}", "文件占用排查",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _fileBusy = false;
            ScanFileButton.IsEnabled = true;
            FilePathBox.IsEnabled = PickFileButton.IsEnabled = PickFolderButton.IsEnabled = true;
            FileKillButton.IsEnabled = FileProcessGrid.SelectedItem is FileProcessRow row && row.StartTimeUtcTicks.HasValue;
        }
    }

    private async Task ScanFileAsyncAfterKill()
    {
        _fileBusy = false;
        await ScanFileAsync();
        FooterStatusText.Text = "请在文件管理器中重试原来的操作；PortViewer 不会自动删除或移动文件。";
        _fileBusy = true;
    }

    private void AdminScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        string target;
        try { target = FileLockPath.Normalize(FilePathBox.Text); }
        catch (Exception ex) { FileScanStatusText.Text = ex.Message; return; }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        try
        {
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--open-file-page");
            start.ArgumentList.Add(target);
            Process.Start(start);
            FooterStatusText.Text = "管理员窗口已打开；请在新窗口重新核对扫描结果。";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FooterStatusText.Text = "已取消管理员权限请求。";
        }
        catch (Exception ex) { FileScanStatusText.Text = $"无法打开管理员窗口：{ex.Message}"; }
    }

    private sealed class FileProcessRow
    {
        public int ProcessId { get; init; }
        public long? StartTimeUtcTicks { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ExecutablePath { get; init; } = string.Empty;
        public string Services { get; init; } = string.Empty;
        public string[] Paths { get; init; } = Array.Empty<string>();
        public string[] Sources { get; init; } = Array.Empty<string>();
        public EndpointRecord[] Ports { get; init; } = Array.Empty<EndpointRecord>();
        public int PortCount => Ports.Length;
        public string MatchedPathsText => Paths.Length == 0 ? "—" : string.Join("；", Paths.Take(3)) +
            (Paths.Length > 3 ? $"；另有 {Paths.Length - 3} 条" : string.Empty);
    }
}
