namespace UnrealAssetTextDiff;

/// <summary>IProgress that writes status lines to the attached console.</summary>
public sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => NativeConsole.WriteLine(value);

    public static void WriteDiagnostics(DiffResult result)
    {
        if (result.ExitCode == ExitCodes.Success && result.SessionDir != null)
        {
            NativeConsole.WriteLine($"Exports: {result.SessionDir}");
        }
        foreach (var line in result.Diagnostics)
        {
            NativeConsole.WriteError(line);
        }
        if (result.ExitCode == ExitCodes.IncompleteExport)
        {
            NativeConsole.WriteError(
                "WARNING: INCOMPLETE EXPORT (exit 6). Text files were produced, " +
                "but the export log shows missing/failed dependencies. " +
                "Matching output is NOT proof the assets are identical.");
        }
        if (result.Limited && result.ExitCode == ExitCodes.Success)
        {
            NativeConsole.WriteError(
                "NOTE: limited export — the stock DiffAssets backend cannot " +
                "export Blueprint class defaults. Build the UatdTextDiff " +
                "plugin to include them.");
        }
    }
}
