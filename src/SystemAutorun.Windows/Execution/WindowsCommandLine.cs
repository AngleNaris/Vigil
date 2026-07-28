using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SystemAutorun.Windows.Execution;

public static class WindowsCommandLine
{
    public static IReadOnlyList<string> ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Array.Empty<string>();
        }

        var pointer = CommandLineToArgvW($"placeholder.exe {arguments}", out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CommandLineToArgvW failed.");
        }

        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result[index - 1] = Marshal.PtrToStringUni(valuePointer) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
