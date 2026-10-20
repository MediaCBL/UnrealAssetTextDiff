using System.IO;
using Microsoft.Win32;

namespace UnrealAssetTextDiff;

/// <summary>
/// Best-effort detection of UnrealEditor-Cmd.exe and TortoiseMerge.exe.
/// Detection never overrides an explicit path; it only fills in blanks.
/// </summary>
public static class ToolLocator
{
    /// <summary>Candidate UnrealEditor-Cmd.exe paths: source builds (HKCU Builds)
    /// and Launcher installs (HKLM Unreal Engine x.y), newest first.</summary>
    public static List<string> DetectEngineCmdPaths()
    {
        var found = new List<string>();
        void AddIfCmd(string? root)
        {
            if (string.IsNullOrEmpty(root)) return;
            string cmd = Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
            if (File.Exists(cmd) && !found.Contains(cmd, StringComparer.OrdinalIgnoreCase))
                found.Add(cmd);
        }

        // Source-built engines registered through UnrealVersionSelector / editor.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            if (key != null)
                foreach (var name in key.GetValueNames())
                    AddIfCmd(key.GetValue(name) as string);
        }
        catch { }

        // Epic Games Launcher installs.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
            if (key != null)
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(sub);
                    AddIfCmd(subKey?.GetValue("InstalledDirectory") as string);
                }
        }
        catch { }

        // Conventional Launcher location fallback.
        try
        {
            string epic = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
            if (Directory.Exists(epic))
                foreach (var dir in Directory.GetDirectories(epic, "UE_*"))
                    AddIfCmd(dir);
        }
        catch { }

        return found;
    }

    public static string? DetectTortoiseMerge()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TortoiseSVN");
            string? dir = key?.GetValue("Directory") as string;
            if (!string.IsNullOrEmpty(dir))
            {
                string exe = Path.Combine(dir, "bin", "TortoiseMerge.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }

        string standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "TortoiseSVN", "bin", "TortoiseMerge.exe");
        return File.Exists(standard) ? standard : null;
    }

    /// <summary>Walk up from a directory looking for a *.uproject.</summary>
    public static string? FindUprojectUpwards(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var hit = dir.GetFiles("*.uproject");
            if (hit.Length > 0) return hit[0].FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
