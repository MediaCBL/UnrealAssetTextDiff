using Xunit;
using UnrealAssetTextDiff;

namespace UnrealAssetTextDiff.Tests;

public class CliOptionsTests
{
    [Fact]
    public void NoArgs_WantsGui()
    {
        var o = CliOptions.Parse(Array.Empty<string>());
        Assert.True(o.WantsGui);
        Assert.False(o.Ingest);
    }

    [Fact]
    public void FullCompareArgs_Parsed()
    {
        var o = CliOptions.Parse(new[]
        {
            "--project", @"D:\P\p.uproject", "--engine", @"C:\UE\UnrealEditor-Cmd.exe",
            "--tortoise", @"C:\T\TortoiseMerge.exe", "--base", "a.uasset",
            "--mine", "b.uasset", "--base-label", "r1", "--mine-label", "r2",
            "--export-only", "--output-dir", @"D:\out", "--timeout-sec", "30",
        });
        Assert.False(o.WantsGui);
        Assert.Equal(@"D:\P\p.uproject", o.Project);
        Assert.Equal("a.uasset", o.Base);
        Assert.Equal("r2", o.MineLabel);
        Assert.True(o.ExportOnly);
        Assert.Equal(@"D:\out", o.OutputDir);
        Assert.Equal(30, o.TimeoutSec);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void MissingAssetArgs_ProducesErrors()
    {
        var o = CliOptions.Parse(new[] { "--project", "p.uproject" });
        Assert.False(o.WantsGui);
        Assert.Contains(o.Errors, e => e.Contains("--base"));
        Assert.Contains(o.Errors, e => e.Contains("--mine"));
    }

    [Fact]
    public void UnknownOption_ProducesError()
    {
        var o = CliOptions.Parse(new[] { "--bogus" });
        Assert.Contains(o.Errors, e => e.Contains("--bogus"));
    }

    [Fact]
    public void IngestMode_Detected()
    {
        var o = CliOptions.Parse(new[] { @"D:\a\b.uasset.t3d", @"D:\a\c.uasset.t3d", "--ingest" });
        Assert.True(o.Ingest);
        Assert.False(o.WantsGui);
        Assert.Equal(2, o.Positional.Count);
    }

    [Fact]
    public void LogArgsMode_Detected()
    {
        var o = CliOptions.Parse(new[] { "--log-args", @"D:\argv.txt" });
        Assert.Equal(@"D:\argv.txt", o.LogArgsPath);
    }
}

public class IngestArgSplitTests
{
    [Fact]
    public void SimplePaths_Split()
    {
        var p = IngestRunner.SplitT3dArgs(new[]
            { @"D:\d\a.uasset.t3d", @"D:\d\b.uasset.t3d", "--ingest" });
        Assert.Equal(new[] { @"D:\d\a.uasset.t3d", @"D:\d\b.uasset.t3d" }, p);
    }

    [Fact]
    public void PathsWithSpaces_Rejoined()
    {
        var p = IngestRunner.SplitT3dArgs(new[]
        {
            @"D:\Projects\My", @"Game\Saved\Diff\a.uasset.t3d",
            @"D:\Projects\My", @"Game\Saved\Diff\b.uasset.t3d", "--ingest"
        });
        Assert.Equal(new[]
        {
            @"D:\Projects\My Game\Saved\Diff\a.uasset.t3d",
            @"D:\Projects\My Game\Saved\Diff\b.uasset.t3d"
        }, p);
    }

    [Fact]
    public void FlagsAfterPaths_Ignored()
    {
        var p = IngestRunner.SplitT3dArgs(new[]
            { @"D:\a.uasset.t3d", "--ingest", @"D:\b.uasset.t3d" });
        Assert.Equal(2, p.Count);
        Assert.Equal(@"D:\b.uasset.t3d", p[1]);
    }

