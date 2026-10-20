using System.Runtime.InteropServices;

namespace UnrealAssetTextDiff;

/// <summary>
/// WinExe has no console; attach to the parent console so CLI output is
/// visible when launched from cmd/PowerShell. Safe no-op when there is none.
/// </summary>
public static class NativeConsole
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;
    private static bool _attached;

    public static void EnsureAttached()
    {
        if (_attached) return;
        _attached = true;
        if (AttachConsole(AttachParentProcess))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
    }

    public static void WriteLine(string s)
    {
        EnsureAttached();
        Console.Out.WriteLine(s);
        Console.Out.Flush();
    }

    public static void WriteError(string s)
    {
        EnsureAttached();
        Console.Error.WriteLine(s);
        Console.Error.Flush();
    }
}
