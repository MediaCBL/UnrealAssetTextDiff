using System.IO;
using System.Text.Json;

namespace UnrealAssetTextDiff;

/// <summary>
/// --ingest mode: this is the executable the commandlet launches through
/// the DiffCmd template ("&lt;exe&gt; {1} {2} --ingest"). The commandlet
/// substitutes the two .t3d export paths for {1}/{2} and launches us
/// detached. All context arrives via the UATD_SESSION_DIR env var plus the
/// session's request.json (the DiffCmd value cannot carry quoted
/// space-separated arguments). The request's expected .t3d/.defaults.t3d
/// paths are used when they exist; argv is re-joined at .t3d boundaries
/// only as a fallback, because paths containing spaces or ".t3d"
/// directory names split ambiguously across argv tokens.
///
/// The ingest helper only collects/normalizes exports and writes
/// result.json — the PARENT owns viewer launch so that outcome flags
/// (incomplete/limited) can be shown in the TortoiseMerge labels.
/// </summary>
public static class IngestRunner
{
    /// <summary>Re-join argv tokens into paths, ending each path at a .t3d token.</summary>
    public static List<string> SplitT3dArgs(IEnumerable<string> args)
    {
        var paths = new List<string>();
        var current = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            current.Add(a);
            if (a.EndsWith(".t3d", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(string.Join(' ', current));
                current.Clear();
            }
        }
        return paths;
    }

    public static int Run(CliOptions o)
    {
        string? sessionDir = Environment.GetEnvironmentVariable(DiffSessionRunner.SessionEnvVar);
        if (sessionDir == null)
        {
            WriteResult(null, new IngestResult { Ok = false, Error = "UATD_SESSION_DIR not set" });
            return ExitCodes.InvalidUsage;
        }

        var ingestLog = Path.Combine(sessionDir, "logs", "ingest.log");
        try
        {
            var req = JsonSerializer.Deserialize<IngestRequest>(
                File.ReadAllText(Path.Combine(sessionDir, "request.json")))!;
            string exportDir = Path.Combine(sessionDir, "export");

            // Resolve the two export paths: prefer the session's deterministic
            // expected paths (exact, session-unique), fall back to argv re-join.
            string? baseT3d = ResolveExport(req.BaseExpectedT3d);
            string? mineT3d = ResolveExport(req.MineExpectedT3d);
            var argvPaths = SplitT3dArgs(o.Positional);
            baseT3d ??= argvPaths.Count > 0 ? argvPaths[0] : null;
            mineT3d ??= argvPaths.Count > 1 ? argvPaths[1] : null;
            File.AppendAllText(ingestLog,
                $"expected base={req.BaseExpectedT3d} mine={req.MineExpectedT3d}\n" +
                $"argv-joined=[{string.Join(" | ", argvPaths)}]\n" +
                $"resolved base={baseT3d} mine={mineT3d}\n");

            if (baseT3d == null || mineT3d == null || !File.Exists(baseT3d) ||
                !File.Exists(mineT3d) || new FileInfo(baseT3d).Length == 0 ||
                new FileInfo(mineT3d).Length == 0)
            {
                WriteResult(sessionDir, new IngestResult
                {
                    Ok = false,
                    SessionTag = req.SessionTag,
                    Error = $"exports missing or empty (base={baseT3d ?? "null"}, mine={mineT3d ?? "null"})"
                });
                return ExitCodes.ExportFailed;
            }

            string rawBase = Path.Combine(exportDir, "base.raw.t3d");
            string rawMine = Path.Combine(exportDir, "mine.raw.t3d");
            File.Copy(baseT3d, rawBase, overwrite: true);
            File.Copy(mineT3d, rawMine, overwrite: true);

            // staged stem = t3d filename minus ".t3d" and the package extension
            static string Stem(string p)
            {
                string f = Path.GetFileName(p);
                f = f.EndsWith(".t3d", StringComparison.OrdinalIgnoreCase)
                    ? f[..^".t3d".Length] : f;
                return Path.GetFileNameWithoutExtension(f);
            }
            ExportNormalizer.WriteNormalizedCopy(rawBase, Stem(baseT3d),
                Path.Combine(exportDir, "base.t3d"), req.Filtered);
            ExportNormalizer.WriteNormalizedCopy(rawMine, Stem(mineT3d),
                Path.Combine(exportDir, "mine.t3d"), req.Filtered);

            // Supplemental class-default exports (written by the UatdAssetDiff
            // commandlet beside the .t3d when the plugin is installed). The
            // payload is classified semantically — a marker file is not a
            // successful defaults export.
            var baseDefaults = Collect(req.BaseExpectedDefaults,
                Path.Combine(exportDir, "base.defaults.t3d"), rawBase);
            var mineDefaults = Collect(req.MineExpectedDefaults,
                Path.Combine(exportDir, "mine.defaults.t3d"), rawMine);
            File.AppendAllText(ingestLog,
                $"defaults base={baseDefaults} mine={mineDefaults}\n");

            WriteResult(sessionDir, new IngestResult
            {
                Ok = true,
                SessionTag = req.SessionTag,
                BaseDefaultsStatus = (int)baseDefaults,
                MineDefaultsStatus = (int)mineDefaults,
            });
            File.AppendAllText(ingestLog, "ingest ok\n");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(ingestLog, ex + "\n"); } catch { }
            WriteResult(sessionDir, new IngestResult { Ok = false, Error = ex.ToString() });
            return ExitCodes.ExportFailed;
        }
    }

    /// <summary>Accept an expected path only if the file exists at all —
    /// emptiness is checked on the resolved path so a failed export is
    /// reported, not silently swapped for a stale sibling.</summary>
    private static string? ResolveExport(string? expected) =>
        expected != null && File.Exists(expected) ? expected : null;

    /// <summary>Copy a defaults file for retention (when present) and return
    /// its semantic status. Only called when the plugin backend was expected
    /// to produce one.</summary>
    private static DefaultsStatus Collect(string? src, string dst, string rawT3d)
    {
        if (src == null) return DefaultsStatus.NotRequested;
        if (File.Exists(src))
            try { File.Copy(src, dst, overwrite: true); } catch { }
        return DiffSessionRunner.ClassifyDefaults(src, rawT3d);
    }

    /// <summary>Atomic write: temp file then move, so the parent never reads a partial JSON.</summary>
    private static void WriteResult(string? sessionDir, IngestResult res)
    {
        if (sessionDir == null) return;
        try
        {
            string final = Path.Combine(sessionDir, "result.json");
            string tmp = final + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(res));
            File.Move(tmp, final, overwrite: true);
        }
        catch { }
    }
}
