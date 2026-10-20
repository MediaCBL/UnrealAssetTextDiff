using System.Runtime.InteropServices;
using System.Text;

namespace UnrealAssetTextDiff;

public static class ShortPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);

    /// <summary>
    /// 8.3 short path for use inside the DiffCmd template, where quotes are
    /// impossible. Returns null when 8.3 names are unavailable/disabled.
    /// </summary>
    public static string? TryGet(string longPath)
    {
        var sb = new StringBuilder(1024);
        int len = GetShortPathNameW(longPath, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Path safe to embed unquoted in the DiffCmd template: the 8.3 form
    /// when the path contains spaces, otherwise the original.
    /// </summary>
    public static string ForDiffCmd(string path)
    {
        if (!path.Contains(' ')) return path;
        var shortPath = TryGet(path);
        return shortPath != null && !shortPath.Contains(' ') ? shortPath : path;
    }
}
