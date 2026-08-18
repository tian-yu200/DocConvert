using System;
using System.Windows;
using DocConvert.Infrastructure.Windows;

namespace DocConvert.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var workerIndex = Array.FindIndex(e.Args, argument => argument.Equals("--office-worker", StringComparison.OrdinalIgnoreCase));
        if (workerIndex >= 0 && workerIndex + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = OfficeWorkerHost.RunAsync(e.Args[workerIndex + 1]).GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);
        JobWorkspace.CleanupOld();
        var window = new MainWindow { DataContext = new MainViewModel() };
        MainWindow = window;
        window.Show();
    }
}
