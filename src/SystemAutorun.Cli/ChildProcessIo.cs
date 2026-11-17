using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SystemAutorun.Cli;

internal static class ChildProcessIo
{
    // Process.Start on Windows can inherit ALL inheritable handles, not just its
    // chosen STD handles. Never lend the caller's capture pipes to a long-lived Agent.
    // This changes inheritance flags only: it neither closes nor redirects this CLI's IO.
    public static void PreventCaptureHandleInheritance()
    {
        if (Console.IsInputRedirected) { Clear(-10); }
        if (Console.IsOutputRedirected) { Clear(-11); }
        if (Console.IsErrorRedirected) { Clear(-12); }
    }

    private static void Clear(int standardHandle)
    {
        var handle = GetStdHandle(standardHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) { return; }
        if (!SetHandleInformation(handle, 1, 0))
        { throw new IOException("Could not isolate CLI capture handles from the daemon.", new Win32Exception(Marshal.GetLastWin32Error())); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