    [Fact]
    public void T3dInDirectoryName_DocumentsAmbiguity()
    {
        // A directory literally named "x.t3d" makes argv re-join ambiguous:
        // "D:\a.t3d" looks like a completed path. This is exactly why the
        // session's expected paths in request.json take precedence; the
        // argv heuristic is a last resort and documents its limitation.
        var p = IngestRunner.SplitT3dArgs(new[]
            { @"D:\a.t3d", @"sub\b.uasset.t3d", @"D:\a.t3d", @"sub\c.uasset.t3d" });
        Assert.Equal(new[]
        {
            @"D:\a.t3d",
            @"sub\b.uasset.t3d",
            @"D:\a.t3d",
            @"sub\c.uasset.t3d",
        }, p); // wrong split under ambiguity — expected-paths resolution avoids this
    }
}

public class StagingTests
{
    [Fact]
    public void Sanitize_ReplacesInvalidChars()
    {
        Assert.Equal("BP_My_Asset", Staging.SanitizePackageFileStem("BP My Asset"));
        Assert.Equal("Asset", Staging.SanitizePackageFileStem("___"));
        Assert.Equal("Asset", Staging.SanitizePackageFileStem("%%%"));
        Assert.Equal("Asset", Staging.SanitizePackageFileStem(""));
        Assert.Equal("a_b_c", Staging.SanitizePackageFileStem("a:b/c"));
        Assert.Equal("Uni__Asset", Staging.SanitizePackageFileStem("Unié_Asset"));
    }

    [Fact]
    public void DeriveAssetName_PrefersLabel()
    {
        Assert.Equal("BP_Door", Staging.DeriveAssetName(@"C:\Temp\svn1A2.tmp", "BP_Door.uasset"));
        Assert.Equal("svn1A2", Staging.DeriveAssetName(@"C:\Temp\svn1A2.tmp", null));
    }

    [Fact]
    public void DeriveAssetName_StripsRevisionSuffix()
    {
        Assert.Equal("BP_Door", Staging.DeriveAssetName(@"x.tmp", "BP_Door.uasset : r123"));
    }

