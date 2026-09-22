using System.Runtime.InteropServices;
using Windows.Win32;

namespace Nami.Services;

internal static unsafe class CommandLine
{
    /// <summary>Split a Windows command line string using the same rules as the C runtime.</summary>
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        int argc;
        var argv = PInvoke.CommandLineToArgv(commandLine, out argc);
        if (argv == null) return result;
        try
        {
            for (int i = 0; i < argc; i++)
                result.Add(argv[i].ToString());
        }
        finally
        {
            PInvoke.LocalFree((Windows.Win32.Foundation.HLOCAL)(nint)argv);
        }
        return result;
    }
}
