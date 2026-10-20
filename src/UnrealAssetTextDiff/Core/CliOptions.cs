namespace UnrealAssetTextDiff;

/// <summary>
/// Command line surface for UnrealAssetTextDiff.
///
/// GUI usage:    UnrealAssetTextDiff.exe
/// CLI compare:  UnrealAssetTextDiff.exe --project P --engine E --tortoise T
///                  --base old.uasset --mine new.uasset
///                  [--base-label L] [--mine-label L] [--export-only]
///                  [--output-dir D] [--timeout-sec N]
/// Ingest mode:  invoked by UnrealEditor-Cmd via the DiffCmd template as
///                  &lt;exe&gt; {1} {2} --ingest   (session dir arrives via
///                  the UATD_SESSION_DIR environment variable because the
///                  DiffCmd value cannot contain quotes or space-safe args)
/// Diagnostics:  --log-args FILE writes raw argv to FILE (used to verify
///               TortoiseSVN placeholder expansion).
/// </summary>
public sealed class CliOptions
{
    public bool WantsGui { get; private set; }
    public bool Ingest { get; private set; }
    public string? LogArgsPath { get; private set; }

    public string? Project { get; private set; }
    public string? Engine { get; private set; }
    public string? Tortoise { get; private set; }
    public string? Base { get; private set; }
    public string? Mine { get; private set; }
    public string? BaseLabel { get; private set; }
    public string? MineLabel { get; private set; }
    public string? OutputDir { get; private set; }
    public bool ExportOnly { get; private set; }
    /// <summary>Opt-in: apply the aggressive identifier-noise normalization to
    /// the comparison copies. Default view normalizes only the staged package
    /// name; raw exports are always preserved alongside.</summary>
    public bool Filtered { get; set; }
    public int TimeoutSec { get; private set; } = 900;
    public bool HelpRequested { get; private set; }

    /// <summary>Raw positional arguments (ingest mode joins these on .t3d boundaries).</summary>
    public List<string> Positional { get; } = new();
    public List<string> Errors { get; } = new();

    private CliOptions() { }

    /// <summary>Construct options programmatically (GUI compare request).</summary>
    public static CliOptions FromFields(string? project, string? engine, string? tortoise,
        string? basePath, string? mine, string? baseLabel, string? mineLabel,
        string? outputDir, bool exportOnly, bool filtered = false)
    {
        return new CliOptions
        {
            Project = Empty(project), Engine = Empty(engine), Tortoise = Empty(tortoise),
            Base = Empty(basePath), Mine = Empty(mine),
            BaseLabel = Empty(baseLabel), MineLabel = Empty(mineLabel),
            OutputDir = Empty(outputDir), ExportOnly = exportOnly, Filtered = filtered,
        };
        static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        o.WantsGui = args.Length == 0;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            string Required(string name)
            {
                string? v = Value();
                if (v == null) o.Errors.Add($"Missing value for {name}.");
                return v ?? "";
            }

            switch (a)
            {
                case "--ingest": o.Ingest = true; break;
                case "--project": o.Project = Required("--project"); break;
                case "--engine": o.Engine = Required("--engine"); break;
                case "--tortoise": o.Tortoise = Required("--tortoise"); break;
                case "--base": o.Base = Required("--base"); break;
                case "--mine": o.Mine = Required("--mine"); break;
                case "--base-label": o.BaseLabel = Required("--base-label"); break;
                case "--mine-label": o.MineLabel = Required("--mine-label"); break;
                case "--output-dir": o.OutputDir = Required("--output-dir"); break;
                case "--log-args": o.LogArgsPath = Required("--log-args"); break;
                case "--export-only": o.ExportOnly = true; break;
                case "--filtered": o.Filtered = true; break;
                case "--timeout-sec":
                    if (int.TryParse(Required("--timeout-sec"), out int t) && t > 0) o.TimeoutSec = t;
                    else o.Errors.Add("--timeout-sec must be a positive integer.");
                    break;
                case "-h": case "--help": case "/?":
                    o.HelpRequested = true;
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        o.Errors.Add($"Unknown option: {a}");
                    else
                        o.Positional.Add(a);
                    break;
            }
        }

        if (!o.WantsGui && !o.Ingest && o.LogArgsPath == null && !o.HelpRequested)
        {
            if (o.Base == null) o.Errors.Add("--base is required.");
            if (o.Mine == null) o.Errors.Add("--mine is required.");
            // --project may be omitted when a project is saved in settings.
        }
        return o;
    }

    public const string Usage =
        "UnrealAssetTextDiff - export two .uasset files to text via Unreal Engine\n" +
        "and open the exports in TortoiseMerge.\n\n" +
        "GUI:            UnrealAssetTextDiff.exe\n" +
        "Compare:        --project <uproject> --base <old> --mine <new>\n" +
        "Options:        --engine <UnrealEditor-Cmd.exe>  --tortoise <TortoiseMerge.exe>\n" +
        "                --base-label <label>  --mine-label <label>\n" +
        "                --export-only         --output-dir <dir>  --timeout-sec <n>\n" +
        "                --filtered            filtered view (normalizes generated ids)\n" +
        "Internal:       --ingest (invoked by Unreal via DiffCmd), --log-args <file>\n\n" +
        "Exit codes: 0=success 1=invalid usage 2=config error 3=export failed\n" +
        "            4=viewer launch failed 5=cancelled 6=incomplete export";
}
