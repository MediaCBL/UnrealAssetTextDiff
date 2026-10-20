namespace UnrealAssetTextDiff;

public static class ExitCodes
{
    /// <summary>Exports produced; viewer launched if requested.</summary>
    public const int Success = 0;
    /// <summary>Invalid command line usage.</summary>
    public const int InvalidUsage = 1;
    /// <summary>Configuration error: missing/unresolvable project, engine, TortoiseMerge, or input paths.</summary>
    public const int ConfigError = 2;
    /// <summary>Export failed: commandlet returned non-zero, ingest helper failed, or exports missing.</summary>
    public const int ExportFailed = 3;
    /// <summary>Exports succeeded but TortoiseMerge could not be launched.</summary>
    public const int ViewerLaunchFailed = 4;
    /// <summary>Operation cancelled by the user.</summary>
    public const int Cancelled = 5;
    /// <summary>Exports were produced but the export-phase log shows missing/failed
    /// dependencies — text output is partial and must not be treated as a clean diff.</summary>
    public const int IncompleteExport = 6;
}
