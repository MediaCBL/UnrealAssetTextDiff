using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnrealAssetTextDiff;

public sealed record StagedAsset(string SourcePath, string StagedPath, string StagedStem, string Sha256);

/// <summary>
/// Copies input assets into a per-comparison staging directory with unique,
/// package-name-safe file stems so the commandlet's own copy into
/// Saved/Diff can never collide between the two sides.
/// </summary>
public static class Staging
{
    private static readonly string[] CompanionExtensions = { ".uexp", ".uptnl", ".ubulk" };

    /// <summary>Map a filename stem to characters valid in a UE package name.</summary>
    public static string SanitizePackageFileStem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Asset";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append((c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z') ||
                      (c is >= '0' and <= '9') || c == '_' ? c : '_');
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "Asset" : s;
    }

    public static string Sha256OfFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    /// <summary>
    /// Human-meaningful base name for the staged file. Normally the input
    /// filename. When the input looks like a version-control temp file
    /// (no .uasset extension, e.g. svnXXXX.tmp), the diff label carries
    /// the real asset name (TortoiseSVN %bname).
    /// </summary>
    public static string DeriveAssetName(string inputPath, string? label)
    {
        string inputStem = Path.GetFileNameWithoutExtension(inputPath);
        string ext = Path.GetExtension(inputPath);
        bool inputLooksReal = ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase)
                              || ext.Equals(".umap", StringComparison.OrdinalIgnoreCase);
        if (inputLooksReal || string.IsNullOrWhiteSpace(label))
            return SanitizePackageFileStem(inputStem);

        // Labels may look like "BP_Barrel_A.uasset" or "BP_Barrel_A : r3989".
        var l = label;
        int colon = l.IndexOf(':');
        if (colon >= 0) l = l[..colon];
        l = Path.GetFileName(l.Trim());
        string? stem = l.Length > 0 ? Path.GetFileNameWithoutExtension(l) : null;
        return SanitizePackageFileStem(string.IsNullOrEmpty(stem) ? inputStem : stem);
    }

    /// <summary>
    /// Stage one input. Returns null when the input is missing. The staged
    /// stem embeds a content hash AND a per-session tag, because the
    /// commandlet copies inputs into the shared <Project>/Saved/Diff
    /// directory by basename — without the session tag, two concurrent
    /// runs of the same comparison would write the same filenames there.
    /// </summary>
    public static StagedAsset? Stage(string inputPath, string? label, string sideTag,
        string stageDir, string sessionTag)
    {
        if (!File.Exists(inputPath)) return null;
        Directory.CreateDirectory(stageDir);

        string hash = Sha256OfFile(inputPath);
        string stem = $"{DeriveAssetName(inputPath, label)}__{sideTag}__{hash[..8]}__{sessionTag}";
        string staged = Path.Combine(stageDir, stem + ".uasset");
        File.Copy(inputPath, staged, overwrite: true);

        foreach (var ext in CompanionExtensions)
        {
            string companion = Path.ChangeExtension(inputPath, ext);
            if (File.Exists(companion))
                File.Copy(companion, Path.ChangeExtension(staged, ext), overwrite: true);
        }
        return new StagedAsset(inputPath, staged, stem, hash);
    }
}
