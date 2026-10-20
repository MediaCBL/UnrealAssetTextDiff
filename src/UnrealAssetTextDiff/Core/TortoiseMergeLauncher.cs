using System.Diagnostics;

namespace UnrealAssetTextDiff;

public static class TortoiseMergeLauncher
{
    /// <summary>
    /// Launch TortoiseMerge as a read-only two-file text diff.
    /// Returns null on success, an error description otherwise.
    /// Note: TortoiseMerge may hand off to TortoiseProc.exe and return
    /// immediately — a successful launch is not proof the window is up
    /// and says nothing about whether the files differ.
    /// </summary>
    public static string? TryLaunch(string tortoisePath, string baseFile, string mineFile,
        string baseLabel, string mineLabel)
    {
        try
        {
            var psi = new ProcessStartInfo(tortoisePath)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("/base:" + baseFile);
            psi.ArgumentList.Add("/mine:" + mineFile);
            psi.ArgumentList.Add("/basename:" + baseLabel);
            psi.ArgumentList.Add("/minename:" + mineLabel);
            psi.ArgumentList.Add("/readonly");
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