    [Fact]
    public void Stage_CopiesWithUniqueStemAndCompanions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uatd-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f1 = Path.Combine(dir, "a", "Same_Name.uasset");
            var f2 = Path.Combine(dir, "b", "Same_Name.uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(f1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(f2)!);
            File.WriteAllBytes(f1, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(f2, new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.ChangeExtension(f1, ".uexp"), new byte[] { 9 });

            var s1 = Staging.Stage(f1, null, "base", dir, "sess0001");
            var s2 = Staging.Stage(f2, null, "mine", dir, "sess0001");
            Assert.NotNull(s1); Assert.NotNull(s2);
            Assert.NotEqual(s1!.StagedStem, s2!.StagedStem);
            Assert.Contains("__base__", s1.StagedStem);
            Assert.Contains("__sess0001", s1.StagedStem);
            Assert.EndsWith(".uasset", s1.StagedPath);
            Assert.True(File.Exists(Path.ChangeExtension(s1.StagedPath, ".uexp")));

            // Same content -> same stem within one session (harmless: identical inputs).
            var s3 = Staging.Stage(f1, null, "mine", dir, "sess0001");
            Assert.Equal(s1.StagedStem.Replace("__base__", "__mine__"), s3!.StagedStem);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Stage_SessionTagIsolatesConcurrentRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uatd-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "Same.uasset");
            File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
            var a = Staging.Stage(f, null, "base", dir, "sessaaaa")!;
            var b = Staging.Stage(f, null, "base", dir, "sessbbbb")!;
            Assert.NotEqual(a.StagedStem, b.StagedStem); // no Saved/Diff collision
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class NormalizerTests
{
    [Fact]
    public void ConservativeView_OnlyStagedStemReplaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uatd-norm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string raw = Path.Combine(dir, "raw.t3d");
            string stem = "BP_A__base__deadbeef__sess0001";
            File.WriteAllText(raw,
                "ExportPath=\"/Temp/Diff/" + stem + ".Obj'\"\n" +
                "NodeGuid=ABC\nName=\"BP_A\"\n" +
                "GraphGuid=7D4031844F9F322638A41382E52C0E73\n" +
                "PinToolTip=NSLOCTEXT(\"KismetSchema\", \"A1B2C3D4\", \"Some pin\")");
            string outp = Path.Combine(dir, "cmp.t3d");
            ExportNormalizer.WriteNormalizedCopy(raw, stem, outp, filtered: false);
            var text = File.ReadAllText(outp);
            Assert.Contains("/Temp/Diff/" + ExportNormalizer.PackagePlaceholder + ".Obj", text);
            Assert.Contains("NodeGuid=ABC", text);
            Assert.Contains("Name=\"BP_A\"", text);
            Assert.DoesNotContain("deadbeef", text);
            Assert.DoesNotContain("sess0001", text);
            // Serialized GraphGuid and NSLOCTEXT key identity are real data —
            // the default view preserves both verbatim.
            Assert.Contains("GraphGuid=7D4031844F9F322638A41382E52C0E73", text);
            Assert.Contains("NSLOCTEXT(\"KismetSchema\", \"A1B2C3D4\"", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FilteredView_OnlyNormalizesInsideGeneratedBlocks()
    {
        // ExecuteUbergraph_* stubs and *_MERGED clones are regenerated per load
        // (FGuid::NewGuid in UEdGraph::PostInitProperties; compiler clones in
        // FKismetCompilerContext) — verified by exporting identical bytes twice.
        // Only inside those objects may identifiers be masked.
        string raw =
            "Begin Object Name=\"ExecuteUbergraph_BP_A\" ExportPath=\"/Script/Engine.EdGraph'x:ExecuteUbergraph_BP_A'\"\n" +
            "   GraphGuid=11111111111111111111111111111111\n" +
            "   PinToolTip=NSLOCTEXT(\"KismetSchema\", \"K1K1K1K1\", \"Pin label\")\n" +
            "End Object\n" +
            "Begin Object Name=\"EventGraph\" ExportPath=\"/Script/Engine.EdGraph'x:EventGraph'\"\n" +
            "   GraphGuid=22222222222222222222222222222222\n" +
            "   PinToolTip=NSLOCTEXT(\"KismetSchema\", \"K2K2K2K2\", \"Pin label\")\n" +
            "   Begin Object Name=\"K2Node_Call\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:EventGraph.N'\"\n" +
            "      GraphGuid=33333333333333333333333333333333\n" +
            "   End Object\n" +
            "End Object\n" +
            "Begin Object Name=\"UserConstructionScript_MERGED\" ExportPath=\"/Script/Engine.EdGraph'x:M'\"\n" +
            "   GraphGuid=44444444444444444444444444444444\n" +
            "   Begin Object Name=\"K2Node_Ref\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:M.N'\"\n" +
            "      GraphGuid=55555555555555555555555555555555\n" +
            "   End Object\n" +
            "End Object\n";
        string text = ExportNormalizer.NormalizeFiltered(raw, "stem");
        // Generated objects' own ids masked; authored identifiers preserved.
        Assert.DoesNotContain("111111111111", text);
        Assert.DoesNotContain("444444444444", text);
        Assert.Contains("GraphGuid=22222222222222222222222222222222", text);
        Assert.Contains("GraphGuid=33333333333333333333333333333333", text);
        Assert.DoesNotContain("555555555555", text);
        // NSLOCTEXT keys masked ONLY inside generated objects; authored
        // namespace+key identity survives the filtered view too.
        Assert.DoesNotContain("K1K1K1K1", text);
        Assert.Contains("NSLOCTEXT(\"KismetSchema\", \"K2K2K2K2\"", text);
    }

    [Fact]
    public void FilteredView_PinTokens_IncludeGraphContext()
    {
        // PinId is not persistent (reconstructed pins get fresh guids per
        // load). Tokens are <graph>.<node>__<pin> so same node/pin names in
        // DIFFERENT graphs (authored graph vs its compiled clone) stay
        // distinct and LinkedTo references resolve to readable identity.
        string raw =
            "Begin Object Name=\"EventGraph\" ExportPath=\"/Script/Engine.EdGraph'x:EventGraph'\"\n" +
            "   Begin Object Name=\"K2Node_Op\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:EventGraph.K2Node_Op'\"\n" +
            "      NodeGuid=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n" +
            "      CustomProperties Pin (PinId=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB,PinName=\"ErrorTolerance\")\n" +
            "      CustomProperties Pin (PinId=CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC,PinName=\"then\",LinkedTo=(K2Node_X BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB,))\n" +
            "   End Object\n" +
            "End Object\n" +
            "Begin Object Name=\"ReceiveTick\" ExportPath=\"/Script/Engine.EdGraph'x:ReceiveTick'\"\n" +
            "   Begin Object Name=\"K2Node_Op\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:ReceiveTick.K2Node_Op'\"\n" +
            "      NodeGuid=DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD\n" +
            "      CustomProperties Pin (PinId=EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE,PinName=\"ErrorTolerance\")\n" +
            "   End Object\n" +
            "End Object\n";
        string text = ExportNormalizer.NormalizeFiltered(raw, "stem");
        Assert.Contains("PinId=P_EventGraph.K2Node_Op__ErrorTolerance", text);
        Assert.Contains("PinId=P_ReceiveTick.K2Node_Op__ErrorTolerance", text);
        Assert.Contains("K2Node_X P_EventGraph.K2Node_Op__ErrorTolerance", text);
        Assert.DoesNotContain("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", text);
        Assert.DoesNotContain("EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", text);
        // Authored NodeGuid untouched (authored graph, no intermediate flag).
        Assert.Contains("NodeGuid=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", text);
    }

    [Fact]
    public void FilteredView_IntermediateNode_MarksItselfOnly()
    {
        // bIsIntermediateNode=True on an ordinary node marks that ONE object
        // as regenerated — verified in real exports where authored graphs
        // (UserConstructionScript) carry intermediate K2Nodes yet are stable.
        // A lone generated node must not make every identifier in its
        // containing graph disposable.
        string raw =
            "Begin Object Name=\"UserConstructionScript\" ExportPath=\"/Script/Engine.EdGraph'x:UserConstructionScript'\"\n" +
            "   GraphGuid=11111111111111111111111111111111\n" +
            "   Begin Object Name=\"K2Node_CallFunction_0\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:UserConstructionScript.K2Node_CallFunction_0'\"\n" +
            "      bIsIntermediateNode=True\n" +
            "      NodeGuid=22222222222222222222222222222222\n" +
            "   End Object\n" +
            "   Begin Object Name=\"K2Node_Authored\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:UserConstructionScript.K2Node_Authored'\"\n" +
            "      NodeGuid=33333333333333333333333333333333\n" +
            "   End Object\n" +
            "End Object\n";
        string text = ExportNormalizer.NormalizeFiltered(raw, "stem");
        // The intermediate node itself is masked; its containing graph and
        // sibling authored node keep their identifiers.
        Assert.DoesNotContain("222222222222", text);
        Assert.Contains("GraphGuid=11111111111111111111111111111111", text);
        Assert.Contains("NodeGuid=33333333333333333333333333333333", text);
    }

    [Fact]
    public void FilteredView_IntermediateFunctionEntry_MarksWholeGraph()
    {
        // A K2Node_FunctionEntry that is intermediate means the FUNCTION was
        // compiler-created (verified: ReceiveTick/ReceiveBeginPlay/BndEvt__*/
        // timeline funcs). Everything inside such a graph is a regenerated
        // clone — mask the whole graph, not just the entry node.
        string raw =
            "Begin Object Name=\"ReceiveTick\" ExportPath=\"/Script/Engine.EdGraph'x:ReceiveTick'\"\n" +
            "   GraphGuid=11111111111111111111111111111111\n" +
            "   Begin Object Name=\"K2Node_FunctionEntry_0\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:ReceiveTick.K2Node_FunctionEntry_0'\"\n" +
            "      bIsIntermediateNode=True\n" +
            "   End Object\n" +
            "   Begin Object Name=\"K2Node_CallFunction_0\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:ReceiveTick.K2Node_CallFunction_0'\"\n" +
            "      GraphGuid=22222222222222222222222222222222\n" +
            "      NodeGuid=33333333333333333333333333333333\n" +
            "   End Object\n" +
            "End Object\n";
        string text = ExportNormalizer.NormalizeFiltered(raw, "stem");
        Assert.DoesNotContain("111111111111", text); // graph's own guid masked
        Assert.DoesNotContain("222222222222", text); // node GraphGuid masked
        Assert.DoesNotContain("333333333333", text); // node NodeGuid masked
    }

    [Fact]
    public void FilteredView_AmbiguousPinContext_KeepsRawGuid()
    {
        // Two different guids claiming the same (graph,node,pin) context:
        // collapsing them would fake identity — keep the raw value instead.
        string raw =
            "Begin Object Name=\"G\" ExportPath=\"/Script/Engine.EdGraph'x:G'\"\n" +
            "   Begin Object Name=\"N\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:G.N'\"\n" +
            "      CustomProperties Pin (PinId=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA,PinName=\"p\")\n" +
            "   End Object\n" +
            "   Begin Object Name=\"N\" ExportPath=\"/Script/BlueprintGraph.K2Node'x:G.N2'\"\n" +
            "      CustomProperties Pin (PinId=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB,PinName=\"p\")\n" +
            "   End Object\n" +
            "End Object\n";
        string text = ExportNormalizer.NormalizeFiltered(raw, "stem");
        Assert.Contains("PinId=P_G.N__p", text);                 // first wins the token
        Assert.Contains("PinId=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", text); // second keeps raw
    }
}

public class CommandLineTests
{
    [Fact]
    public void DiffCmdToken_SingleQuotedValue()
    {
        string args = DiffSessionRunner.BuildCommandletArguments(
            @"D:\P\p.uproject", @"D:\s\a.uasset", @"D:\s\b.uasset", @"D:\T\UATD.exe");
        Assert.Contains("-run=DiffAssets", args);
        Assert.Contains("DiffCmd=\"D:\\T\\UATD.exe {1} {2} --ingest\"", args);
        // The supplemental defaults backend selects the plugin commandlet.
        Assert.Contains("-run=UatdAssetDiff", DiffSessionRunner.BuildCommandletArguments(
            @"D:\P\p.uproject", @"D:\s\a.uasset", @"D:\s\b.uasset", @"D:\T\UATD.exe",
            defaultsBackend: true));
        // No quotes inside the DiffCmd value other than the wrapping pair.
        int eq = args.IndexOf("DiffCmd=\"", StringComparison.Ordinal);
        string value = args[(eq + "DiffCmd=\"".Length)..];
        value = value[..value.IndexOf('"')];
        Assert.DoesNotContain('"', value);
        Assert.Contains("{1}", value);
        Assert.Contains("{2}", value);
        Assert.True(value.IndexOf("{1}") < value.IndexOf("{2}"));
    }

    [Fact]
    public void HelperUrl_MustNotContainSpaces_WhenShortPathUnavailable()
    {
        // Documents the contract: ForDiffCmd returns a space-free path when
        // 8.3 names exist; otherwise the raw path (CreateProcess handles the
        // progressive-match fallback).
        string url = ShortPath.ForDiffCmd(Environment.ProcessPath!);
        Assert.False(url.Contains('"'));
    }
}

public class ExportPhaseScanTests
{
    private static string WriteLog(string[] lines)
    {
        string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        File.WriteAllLines(p, lines);
        return p;
    }

    [Fact]
    public void StagedAsset_Errors_AreFailures_UnrelatedWarnings_AreNotes()
    {
        // Error-severity [AssetLog] lines are ALWAYS failures — a transitively
        // loaded dependency error may be load-relevant. Non-Error lines are
        // classified by message content, not severity: purely informational
        // warnings stay notes.
        string log = WriteLog(new[]
        {
            "LogInit: Display: engine startup noise",
            "LogDiffAssetsCommandlet: Params: -run=DiffAssets a__base__HH__ss.uasset b__mine__HH__ss.uasset",
            "LogBlueprint: Error: [AssetLog] D:/other/BPC_Unrelated.uasset: [Compiler] pin missing",
            "LogBlueprint: Warning: [AssetLog] D:/other/BPC_Unrelated.uasset: [Compiler] unused var",
            "LogBlueprint: Error: [AssetLog] D:/x/a__base__HH__ss.uasset: [Compiler] bad cast",
            "LogWindows: Display: Failed to load 'VtuneApi.dll' (GetLastError=126)",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a__base__HH__ss", "b__mine__HH__ss");
        Assert.Equal(2, scan.Failures.Count);
        Assert.Contains(scan.Failures, f => f.Contains("BPC_Unrelated") && f.Contains("Error:"));
        Assert.Contains(scan.Failures, f => f.Contains("a__base__HH__ss"));
        Assert.Single(scan.Notes);
        Assert.Contains("Warning:", scan.Notes[0]);
        File.Delete(log);
    }

    [Fact]
    public void AssetLog_UnclassifiedError_StaysFailure_EvenAbsentFromExports()
    {
        // Regression for the removed raw-absence rule: a missing parent or
        // dependency may be invisible in the exported text, so a negative
        // search is not grounds for a note. Unclassified errors retain
        // incomplete status.
        string log = WriteLog(new[]
        {
            "LogUatdDiffAssets: Params: -run=UatdAssetDiff a__base__HH__ss.uasset b__mine__HH__ss.uasset",
            "LogBlueprint: Error: [AssetLog] /Game/BP_ParentInterface: [Compiler] bad ref",
            "LogBlueprint: Error: [AssetLog] /Game/BPC_TransientEditorOnly: failed",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(
            log, "a__base__HH__ss", "b__mine__HH__ss");
        Assert.Equal(2, scan.Failures.Count);
        Assert.Empty(scan.Notes);
        File.Delete(log);
    }

    [Fact]
    public void AssetLog_WarningSeverityFailure_OnDependency_IsFailure()
    {
        // UE logs failed imports / missing members at WARNING severity, and
        // the named asset may be a DEPENDENCY, not a staged input. Real
        // warning shapes observed in engine logs; a dependency path must
        // not bypass the failure classification.
        string log = WriteLog(new[]
        {
            "LogUatdDiffAssets: Params: -run=UatdAssetDiff a__base__HH__ss.uasset b__mine__HH__ss.uasset",
            "LogBlueprint: Warning: [AssetLog] /Game/BP_ParentDep.uasset: [Compiler] Could not find a variable named \"OldVar\" in '/Game/BP_ParentDep.BP_ParentDep'.",
            "LogBlueprint: Warning: [AssetLog] /Game/BP_BaseClass.uasset: [Compiler] In use pin  Input  no longer exists on node  Event . Please refresh node or break links to remove pin.",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a__base__HH__ss", "b__mine__HH__ss");
        Assert.Equal(2, scan.Failures.Count);
        Assert.Empty(scan.Notes);
        File.Delete(log);
    }

    [Fact]
    public void AssetLog_Informational_OnStagedAsset_IsNote_NotFailure()
    {
        // Purely informational messages must not become failures solely
        // because they mention a staged asset.
        string log = WriteLog(new[]
        {
            "LogUatdDiffAssets: Params: -run=UatdAssetDiff a.uasset b.uasset",
            "LogBlueprint: Display: [AssetLog] /x/a.uasset: compiling in the background",
            "LogBlueprint: Warning: [AssetLog] /x/a.uasset: [Compiler] variable OldName is deprecated, consider NewName",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a", "b");
        Assert.Empty(scan.Failures);
        Assert.Equal(2, scan.Notes.Count);
        File.Delete(log);
    }

    [Fact]
    public void RequiredModuleLoadFailure_NotHiddenByOptionalDllFilter()
    {
        // Only proven-optional capture/profiling DLLs are excluded. Required
        // module failures — native import misses and project plugin DLLs —
        // post-Params are failures, not noise.
        string log = WriteLog(new[]
        {
            "LogUatdDiffAssets: Params: -run=UatdAssetDiff a.uasset b.uasset",
            "LogWindows: Display: Failed to load 'VtuneApi.dll' (GetLastError=126)",
            "LogWindows:   Missing import: AVICAP32.dll",
            "LogWindows: Failed to load 'D:/Proj/Plugins/Foo/bin/x64/bar-58.dll' (GetLastError=126)",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a", "b");
        Assert.Equal(2, scan.Failures.Count);
        Assert.Contains(scan.Failures, f => f.Contains("Missing import"));
        Assert.Contains(scan.Failures, f => f.Contains("bar-58.dll"));
        Assert.DoesNotContain(scan.Failures, f => f.Contains("VtuneApi"));
        File.Delete(log);
    }

    [Fact]
    public void UatdCommandlet_ParamsMarker_Recognized()
    {
        // The supplemental commandlet logs under LogUatdDiffAssets; the
        // export-phase marker must match either commandlet.
        string log = WriteLog(new[]
        {
            "LoadErrors: Error: pre-phase noise",
            "LogUatdDiffAssets: Params: -run=UatdAssetDiff a.uasset b.uasset",
            "LogLinker: Error: Failed to load package '/Game/Missing'",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a", "b");
        Assert.Single(scan.Failures);
        File.Delete(log);
    }
}

public class DefaultsClassificationTests
{
    private static string Temp(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".t3d");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void ValidPayload_IsIncluded()
    {
        string d = Temp("; UatdClassDefaults: CDO property values\n" +
                        "Class=/Temp/Diff/x.BP_C\nSuperClass=/Script/Engine.Actor\n" +
                        "[var] Damage : float = 10.000000\nPropertyCount=1\n");
        Assert.Equal(DefaultsStatus.Included, DiffSessionRunner.ClassifyDefaults(d, null));
        File.Delete(d);
    }

    [Fact]
    public void NoDefaultObject_IsUnavailable_NotCoverage()
    {
        // The marker records a failed defaults export — it must never count
        // as successful coverage of a Blueprint's class defaults.
        string d = Temp("; UatdClassDefaults: CDO property values\nNoDefaultObject\n");
        Assert.Equal(DefaultsStatus.Unavailable, DiffSessionRunner.ClassifyDefaults(d, null));
        File.Delete(d);
    }

    [Fact]
    public void MissingOrEmptyFile_IsUnavailable()
    {
        Assert.Equal(DefaultsStatus.Unavailable,
            DiffSessionRunner.ClassifyDefaults(null, null));
        Assert.Equal(DefaultsStatus.Unavailable,
            DiffSessionRunner.ClassifyDefaults(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".t3d"), null));
    }

    [Fact]
    public void NoBlueprintGeneratedClass_ContradictedByRaw_IsUnavailable()
    {
        // A NoBlueprintGeneratedClass marker on an asset whose raw export
        // clearly contains BlueprintGeneratedClass objects is an exporter
        // failure, not a non-Blueprint asset.
        string d = Temp("NoBlueprintGeneratedClass\n");
        string raw = Temp("Begin Object Class=/Script/Engine.BlueprintGeneratedClass Name=\"BP_C\"\n");
        Assert.Equal(DefaultsStatus.Unavailable,
            DiffSessionRunner.ClassifyDefaults(d, raw));
        File.Delete(d); File.Delete(raw);
    }

    [Fact]
    public void NoBlueprintGeneratedClass_ConfirmedNonBlueprint_IsNotABlueprint()
    {
        string d = Temp("NoBlueprintGeneratedClass\n");
        string raw = Temp("Begin Object Class=/Script/Engine.StaticMesh Name=\"SM_X\"\n");
        Assert.Equal(DefaultsStatus.NotABlueprint,
            DiffSessionRunner.ClassifyDefaults(d, raw));
        File.Delete(d); File.Delete(raw);
    }

    [Fact]
    public void MalformedPayload_IsUnavailable()
    {
        string d = Temp("; UatdClassDefaults: CDO property values\nClass=/Temp/Diff/x.BP_C\n");
        Assert.Equal(DefaultsStatus.Unavailable, DiffSessionRunner.ClassifyDefaults(d, null));
        File.Delete(d);
    }
}

public class ProcessOutcomeTests
{
    private static string WriteLog(string[] lines)
    {
        string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        File.WriteAllLines(p, lines);
        return p;
    }

    [Fact]
    public void NonzeroExit_WithExports_IsIncomplete_NotSuccess()
    {
        // A nonzero/crashed engine exit cannot become ordinary success merely
        // because output files were written.
        Assert.Equal(ExitCodes.IncompleteExport,
            DiffSessionRunner.ClassifyProcessOutcome(1, exportsArrived: true));
        Assert.Equal(ExitCodes.IncompleteExport,
            DiffSessionRunner.ClassifyProcessOutcome(-1, exportsArrived: true));
        Assert.Equal(ExitCodes.IncompleteExport,
            DiffSessionRunner.ClassifyProcessOutcome(unchecked((int)0xC0000005),
                exportsArrived: true));
    }

    [Fact]
    public void CleanExit_WithExports_IsSuccess()
    {
        Assert.Equal(ExitCodes.Success,
            DiffSessionRunner.ClassifyProcessOutcome(0, exportsArrived: true));
    }

    [Fact]
    public void MissingExports_IsExportFailed_RegardlessOfExit()
    {
        Assert.Equal(ExitCodes.ExportFailed,
            DiffSessionRunner.ClassifyProcessOutcome(0, exportsArrived: false));
        Assert.Equal(ExitCodes.ExportFailed,
            DiffSessionRunner.ClassifyProcessOutcome(3, exportsArrived: false));
    }

    [Fact]
    public void PreExportPhase_Errors_AreIgnored()
    {
        // Only lines after the Params: marker count — engine-init noise
        // (optional DLLs etc.) must not downgrade a clean run.
        string log = WriteLog(new[]
        {
            "LogLinker: Warning: early benign warning",
            "LoadErrors: Error: pre-phase noise",
            "LogDiffAssetsCommandlet: Params: -run=DiffAssets a.uasset b.uasset",
            "LogInit: Display: clean finish",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a", "b");
        Assert.Empty(scan.Failures);
        File.Delete(log);
    }

    [Fact]
    public void GenericLoadFailure_AfterParams_IsFailure()
    {
        string log = WriteLog(new[]
        {
            "LogDiffAssetsCommandlet: Params: -run=DiffAssets a.uasset b.uasset",
            "LogLinker: Error: Failed to load package '/Game/Missing'",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a", "b");
        Assert.Single(scan.Failures);
        File.Delete(log);
    }

    [Fact]
    public void DependencyWarning_WithCleanExitAndExports_RemainsIncomplete()
    {
        // Focused regression: a Warning-severity dependency diagnostic
        // (a parent-class member that failed to load, on a non-staged path),
        // engine exit 0, exports present — must remain INCOMPLETE, not
        // silently success. This mirrors FinishOutcome's combination:
        // any scan failure forces incomplete regardless of process outcome.
        string log = WriteLog(new[]
        {
            "LogUatdDiffAssets: Params: -run=UatdAssetDiff a.uasset b.uasset",
            "LogBlueprint: Warning: [AssetLog] /Game/BP_RequiredParent.uasset: [Compiler] The property associated with  Old Pin  could not be found in '/Game/BP_RequiredParent.BP_RequiredParent_C'",
        });
        var scan = DiffSessionRunner.ScanExportPhaseFailures(log, "a", "b");
        Assert.Single(scan.Failures);
        // Process outcome alone would say success — the scan failure must win.
        Assert.Equal(ExitCodes.Success,
            DiffSessionRunner.ClassifyProcessOutcome(0, exportsArrived: true));
        Assert.True(scan.Failures.Count > 0); // FinishOutcome -> IncompleteExport (6)
        File.Delete(log);
    }
}
