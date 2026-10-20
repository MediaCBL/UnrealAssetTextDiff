using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace UnrealAssetTextDiff;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private string? _lastSessionDir;
    private readonly DispatcherTimer _elapsed = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _runStart;

    public MainWindow()
    {
        InitializeComponent();
        _elapsed.Tick += (_, _) =>
            StatusText.Text = $"Running… {(int)(DateTime.Now - _runStart).TotalSeconds}s";

        var settings = AppSettings.Load();
        ProjectBox.Text = settings.ProjectPath
            ?? ToolLocator.FindUprojectUpwards(Environment.CurrentDirectory) ?? "";
        EngineBox.Text = settings.EngineCmdPath
            ?? ToolLocator.DetectEngineCmdPaths().FirstOrDefault() ?? "";
        TortoiseBox.Text = settings.TortoiseMergePath
            ?? ToolLocator.DetectTortoiseMerge() ?? "";

        WireDrop(ProjectBox, p => ProjectBox.Text = p);
        WireDrop(EngineBox, p => EngineBox.Text = p);
        WireDrop(TortoiseBox, p => TortoiseBox.Text = p);
        WireDrop(BaseBox, p => { BaseBox.Text = p; AutoLabel(BaseLabelBox, p); });
        WireDrop(MineBox, p => { MineBox.Text = p; AutoLabel(MineLabelBox, p); });

        Closed += (_, _) => SaveSettings();
        DiffSessionRunner.PruneSessions();
    }

    /// <summary>Prefill fields when the app was launched with partial CLI args.</summary>
    public void ApplyCliOptions(CliOptions o)
    {
        if (o.Project != null) ProjectBox.Text = o.Project;
        if (o.Engine != null) EngineBox.Text = o.Engine;
        if (o.Tortoise != null) TortoiseBox.Text = o.Tortoise;
        if (o.Base != null) { BaseBox.Text = o.Base; AutoLabel(BaseLabelBox, o.Base); }
        if (o.Mine != null) { MineBox.Text = o.Mine; AutoLabel(MineLabelBox, o.Mine); }
        if (o.BaseLabel != null) BaseLabelBox.Text = o.BaseLabel;
        if (o.MineLabel != null) MineLabelBox.Text = o.MineLabel;
    }

    private void SaveSettings()
    {
        var s = AppSettings.Load();
        s.ProjectPath = ProjectBox.Text.Length > 0 ? ProjectBox.Text : s.ProjectPath;
        s.EngineCmdPath = EngineBox.Text.Length > 0 ? EngineBox.Text : s.EngineCmdPath;
        s.TortoiseMergePath = TortoiseBox.Text.Length > 0 ? TortoiseBox.Text : s.TortoiseMergePath;
        s.Save();
    }

    private static void WireDrop(TextBox box, Action<string> onPath)
    {
        box.PreviewDragOver += (_, e) =>
        {
            e.Handled = true;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        box.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                onPath(files[0]);
        };
    }

    private static void AutoLabel(TextBox labelBox, string path)
    {
        if (labelBox.Text.Length == 0)
            labelBox.Text = Path.GetFileNameWithoutExtension(path);
    }

    private void BrowseFor(TextBox box, string filter, TextBox? labelBox = null)
    {
        var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dlg.ShowDialog(this) == true)
        {
            box.Text = dlg.FileName;
            if (labelBox != null) AutoLabel(labelBox, dlg.FileName);
        }
    }

    private void BrowseProject(object s, RoutedEventArgs e) =>
        BrowseFor(ProjectBox, "Unreal project (*.uproject)|*.uproject");
    private void BrowseEngine(object s, RoutedEventArgs e) =>
        BrowseFor(EngineBox, "UnrealEditor-Cmd.exe|UnrealEditor-Cmd.exe|Executables (*.exe)|*.exe");
    private void BrowseTortoise(object s, RoutedEventArgs e) =>
        BrowseFor(TortoiseBox, "TortoiseMerge.exe|TortoiseMerge.exe|Executables (*.exe)|*.exe");
    private void BrowseBase(object s, RoutedEventArgs e) =>
        BrowseFor(BaseBox, "Unreal assets (*.uasset)|*.uasset|All files (*.*)|*.*", BaseLabelBox);
    private void BrowseMine(object s, RoutedEventArgs e) =>
        BrowseFor(MineBox, "Unreal assets (*.uasset)|*.uasset|All files (*.*)|*.*", MineLabelBox);

    private void SwapSides(object s, RoutedEventArgs e)
    {
        (BaseBox.Text, MineBox.Text) = (MineBox.Text, BaseBox.Text);
        (BaseLabelBox.Text, MineLabelBox.Text) = (MineLabelBox.Text, BaseLabelBox.Text);
    }

    private void CompareClick(object s, RoutedEventArgs e) => Run(exportOnly: false);
    private void ExportOnlyClick(object s, RoutedEventArgs e) => Run(exportOnly: true);
    private void CancelClick(object s, RoutedEventArgs e) => _cts?.Cancel();

    private void OpenFolderClick(object s, RoutedEventArgs e)
    {
        if (_lastSessionDir != null && Directory.Exists(_lastSessionDir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastSessionDir}\"")
            { UseShellExecute = false });
    }

    private async void Run(bool exportOnly)
    {
        if (BaseBox.Text.Length == 0 || MineBox.Text.Length == 0)
        {
            StatusText.Text = "Select both assets first.";
            return;
        }
        SetRunning(true);
        _cts = new CancellationTokenSource();
        _runStart = DateTime.Now;
        _elapsed.Start();
        Log($"--- {(exportOnly ? "Export only" : "Compare")} started {DateTime.Now:T} ---");

        var options = CliOptions.FromFields(ProjectBox.Text, EngineBox.Text, TortoiseBox.Text,
            BaseBox.Text, MineBox.Text, BaseLabelBox.Text, MineLabelBox.Text,
            outputDir: null, exportOnly, filtered: FilteredCheck.IsChecked == true);
        var progress = new Progress<string>(Log);
        try
        {
            var result = await DiffSessionRunner.RunAsync(options, progress, _cts.Token);
            foreach (var d in result.Diagnostics) Log(d);
            _lastSessionDir = result.SessionDir;
            OpenFolderButton.IsEnabled = _lastSessionDir != null;
            StatusText.Text = result.ExitCode switch
            {
                ExitCodes.Success when result.IdenticalInputs => "Inputs identical.",
                ExitCodes.Success when exportOnly && result.Limited =>
                    "Exports written (LIMITED backend — no class defaults).",
                ExitCodes.Success when exportOnly => "Exports written.",
                ExitCodes.Success when result.Limited =>
                    "Viewer launched (LIMITED backend — no class defaults).",
                ExitCodes.Success => "Viewer launched.",
                ExitCodes.IncompleteExport => "INCOMPLETE export (missing dependencies) — see log.",
                ExitCodes.ConfigError => "Configuration error — see log.",
                ExitCodes.ExportFailed => "Export failed — see log.",
                ExitCodes.ViewerLaunchFailed => "Viewer launch failed — see log.",
                ExitCodes.Cancelled => "Cancelled.",
                _ => $"Exit {result.ExitCode}.",
            };
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            Log("Unexpected error: " + ex.Message);
            StatusText.Text = "Failed — see log.";
        }
        finally
        {
            _elapsed.Stop();
            SetRunning(false);
            SaveSettings();
        }
    }

    private void SetRunning(bool running)
    {
        CompareButton.IsEnabled = !running;
        ExportOnlyButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        Progress.IsIndeterminate = running;
    }

    private void Log(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
