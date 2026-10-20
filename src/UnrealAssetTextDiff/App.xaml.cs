using System.IO;
using System.Windows;

namespace UnrealAssetTextDiff;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CliOptions.Parse(e.Args);
        DiffSessionRunner.PruneSessions();
        if (options.LogArgsPath != null)
        {
            File.WriteAllLines(options.LogArgsPath, e.Args);
            Environment.Exit(ExitCodes.Success);
        }
        if (options.Ingest)
        {
            Environment.Exit(IngestRunner.Run(options));
        }
        if (!options.WantsGui)
        {
            if (options.HelpRequested)
            {
                NativeConsole.WriteLine(CliOptions.Usage);
                Environment.Exit(ExitCodes.Success);
            }
            if (options.Errors.Count > 0)
            {
                foreach (var err in options.Errors) NativeConsole.WriteError(err);
                Environment.Exit(ExitCodes.InvalidUsage);
            }
            var result = DiffSessionRunner.RunAsync(options, new ConsoleProgress(), CancellationToken.None)
                .GetAwaiter().GetResult();
            ConsoleProgress.WriteDiagnostics(result);
            Environment.Exit(result.ExitCode);
        }

        if (options.Errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, options.Errors),
                "UnrealAssetTextDiff", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var window = new MainWindow();
        window.ApplyCliOptions(options);
        window.Show();
    }
}
