using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnrealAssetTextDiff;

public sealed class DiffResult
{
    public int ExitCode;
    public string? SessionDir;
    public string? BaseCompare;
    public string? MineCompare;
    public string? BaseRaw;
    public string? MineRaw;
    public bool IdenticalInputs;
    /// <summary>Exports were produced but the commandlet log shows load/dependency
    /// failures during the export phase — the text may omit content.</summary>
    public bool Incomplete;
    /// <summary>The limited backend (stock DiffAssets) ran — no supplemental
    /// class-default export. Defaults sections in the compare files say so.</summary>
    public bool Limited;
    /// <summary>Class-default exports were produced for BOTH sides.</summary>
    public bool DefaultsIncluded;
    /// <summary>Semantic per-side defaults status (see ClassifyDefaults).</summary>
    public DefaultsStatus DefaultsBaseStatus;
    public DefaultsStatus DefaultsMineStatus;
    /// <summary>The plugin backend ran but at least one side's defaults
    /// payload is unavailable/failed — treated as incomplete.</summary>
    public bool DefaultsUnavailable;
    /// <summary>The aggressive filtered normalization view was applied.</summary>
    public bool Filtered;
    /// <summary>UnrealEditor-Cmd's process exit code, preserved for diagnostics.</summary>
    public int EngineExitCode = -1;
    /// <summary>Raw .t3d exports compare byte-identical.</summary>
    public bool RawExportsIdentical;
    /// <summary>Comparison copies (post-normalization, incl. defaults) compare identical.</summary>
    public bool CompareIdentical;
    public List<string> Diagnostics = new();
}

/// <summary>State passed to the ingest helper (via request.json + env var).</summary>
public sealed class IngestRequest
{
    [JsonPropertyName("sessionDir")] public string SessionDir { get; set; } = "";
    /// <summary>Per-session token; the ingest helper echoes it in result.json.</summary>
    [JsonPropertyName("sessionTag")] public string SessionTag { get; set; } = "";
    [JsonPropertyName("baseLabel")] public string BaseLabel { get; set; } = "";
    [JsonPropertyName("mineLabel")] public string MineLabel { get; set; } = "";
    /// <summary>Apply the filtered (identifier-noise) normalization to compare copies.</summary>
    [JsonPropertyName("filtered")] public bool Filtered { get; set; }
    /// <summary>Deterministic paths where Unreal writes the exports —
    /// preferred over re-joining argv tokens at .t3d boundaries.</summary>
    [JsonPropertyName("baseExpectedT3d")] public string? BaseExpectedT3d { get; set; }
    [JsonPropertyName("mineExpectedT3d")] public string? MineExpectedT3d { get; set; }
    /// <summary>Expected class-default exports (UatdAssetDiff commandlet).</summary>
    [JsonPropertyName("baseExpectedDefaults")] public string? BaseExpectedDefaults { get; set; }
    [JsonPropertyName("mineExpectedDefaults")] public string? MineExpectedDefaults { get; set; }
}

/// <summary>Semantic status of one side's supplemental defaults export.
/// File arrival is not success — see ClassifyDefaults.</summary>
public enum DefaultsStatus
{
    /// <summary>Stock backend (limited): no defaults were expected.</summary>
    NotRequested = 0,
    /// <summary>A valid defaults payload was produced.</summary>
    Included = 1,
    /// <summary>The package legitimately contains no Blueprint class —
    /// defaults are not applicable (not a failure).</summary>
    NotABlueprint = 2,
    /// <summary>The plugin ran but produced no usable payload: missing file,
    /// NoDefaultObject, a NoBlueprintGeneratedClass marker contradicting the
    /// raw export, or a malformed file. Treated as incomplete.</summary>
    Unavailable = 3,
}

public sealed class IngestResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("sessionTag")] public string? SessionTag { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("baseDefaultsStatus")] public int BaseDefaultsStatus { get; set; }
    [JsonPropertyName("mineDefaultsStatus")] public int MineDefaultsStatus { get; set; }
}

/// <summary>
/// Runs one comparison session:
///   stage inputs → request.json → UnrealEditor-Cmd -run=DiffAssets
///   (DiffCmd = this exe in --ingest mode) → wait for result.json or
///   fall back to reading the deterministic Saved/Diff outputs directly.
/// </summary>
public static class DiffSessionRunner
{
    public const string SessionEnvVar = "UATD_SESSION_DIR";
    private const int IngestGraceSec = 20;
    private const int MaxSessionsKept = 25;

