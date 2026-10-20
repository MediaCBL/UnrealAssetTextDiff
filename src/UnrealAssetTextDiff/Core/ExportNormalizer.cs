using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnrealAssetTextDiff;

/// <summary>
/// Two comparison views of a raw Unreal text export. Raws are always kept
/// untouched; neither view is proof the underlying binaries match.
///
/// Normalize (DEFAULT, conservative):
///   Replaces only the staged package name — the commandlet loads staged
///   copies under /Temp/Diff/&lt;stagedStem&gt;, so every occurrence is
///   pipeline noise by construction. Nothing else is touched: authored
///   GraphGuid/NodeGuid/PinId, NSLOCTEXT namespace+key identity, and all
///   property values are preserved verbatim. Per-load identifier churn
///   inside compiler-regenerated graphs remains visible in this view —
///   that is the honest trade-off the filtered view exists for.
///
/// NormalizeFiltered (OPT-IN):
///   Additionally normalizes per-load-regenerated identifiers, each rule
///   verified by exporting identical bytes twice and diffing the raws:
///   - Objects named ExecuteUbergraph_*/ *_MERGED and objects flagged
///     bIsIntermediateNode=True (plus anything nested inside them) are
///     regenerated on load; inside them GraphGuid/NodeGuid/MemberGuid/
///     InterfaceGuid/PersistentGuid, unresolvable LinkedTo guids and
///     NSLOCTEXT keys are masked. A lone intermediate node marks only
///     itself, never its whole containing graph.
///   - PinId is not reliably persistent (reconstructed pins get fresh
///     ids each load), so PinId is normalized to a context token
///     P_&lt;graph&gt;.&lt;node&gt;__&lt;pin&gt; — node name + pin name +
///     OWNING GRAPH, so same-named pins in different graphs and the
///     authored vs compiled instances of a node stay distinct. LinkedTo
///     guid references resolve through the same map, preserving
///     connection identity (node + pin) rather than volatile guids.
///     Pins whose context key is reused by a different guid keep their
///     raw value rather than collapsing into each other.
/// </summary>
public static class ExportNormalizer
{
    public const string PackagePlaceholder = "UatdAssetPackage";

    /// <summary>Conservative view: staged package name only.</summary>
    public static string Normalize(string rawText, string stagedStem) =>
        rawText.Replace(stagedStem, PackagePlaceholder, StringComparison.Ordinal);

