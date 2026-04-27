using System.Diagnostics;
namespace sp2rdlGenExtension.Services;

internal static class DebugExpShutdown
{
    public static void TryCloseExpInstance()
    {
#if DEBUG
        try
        {
            var current = Process.GetCurrentProcess();
            if (!string.Equals(current.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase)
                || !IsExperimentalInstance(Environment.GetCommandLineArgs()))
            {
                return;
            }

            current.CloseMainWindow();
        }
        catch
        {
        }
#endif
    }

#if DEBUG
    private static bool IsExperimentalInstance(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "/RootSuffix", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && string.Equals(args[index + 1], "Exp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
#endif
}