    public static async Task<DiffResult> RunAsync(
        CliOptions o, IProgress<string> progress, CancellationToken ct)
    {
        var result = new DiffResult();
        var settings = AppSettings.Load();

        // ---- resolve configuration (explicit > saved > single detected) ----
        string? project = o.Project ?? settings.ProjectPath;
        string? engine = ResolveEngine(o.Engine ?? settings.EngineCmdPath, result.Diagnostics);
        string? tortoise = o.ExportOnly ? (o.Tortoise ?? settings.TortoiseMergePath)
                                        : ResolveTortoise(o.Tortoise ?? settings.TortoiseMergePath, result.Diagnostics);

        if (project == null || !File.Exists(project))
        {
            result.Diagnostics.Add($"Config error: project file not found: {project ?? "(none specified)"}");
            result.ExitCode = ExitCodes.ConfigError; return result;
        }
        if (engine == null)
        {
            result.Diagnostics.Add("Config error: no UnrealEditor-Cmd.exe. Specify --engine or save one in the app.");
            result.ExitCode = ExitCodes.ConfigError; return result;
        }
        if (o.Base == null || o.Mine == null || !File.Exists(o.Base) || !File.Exists(o.Mine))
        {
            result.Diagnostics.Add("Config error: input asset not found " +
                $"({(o.Base != null && !File.Exists(o.Base) ? o.Base : o.Mine)}).");
            result.ExitCode = ExitCodes.ConfigError; return result;
        }
        if (!o.ExportOnly && tortoise == null)
        {
            result.Diagnostics.Add("Config error: TortoiseMerge.exe not found. Specify --tortoise or save one.");
            result.ExitCode = ExitCodes.ConfigError; return result;
        }

        // Persist resolved configuration for next time / saved profile use.
        settings.ProjectPath = project;
        settings.EngineCmdPath = engine;
        if (tortoise != null) settings.TortoiseMergePath = tortoise;
        settings.Save();

        // ---- identical-input fast path ----
        string baseHash = Staging.Sha256OfFile(o.Base);
        string mineHash = Staging.Sha256OfFile(o.Mine);
        if (baseHash == mineHash && !o.ExportOnly)
        {
            result.IdenticalInputs = true;
            result.Diagnostics.Add("Inputs are identical (SHA-256 match); nothing to diff. " +
                "Use export-only mode to still produce text exports.");
            return result;
        }

        // ---- session ----
        string root = o.OutputDir ?? AppSettings.SessionsRoot;
        string sessionTag = Guid.NewGuid().ToString("N")[..8];
        string sessionDir = Path.Combine(root,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{sessionTag}");
        string stageDir = Path.Combine(sessionDir, "stage");
        string exportDir = Path.Combine(sessionDir, "export");
        string logDir = Path.Combine(sessionDir, "logs");
        Directory.CreateDirectory(exportDir);
        Directory.CreateDirectory(logDir);
        result.SessionDir = sessionDir;
        progress.Report($"Session: {sessionDir}");
        var sessionStartUtc = DateTime.UtcNow;

        var stagedBase = Staging.Stage(o.Base, o.BaseLabel, "base", stageDir, sessionTag);
        var stagedMine = Staging.Stage(o.Mine, o.MineLabel, "mine", stageDir, sessionTag);
        if (stagedBase == null || stagedMine == null)
        {
            result.Diagnostics.Add("Config error: failed to stage an input file.");
            result.ExitCode = ExitCodes.ConfigError; return result;
        }
        progress.Report($"Staged base -> {stagedBase.StagedStem}.uasset");
        progress.Report($"Staged mine -> {stagedMine.StagedStem}.uasset");

        string baseLabel = o.BaseLabel ?? Path.GetFileName(o.Base);
        string mineLabel = o.MineLabel ?? Path.GetFileName(o.Mine);

        string diffDir = DiffDir(project);
        string projectDir = Path.GetDirectoryName(Path.GetFullPath(project))!;

        // The UatdTextDiff editor plugin provides -run=UatdAssetDiff: the same
        // pipeline plus a per-revision class-default export. Without it we
        // fall back to the stock -run=DiffAssets, a clearly labelled limited
        // export (no variable-default coverage).
        string pluginDll = Path.Combine(projectDir, "Plugins", "UatdTextDiff",
            "Binaries", "Win64", "UnrealEditor-UatdTextDiff.dll");
        bool defaultsBackend = File.Exists(pluginDll);
        result.Limited = !defaultsBackend;
        result.Filtered = o.Filtered;
        if (!defaultsBackend)
        {
            result.Diagnostics.Add(
                "Limited export: UatdTextDiff editor plugin not built " +
                "(Plugins/UatdTextDiff). Blueprint class defaults are NOT " +
                "included — variable-default changes may be invisible. " +
                "Build it with: UnrealBuildTool.exe <ProjectTarget>Editor " +
                "Win64 Development -Project=<uproject> " +
                "-Plugin=<project>/Plugins/UatdTextDiff/UatdTextDiff.uplugin");
        }

        var request = new IngestRequest
        {
            SessionDir = sessionDir,
            SessionTag = sessionTag,
            BaseLabel = baseLabel,
            MineLabel = mineLabel,
            Filtered = o.Filtered,
            BaseExpectedT3d = Path.Combine(diffDir, stagedBase.StagedStem + ".uasset.t3d"),
            MineExpectedT3d = Path.Combine(diffDir, stagedMine.StagedStem + ".uasset.t3d"),
            BaseExpectedDefaults = defaultsBackend
                ? Path.Combine(diffDir, stagedBase.StagedStem + ".defaults.t3d") : null,
            MineExpectedDefaults = defaultsBackend
                ? Path.Combine(diffDir, stagedMine.StagedStem + ".defaults.t3d") : null,
        };
        File.WriteAllText(Path.Combine(sessionDir, "request.json"),
            JsonSerializer.Serialize(request));

        // ---- command line (see docs: DiffCmd value cannot contain quotes) ----
        string helperUrl = ShortPath.ForDiffCmd(Environment.ProcessPath!);
        string cmdLog = Path.Combine(logDir, "commandlet.log");
        string arguments = BuildCommandletArguments(project, stagedBase.StagedPath,
            stagedMine.StagedPath, helperUrl, cmdLog, defaultsBackend);
        progress.Report($"Launching: {engine}");
        progress.Report($"Arguments: {arguments}");

        try
        {
            int engineExit = await RunEngineAsync(engine, arguments, sessionDir,
                TimeSpan.FromSeconds(o.TimeoutSec), progress, ct).ConfigureAwait(false);
        if (engineExit == ExitCodes.Cancelled)
        {
            result.ExitCode = ExitCodes.Cancelled;
            result.Diagnostics.Add("Cancelled by user.");
            return result;
        }
        result.EngineExitCode = engineExit;
        // NOTE: the process exit code is NOT a reliable verdict. UE folds every
        // Error-level log line into it ("Failure - N error(s)") — e.g. compile
        // errors inside a transitively loaded dependency — even when both
        // exports were written. Export arrival is authoritative; the exit code
        // and phase-scoped log scan decide completeness afterwards.
        if (engineExit != 0)
            result.Diagnostics.Add($"UnrealEditor-Cmd exited with code {engineExit} " +
                "(engine error counting; checking whether exports still arrived).");
        progress.Report("Unreal export finished; collecting exports.");
        result.Diagnostics.AddRange(ExtractLoadDiagnostics(cmdLog));

        // ---- collect: prefer the ingest helper's result.json, else fall back ----
        string resultFile = Path.Combine(sessionDir, "result.json");
        IngestResult? ingest = await WaitForResultFileAsync(resultFile,
            TimeSpan.FromSeconds(IngestGraceSec), ct).ConfigureAwait(false);
        bool haveExports;
        if (ingest != null)
        {
            if (!ingest.Ok)
            {
                result.ExitCode = ExitCodes.ExportFailed;
                result.Diagnostics.Add($"Ingest failed: {ingest.Error}");
                return result;
            }
            if (ingest.SessionTag != sessionTag)
            {
                result.ExitCode = ExitCodes.ExportFailed;
                result.Diagnostics.Add(
                    $"Result marker belongs to another session ({ingest.SessionTag}); refusing stale output.");
                return result;
            }
            result.DefaultsBaseStatus = (DefaultsStatus)ingest.BaseDefaultsStatus;
            result.DefaultsMineStatus = (DefaultsStatus)ingest.MineDefaultsStatus;
            haveExports = true;
        }
        else
        {
            progress.Report("Ingest helper did not report; reading Saved/Diff directly.");
            var dsBase = DefaultsStatus.NotRequested;
            var dsMine = DefaultsStatus.NotRequested;
            haveExports = CollectFromDiffDir(project, stagedBase, stagedMine, request,
                exportDir, sessionStartUtc, ref dsBase, ref dsMine, result.Diagnostics);
            result.DefaultsBaseStatus = dsBase;
            result.DefaultsMineStatus = dsMine;
            if (!haveExports)
            {
                result.ExitCode = ExitCodes.ExportFailed;
                result.Diagnostics.Add($"Expected exports not found under {DiffDir(project)}. See {cmdLog}.");
                result.Diagnostics.AddRange(ExtractLogDiagnostics(cmdLog));
                return result;
            }
        }
        FillPaths(result, exportDir);

        // Supplemental class defaults are appended to the comparison copies
        // under a clear marker so a TortoiseMerge-only user sees them too.
        result.DefaultsIncluded = result.DefaultsBaseStatus == DefaultsStatus.Included &&
                                  result.DefaultsMineStatus == DefaultsStatus.Included;
        result.DefaultsUnavailable =
            result.DefaultsBaseStatus == DefaultsStatus.Unavailable ||
            result.DefaultsMineStatus == DefaultsStatus.Unavailable;
        foreach (var (cmp, raw, ds, stem) in new[]
        {
            (result.BaseCompare!, Path.Combine(exportDir, "base.defaults.t3d"),
                result.DefaultsBaseStatus, stagedBase.StagedStem),
            (result.MineCompare!, Path.Combine(exportDir, "mine.defaults.t3d"),
                result.DefaultsMineStatus, stagedMine.StagedStem),
        })
            AppendDefaultsSection(cmp, raw, ds, result.Limited, stem);

        if (result.DefaultsUnavailable)
            result.Diagnostics.Add("Class-default export unavailable or invalid " +
                "for a Blueprint input (missing file, NoDefaultObject, or a " +
                "malformed/contradictory defaults payload).");
        if (result.DefaultsBaseStatus == DefaultsStatus.NotABlueprint &&
            result.DefaultsMineStatus == DefaultsStatus.NotABlueprint)
            result.Diagnostics.Add("No Blueprint class in either input — " +
                "class defaults not applicable.");

        ReportHashCheck(result, o.Base, baseHash, o.Mine, mineHash);
        FinishOutcome(result, cmdLog, stagedBase, stagedMine, engineExit);

        result.RawExportsIdentical =
            File.ReadAllBytes(result.BaseRaw!).SequenceEqual(File.ReadAllBytes(result.MineRaw!));
        result.CompareIdentical =
            File.ReadAllText(result.BaseCompare!) == File.ReadAllText(result.MineCompare!);
        result.Diagnostics.Add(
            $"Binary inputs {(baseHash == mineHash ? "identical" : "differ")}; " +
            $"raw exports {(result.RawExportsIdentical ? "identical" : "differ")}; " +
            $"comparison copies {(result.CompareIdentical ? "identical" : "differ")}" +
            (result.Filtered ? " (filtered view)" : "") + ".");

        if (!o.ExportOnly)
        {
            string tag = result.ExitCode == ExitCodes.IncompleteExport
                ? " [INCOMPLETE EXPORT]"
                : result.Limited ? " [LIMITED EXPORT - no class defaults]" : "";
            string? err = TortoiseMergeLauncher.TryLaunch(tortoise!, result.BaseCompare!,
                result.MineCompare!, baseLabel + tag, mineLabel + tag);
            if (err != null)
            {
                result.ExitCode = ExitCodes.ViewerLaunchFailed;
                result.Diagnostics.Add($"Viewer launch failed: {err}");
                return result;
            }
            progress.Report("TortoiseMerge launched.");
        }
        return result;
        }
        finally
        {
            // The commandlet copies our staged inputs into <Project>/Saved/Diff
            // and leaves them. Stems carry this session's tag, so removing
            // them can never touch another comparison's files.
            CleanupDiffDir(diffDir, stagedBase, stagedMine);
        }
    }

    /// <summary>Append the supplemental class-default dump (or an explicit
    /// status marker) to a comparison copy, under a clear section header.
    /// The staged stem inside Class= paths is normalized the same way as the
    /// main export.</summary>
    private static void AppendDefaultsSection(string comparePath, string? defaultsPath,
        DefaultsStatus status, bool limited, string stagedStem)
    {
        var sb = new StringBuilder();
        sb.Append("\n; ============================================\n");
        sb.Append("; CLASS DEFAULTS");
        switch (status)
        {
            case DefaultsStatus.Included when defaultsPath != null && File.Exists(defaultsPath):
                sb.Append(" (UatdAssetDiff supplemental export)\n");
                sb.Append("; ============================================\n");
                sb.Append(ExportNormalizer.Normalize(
                    File.ReadAllText(defaultsPath), stagedStem));
                break;
            case DefaultsStatus.NotABlueprint:
                sb.Append(" — not applicable (no Blueprint generated class in this asset).\n");
                sb.Append("; ============================================\n");
                break;
            case DefaultsStatus.Unavailable:
                sb.Append(" UNAVAILABLE — the supplemental exporter produced no usable\n");
                sb.Append("; defaults payload for this asset (see commandlet.log); the\n");
                sb.Append("; export is INCOMPLETE — matching text is not proof of identity.\n");
                sb.Append("; ============================================\n");
                break;
            default: // NotRequested — limited backend
                sb.Append(" UNAVAILABLE — ");
                sb.Append(limited
                    ? "limited backend (UatdTextDiff plugin not built); variable-default changes may be invisible.\n"
                    : "the supplemental exporter produced no defaults file for this asset.\n");
                sb.Append("; ============================================\n");
                break;
        }
        File.AppendAllText(comparePath, sb.ToString());
    }

    private static void CleanupDiffDir(string diffDir, params StagedAsset[] staged)
    {
        foreach (var s in staged)
        {
            foreach (var ext in new[] { ".uasset.t3d", ".defaults.t3d", ".uasset", ".uexp", ".uptnl", ".ubulk" })
            {
                try { File.Delete(Path.Combine(diffDir, s.StagedStem + ext)); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Success vs incomplete-export classification. Exports may be produced
    /// while dependencies failed to load — never present that as a plain
    /// success. Compare-mode viewers still open (partial text is inspectable),
    /// but the result is marked and the exit code is non-success.
    /// </summary>
    /// <summary>Semantic classification of a supplemental defaults file —
    /// file ARRIVAL alone is not success. A valid payload starts with the
    /// UatdClassDefaults header and carries Class=/PropertyCount=; the
    /// commandlet's marker lines record why defaults are absent, and a
    /// NoBlueprintGeneratedClass marker that contradicts a raw export
    /// containing BlueprintGeneratedClass objects is an exporter failure,
    /// not a legitimately non-Blueprint asset.</summary>
    public static DefaultsStatus ClassifyDefaults(string? defaultsPath, string? rawT3dPath)
    {
        if (defaultsPath == null || !File.Exists(defaultsPath) ||
            new FileInfo(defaultsPath).Length == 0)
            return DefaultsStatus.Unavailable;
        string text;
        try { text = File.ReadAllText(defaultsPath); }
        catch { return DefaultsStatus.Unavailable; }
        if (text.Contains("NoDefaultObject"))
            return DefaultsStatus.Unavailable;
        if (text.Contains("NoBlueprintGeneratedClass"))
        {
            bool rawShowsBlueprint = false;
            try
            {
                rawShowsBlueprint = rawT3dPath != null && File.Exists(rawT3dPath) &&
                    File.ReadAllText(rawT3dPath)
                        .Contains("BlueprintGeneratedClass", StringComparison.Ordinal);
            }
            catch { rawShowsBlueprint = true; } // unreadable: stay conservative
            return rawShowsBlueprint ? DefaultsStatus.Unavailable
                                     : DefaultsStatus.NotABlueprint;
        }
        return text.Contains("Class=") && text.Contains("PropertyCount=")
            ? DefaultsStatus.Included : DefaultsStatus.Unavailable;
    }

    /// <summary>Combine the process outcome with export arrival. A nonzero or
    /// crashed engine exit is NEVER ordinary success merely because files
    /// were written — it is incomplete (viewable but flagged). Cancelled and
    /// config failures are handled before this point.</summary>
    public static int ClassifyProcessOutcome(int engineExit, bool exportsArrived) =>
        !exportsArrived ? ExitCodes.ExportFailed
        : engineExit != 0 ? ExitCodes.IncompleteExport
        : ExitCodes.Success;

    /// <summary>
    /// Success vs incomplete-export classification. Exports may be produced
    /// while dependencies failed to load — never present that as a plain
    /// success. Compare-mode viewers still open (partial text is inspectable),
    /// but the result is marked and the exit code is non-success.
    /// </summary>
    /// <summary>
    /// Success vs incomplete-export classification. Exports may be produced
    /// while dependencies failed to load — never present that as a plain
    /// success. Three things force incomplete status even with files on disk:
    /// export-phase log failures, a semantically-unavailable defaults payload
    /// under the plugin backend, and an abnormal/nonzero engine exit (a
    /// crashed process cannot be ordinary success). The viewer still opens —
    /// partial text is inspectable — but the result is marked and the exit
    /// code is non-success.
    /// </summary>
    private static void FinishOutcome(DiffResult result, string cmdLog,
        StagedAsset stagedBase, StagedAsset stagedMine, int engineExit)
    {
        var scan = ScanExportPhaseFailures(cmdLog,
            stagedBase.StagedStem, stagedMine.StagedStem);
        foreach (var n in scan.Notes)
            result.Diagnostics.Add("  unrelated-asset warning: " + n);
        bool incomplete = scan.Failures.Count > 0;
        foreach (var f in scan.Failures) result.Diagnostics.Add("  export-phase: " + f);

        if (result.DefaultsUnavailable)
        {
            incomplete = true;
            result.Diagnostics.Add("  defaults: supplemental class-default " +
                "export did not produce a usable payload.");
        }

        // Abnormal process outcome is never silently folded into success:
        // exports may be valid, but an engine that exited nonzero or crashed
        // leaves the completeness of its output unestablished.
        if (ClassifyProcessOutcome(engineExit, exportsArrived: true) ==
            ExitCodes.IncompleteExport)
        {
            incomplete = true;
            result.Diagnostics.Add($"  process: UnrealEditor-Cmd exited " +
                $"abnormally (code {engineExit}) after producing exports.");
        }

        result.Incomplete = incomplete;
        if (incomplete)
        {
            result.ExitCode = ExitCodes.IncompleteExport;
            result.Diagnostics.Add("INCOMPLETE EXPORT: load/dependency errors, " +
                "unavailable defaults, or an abnormal engine exit. The exported " +
                "text may omit content — do not treat matching output as proof " +
                "the assets are identical.");
        }
        else
        {
            result.ExitCode = ExitCodes.Success;
        }
    }

    /// <summary>
    /// Builds the UnrealEditor-Cmd argument string. The DiffCmd value must be
    /// a single token whose VALUE is quoted (DiffCmd="...") — the commandlet
    /// parses tokens itself and any quote inside the value breaks it. The
    /// helper URL therefore must not contain quotes; everything before {1}
    /// is treated as the executable, so all ingest arguments come after.
    /// </summary>
    public static string BuildCommandletArguments(string project, string staged1,
        string staged2, string helperUrl, string? absLogPath = null,
        bool defaultsBackend = false)
    {
        // The engine writes its own log file (-abslog). We deliberately do NOT
        // redirect stdout: detached engine children (e.g. UnrealTraceServer)
        // inherit the pipe and keep it open forever, which would hang the wait.
        // -run=UatdAssetDiff (UatdTextDiff plugin) additionally writes
        // <stem>.defaults.t3d class-default exports beside the .t3d files.
        string args = $"\"{project}\" -run={(defaultsBackend ? "UatdAssetDiff" : "DiffAssets")} " +
                      $"\"{staged1}\" \"{staged2}\" " +
                      $"DiffCmd=\"{helperUrl} {{1}} {{2}} --ingest\" " +
                      "-unattended -nullrhi";
        if (absLogPath != null) args += $" -abslog=\"{absLogPath}\"";
        return args;
    }

    private static void FillPaths(DiffResult r, string exportDir)
    {
        r.BaseCompare = Path.Combine(exportDir, "base.t3d");
        r.MineCompare = Path.Combine(exportDir, "mine.t3d");
        r.BaseRaw = Path.Combine(exportDir, "base.raw.t3d");
        r.MineRaw = Path.Combine(exportDir, "mine.raw.t3d");
    }

    private static void ReportHashCheck(DiffResult r, string basePath, string baseHash,
        string minePath, string mineHash)
    {
        if (Staging.Sha256OfFile(basePath) != baseHash || Staging.Sha256OfFile(minePath) != mineHash)
            r.Diagnostics.Add("WARNING: an input file changed during the export run.");
        else
            r.Diagnostics.Add("Input hashes unchanged.");
    }

    public static string DiffDir(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "Saved", "Diff");

    /// <summary>Parent-side fallback when the ingest helper never reported.</summary>
    private static bool CollectFromDiffDir(string project, StagedAsset b, StagedAsset m,
        IngestRequest req, string exportDir, DateTime sessionStartUtc,
        ref DefaultsStatus defaultsBase, ref DefaultsStatus defaultsMine,
        List<string> diagnostics)
    {
        string diffDir = DiffDir(project);
        string t1 = req.BaseExpectedT3d ?? Path.Combine(diffDir, b.StagedStem + ".uasset.t3d");
        string t2 = req.MineExpectedT3d ?? Path.Combine(diffDir, m.StagedStem + ".uasset.t3d");
        if (!File.Exists(t1) || !File.Exists(t2)) return false;

        // Never accept output that predates this session — it must have been
        // written by this run (stems embed the session tag, this is belt & braces).
        if (File.GetLastWriteTimeUtc(t1) < sessionStartUtc ||
            File.GetLastWriteTimeUtc(t2) < sessionStartUtc)
        {
            diagnostics.Add("Found expected export names in Saved/Diff but they predate " +
                "this session; refusing stale output.");
            return false;
        }

        string rawBase = Path.Combine(exportDir, "base.raw.t3d");
        string rawMine = Path.Combine(exportDir, "mine.raw.t3d");
        File.Copy(t1, rawBase, overwrite: true);
        File.Copy(t2, rawMine, overwrite: true);
        ExportNormalizer.WriteNormalizedCopy(rawBase, b.StagedStem,
            Path.Combine(exportDir, "base.t3d"), req.Filtered);
        ExportNormalizer.WriteNormalizedCopy(rawMine, m.StagedStem,
            Path.Combine(exportDir, "mine.t3d"), req.Filtered);

        // Supplemental defaults sit beside the .t3d exports when the
        // UatdAssetDiff commandlet produced them.
        // Supplemental defaults sit beside the .t3d exports when the
        // UatdAssetDiff commandlet produced them. Presence alone is not
        // success — the payload is classified semantically (and must not
        // predate this session).
        string d1 = req.BaseExpectedDefaults ?? Path.Combine(diffDir, b.StagedStem + ".defaults.t3d");
        string d2 = req.MineExpectedDefaults ?? Path.Combine(diffDir, m.StagedStem + ".defaults.t3d");
        if (req.BaseExpectedDefaults != null)
        {
            bool fresh = File.Exists(d1) && File.GetLastWriteTimeUtc(d1) >= sessionStartUtc;
            if (fresh)
                File.Copy(d1, Path.Combine(exportDir, "base.defaults.t3d"), overwrite: true);
            defaultsBase = fresh
                ? ClassifyDefaults(d1, rawBase) : DefaultsStatus.Unavailable;
        }
        if (req.MineExpectedDefaults != null)
        {
            bool fresh = File.Exists(d2) && File.GetLastWriteTimeUtc(d2) >= sessionStartUtc;
            if (fresh)
                File.Copy(d2, Path.Combine(exportDir, "mine.defaults.t3d"), overwrite: true);
            defaultsMine = fresh
                ? ClassifyDefaults(d2, rawMine) : DefaultsStatus.Unavailable;
        }
        diagnostics.Add("Collected exports via deterministic Saved/Diff paths (helper fallback).");
        return true;
    }

    private static string? ResolveEngine(string? configured, List<string> diag)
    {
        if (configured != null)
            return File.Exists(configured) ? configured : null;
        var detected = ToolLocator.DetectEngineCmdPaths();
        if (detected.Count == 1) return detected[0];
        if (detected.Count > 1)
            diag.Add($"Multiple engines detected ({string.Join("; ", detected)}); specify --engine.");
        return null;
    }

    private static string? ResolveTortoise(string? configured, List<string> diag)
    {
        if (configured != null)
            return File.Exists(configured) ? configured : null;
        return ToolLocator.DetectTortoiseMerge();
    }

    private static async Task<IngestResult?> WaitForResultFileAsync(
        string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    return JsonSerializer.Deserialize<IngestResult>(File.ReadAllText(path));
                }
                catch { /* partial write; retry */ }
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Failures make the export incomplete; notes are surfaced but
    /// do not change the outcome (informational diagnostics that do not
    /// indicate a failed load/import/compile).</summary>
    public readonly record struct ExportPhaseScan(List<string> Failures, List<string> Notes);

    /// <summary>Proven-optional capture/profiling tool DLLs — verified benign
    /// engine-init noise in real logs (VTune, PIX, AqProf). Only these are
    /// excluded; a general ".dll" filter must never hide required-module
    /// failures such as 'Missing import:' or a project plugin DLL that
    /// failed to load.</summary>
    private static readonly Regex OptionalToolDll = new(
        @"VtuneApi|WinPixGpuCapturer|aqProf|WinPixEventRuntime",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Failure-indicating content inside an [AssetLog] message
    /// regardless of the severity UE chose — Unreal reports failed imports,
    /// missing members and broken parent-class loads at Warning severity,
    /// and a dependency path differing from the staged asset must not
    /// downgrade them. Purely informational lines do not match.</summary>
    private static readonly Regex FailedAssetDiagnostic = new(
        @"Failed to (?:load|find|import)|Can't find file|Could not find|" +
        @"could not be found|no longer exists|Missing|import error",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Dependency/load failures that make an export incomplete. Only lines AFTER
    /// the commandlet "Params:" marker are considered — that is where the
    /// export phase begins; earlier engine-init lines must not downgrade a
    /// clean run. DLL lines are excluded ONLY for proven-optional
    /// capture/profiling tools (VtuneApi/WinPixGpuCapturer/aqProf) — a general
    /// ".dll" filter would hide required-module failures ('Missing import:',
    /// project plugin DLLs), which are matched below as failures.
    ///
    /// "[AssetLog] &lt;path&gt;: ..." lines name the asset they were logged for.
    /// Error-severity lines are ALWAYS failures. Warning/Display-severity
    /// lines are classified by message CONTENT: UE reports failed imports,
    /// missing members and broken parent-class loads at Warning severity,
    /// and a dependency path differing from the staged input must not
    /// downgrade them — FailedAssetDiagnostic lines are failures regardless
    /// of the named asset. Purely informational lines (compile notes,
    /// unused-variable hints) stay non-blocking notes even when they name
    /// a staged input.
    /// </summary>
    public static ExportPhaseScan ScanExportPhaseFailures(
        string logPath, string? stemA = null, string? stemB = null)
    {
        var failures = new List<string>();
        var notes = new List<string>();
        if (!File.Exists(logPath)) return new(failures, notes);

        bool inExportPhase = false;
        foreach (var line in File.ReadLines(logPath))
        {
            if (!inExportPhase)
            {
                if (line.Contains("Params:") &&
                    (line.Contains("LogDiffAssetsCommandlet") ||
                     line.Contains("LogUatdDiffAssets")))
                    inExportPhase = true;
                continue;
            }
            if (OptionalToolDll.IsMatch(line)) continue;

            if (line.Contains("[AssetLog]"))
            {
                // Severity alone cannot classify: UE logs failed imports and
                // parent/member loads at Warning. An Error-severity line is
                // always a failure; otherwise the MESSAGE CONTENT decides —
                // failure-indicating diagnostics fail regardless of which
                // asset is named, while purely informational lines (even
                // ones naming a staged input) stay non-blocking notes.
                bool isError = Regex.IsMatch(line, @"\bError\s*:");
                if (isError || FailedAssetDiagnostic.IsMatch(line)) failures.Add(line);
                else notes.Add(line);
                continue;
            }
            if ((line.Contains("LogLinker") &&
                    (line.Contains("Error") || line.Contains("Warning"))) ||
                line.Contains("LoadErrors") ||
                line.Contains("Can't find file for asset") ||
                line.Contains("Failed to load") ||
                line.Contains("Missing import:") ||
                line.Contains("No text was exported") ||
                (line.Contains("Failed to find") &&
                    (line.Contains("script") || line.Contains("class") || line.Contains("package"))) ||
                (line.Contains("Missing") &&
                    (line.Contains("script") || line.Contains("class") || line.Contains("package"))))
            {
                // Generic load failures may reference dependencies of the
                // staged assets — conservatively always a failure.
                failures.Add(line);
            }
        }
        return new(
            failures.Skip(Math.Max(0, failures.Count - 40)).ToList(),
            notes.Skip(Math.Max(0, notes.Count - 20)).ToList());
    }

    /// <summary>Load/dependency warnings worth surfacing on success.</summary>
    private static IEnumerable<string> ExtractLoadDiagnostics(string logPath)
    {
        if (!File.Exists(logPath)) yield break;
        var hits = new List<string>();
        foreach (var line in File.ReadLines(logPath))
        {
            if ((line.Contains("LogLinker") &&
                    (line.Contains("Error") || line.Contains("Warning"))) ||
                line.Contains("LoadErrors") ||
                line.Contains("LogDiffAssetsCommandlet") ||
                line.Contains("Can't find file for asset") ||
                (line.Contains("Missing") && line.Contains("script")))
                hits.Add(line);
        }
        foreach (var l in hits.Skip(Math.Max(0, hits.Count - 40)))
            yield return "  load-warning: " + l;
    }

    private static IEnumerable<string> ExtractLogDiagnostics(string logPath)
    {
        if (!File.Exists(logPath)) yield break;
        var hits = new List<string>();
        foreach (var line in File.ReadLines(logPath))
        {
            if (line.Contains("Error") || line.Contains("Warning") ||
                line.Contains("DiffAssets") || line.Contains("Could not") ||
                line.Contains("Missing") || line.Contains("Failed"))
                hits.Add(line);
        }
        foreach (var l in hits.Skip(Math.Max(0, hits.Count - 60)))
            yield return "  " + l;
    }

    private static async Task<int> RunEngineAsync(string engine, string arguments,
        string sessionDir, TimeSpan timeout,
        IProgress<string> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(engine, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment[SessionEnvVar] = sessionDir;

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch { return ExitCodes.ConfigError; }

        // Poll HasExited rather than WaitForExitAsync: the commandlet may
        // remain alive (or appear alive) while detached descendants linger,
        // and the poll loop additionally lets us proceed the moment the
        // ingest helper reports success even if the engine is still exiting.
        string resultFile = Path.Combine(sessionDir, "result.json");
        var deadline = DateTime.UtcNow + timeout;
        int exitCode = -1;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    exitCode = process.ExitCode;
                    break;
                }
                if (File.Exists(resultFile))
                {
                    // Export is done; give the engine a short grace period to
                    // exit cleanly, then proceed regardless of its exit code.
                    var graceEnd = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    while (DateTime.UtcNow < graceEnd && !process.HasExited)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    exitCode = process.HasExited ? process.ExitCode : 0;
                    break;
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return ExitCodes.Cancelled;
        }
        if (exitCode < 0)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return ExitCodes.ExportFailed;
        }
        if (!process.HasExited)
            try { process.Kill(entireProcessTree: true); } catch { }
        progress.Report($"UnrealEditor-Cmd exited with code {exitCode}.");
        return exitCode;
    }

    /// <summary>Prune sessions older than the newest MaxSessionsKept.</summary>
    public static void PruneSessions()
    {
        try
        {
            var dir = new DirectoryInfo(AppSettings.SessionsRoot);
            if (!dir.Exists) return;
            var old = dir.GetDirectories().OrderByDescending(d => d.CreationTimeUtc)
                         .Skip(MaxSessionsKept).ToList();
            foreach (var d in old) d.Delete(recursive: true);
        }
        catch { }
    }
}
