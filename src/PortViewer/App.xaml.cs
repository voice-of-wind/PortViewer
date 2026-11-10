using System.Windows;
using System.Text.Json;
using PortViewer.Core.Interop;
using PortViewer.Core.Models;
using PortViewer.Core.Services;

namespace PortViewer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--scan-handles", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RunHandleScan(e.Args));
            return;
        }

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--elevated-kill", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = RunElevatedKill(e.Args);
            Shutdown(exitCode);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var initialFilePath = e.Args.Length == 2 &&
            string.Equals(e.Args[0], "--open-file-page", StringComparison.OrdinalIgnoreCase)
            ? e.Args[1] : null;
        MainWindow = new MainWindow(initialFilePath);
        MainWindow.Show();
    }

    private static int RunHandleScan(IReadOnlyList<string> args)
    {
        if (args.Count != 3) return 10;
        try
        {
            var result = new WindowsFileLockScanner().Scan(args[1]);
            File.WriteAllText(args[2], JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(args[2], JsonSerializer.Serialize(new FileLockScanResult
                {
                    TargetPath = args[1], IsPartial = true, Error = ex.Message
                }));
            }
            catch { }
            return 1;
        }
    }

    private static int RunElevatedKill(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || !int.TryParse(args[1], out var processId) ||
            !long.TryParse(args[2], out var expectedStartTicks))
        {
            return ElevatedKillExitCodes.InvalidArguments;
        }

        var result = new ProcessTerminationService().Terminate(processId, expectedStartTicks);
        return ElevatedKillExitCodes.FromResult(result);
    }
}