    private static readonly Regex BeginObjectPattern =
        new(@"^\s*Begin Object\b.*?\bName=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex BeginPathPattern =
        new(@"ExportPath=""[^'""]*'([^']+)'""", RegexOptions.Compiled);
    private static readonly Regex EndObjectPattern =
        new(@"^\s*End Object\b", RegexOptions.Compiled);
    private static readonly Regex PinIdPattern =
        new(@"PinId=([0-9A-Fa-f]{32})", RegexOptions.Compiled);
    private static readonly Regex PinNamePattern =
        new(@"PinName=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex LinkedToGuidPattern =
        new(@"\b([0-9A-Fa-f]{32})\b(?=[,)])", RegexOptions.Compiled);
    // Whole generated graph objects (name match on the Begin line's
    // object name or its ExportPath).
    private static readonly Regex GeneratedName =
        new(@"ExecuteUbergraph_|_MERGED(?=[.'""])", RegexOptions.Compiled);
    // First path segment after ':' in an ExportPath = the graph name.
    private static readonly Regex GraphSegment =
        new(@":([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex GeneratedFieldGuid =
        new(@"\b(?:GraphGuid|MemberGuid|InterfaceGuid|PersistentGuid)=" +
            @"[0-9A-Fa-f]{32}", RegexOptions.Compiled);
    private static readonly Regex NodeGuidPattern =
        new(@"\bNodeGuid=([0-9A-Fa-f]{32})", RegexOptions.Compiled);
    private static readonly Regex NsLocText =
        new(@"NSLOCTEXT\(""([^""]*)"",\s*""[^""]*"",", RegexOptions.Compiled);
    private static readonly Regex Sanitize = new(@"[^A-Za-z0-9_.]", RegexOptions.Compiled);

    /// <summary>Filtered view: conservative + per-load identifier noise.</summary>
    public static string NormalizeFiltered(string rawText, string stagedStem)
    {
        string text = Normalize(rawText, stagedStem);
        var lines = text.Split('\n');

        // Pass 1 — pin guid -> context token map (graph.node__pin), plus the
        // volatile-object sets. An object flagged bIsIntermediateNode=True is
        // volatile BY ITSELF (never its whole containing graph); a graph whose
        // K2Node_FunctionEntry is intermediate is a compiler-generated
        // function (verified: only ExecuteUbergraph/event-dispatch/timeline/
        // delegate-signature graphs have one — authored graphs like
        // UserConstructionScript carry intermediate nodes of other types and
        // stay stable), so everything inside it is a regenerated clone.
        var pinMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedKeys = new Dictionary<string, string>(StringComparer.Ordinal); // key -> first guid
        var volatileObjects = new HashSet<string>(StringComparer.Ordinal);
        var volatileGraphs = new HashSet<string>(StringComparer.Ordinal);
        var names = new Stack<string>();
        var graphs = new Stack<string>();
        var beginPaths = new Stack<string>();
        foreach (var line in lines)
        {
            var b = BeginObjectPattern.Match(line);
            if (b.Success)
            {
                names.Push(b.Groups[1].Value);
                var seg = GraphSegment.Match(line);
                graphs.Push(seg.Success ? seg.Groups[1].Value : "");
                var path = BeginPathPattern.Match(line);
                beginPaths.Push(path.Success ? path.Groups[1].Value : "");
            }
            else if (EndObjectPattern.IsMatch(line) && names.Count > 0)
            {
                names.Pop(); graphs.Pop(); beginPaths.Pop();
            }

            if (line.Contains("bIsIntermediateNode=True") && beginPaths.Count > 0 &&
                beginPaths.Peek().Length > 0)
            {
                volatileObjects.Add(beginPaths.Peek());
                if (names.Peek().StartsWith("K2Node_FunctionEntry", StringComparison.Ordinal) &&
                    graphs.Peek().Length > 0)
                {
                    volatileGraphs.Add(graphs.Peek());
                }
            }

            var p = PinIdPattern.Match(line);
            if (!p.Success || names.Count == 0) continue;
            string guid = p.Groups[1].Value;
            if (pinMap.ContainsKey(guid)) continue;
            string pin = PinNamePattern.Match(line) is { Success: true } m
                ? m.Groups[1].Value : "pin";
            string key = graphs.Peek() + "." + names.Peek() + "__" + pin;
            key = Sanitize.Replace(key, "_");
            if (usedKeys.TryGetValue(key, out string? other) && other != guid)
            {
                // Same (graph,node,pin) context reused by a different guid —
                // ambiguous: leave the raw guid rather than collapse two
                // distinct pins into one identity.
                pinMap[guid] = guid;
            }
            else
            {
                usedKeys[key] = guid;
                pinMap[guid] = "P_" + key;
            }
        }

        // Pass 2 — emit.
        var sb = new StringBuilder(text.Length);
        var genFlags = new Stack<bool>();
        var names2 = new Stack<string>();
        foreach (var line in lines)
        {
            var b = BeginObjectPattern.Match(line);
            if (b.Success)
            {
                names2.Push(b.Groups[1].Value);
                var path = BeginPathPattern.Match(line);
                string p = path.Success ? path.Groups[1].Value : "";
                var seg = GraphSegment.Match(line);
                bool gen = (genFlags.Count > 0 && genFlags.Peek()) ||
                           GeneratedName.IsMatch(line) ||
                           (p.Length > 0 && volatileObjects.Contains(p)) ||
                           (seg.Success && volatileGraphs.Contains(seg.Groups[1].Value));
                genFlags.Push(gen);
            }
            else if (EndObjectPattern.IsMatch(line))
            {
                if (genFlags.Count > 0) genFlags.Pop();
                if (names2.Count > 0) names2.Pop();
            }

            string outLine = line;
            bool inVolatile = genFlags.Count > 0 && genFlags.Peek();

            outLine = PinIdPattern.Replace(outLine,
                m => "PinId=" + (pinMap.TryGetValue(m.Groups[1].Value, out var t)
                    ? t : m.Groups[1].Value));

            if (outLine.Contains("LinkedTo="))
            {
                outLine = LinkedToGuidPattern.Replace(outLine, m =>
                {
                    if (pinMap.TryGetValue(m.Groups[1].Value, out var t)) return t;
                    return inVolatile ? PackagePlaceholder : m.Groups[1].Value;
                });
            }

            if (inVolatile)
            {
                outLine = GeneratedFieldGuid.Replace(outLine,
                    m => m.Value[..m.Value.IndexOf('=')] + "=" + PackagePlaceholder);
                outLine = NodeGuidPattern.Replace(outLine,
                    m => "NodeGuid=N_" + (names2.Count > 0 ? names2.Peek() : "gen"));
                outLine = NsLocText.Replace(outLine,
                    m => "NSLOCTEXT(\"" + m.Groups[1].Value + "\", \"" + PackagePlaceholder + "\",");
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(outLine);
        }
        return sb.ToString();
    }

    public static void WriteNormalizedCopy(string rawT3dPath, string stagedStem,
        string outPath, bool filtered) =>
        File.WriteAllText(outPath,
            filtered ? NormalizeFiltered(File.ReadAllText(rawT3dPath), stagedStem)
                     : Normalize(File.ReadAllText(rawT3dPath), stagedStem));
}
