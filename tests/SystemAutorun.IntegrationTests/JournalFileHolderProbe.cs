using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.IntegrationTests;

// Diagnostic-only Restart Manager queries for ONE owned journal file. Never registers
// services/processes, shuts down/restarts applications, or changes file sharing/ACLs.
internal static class JournalFileHolderProbe
{
    internal sealed class Result
    {
        public uint Error { get; set; }
        public uint EndError { get; set; }
        public int[] ProcessIds { get; set; } = [];
        public override string ToString() => $"rmError={Error}, rmEndError={EndError}, pids=[{string.Join(',', ProcessIds)}]";
    }

    internal static Result Capture(string ownedRoot, string path)
    {
        var directory = Path.GetFullPath(Path.Combine(ownedRoot, ".autorun-state", CurrentUserIdentity.Key, "operations"));
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).EndsWith(".json", StringComparison.Ordinal))
        { throw new InvalidOperationException("Refusing Restart Manager query outside the owned journal."); }
        if (!File.Exists(full)) { return new() { Error = 2 }; }
        var result = new Result();
        result.Error = RmStartSession(out var session, 0, new StringBuilder(33));
        if (result.Error != 0) { return result; }
        try
        {
            result.Error = RmRegisterResources(session, 1, [full], 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (result.Error == 0)
            {
                uint count = 0;
                result.Error = RmGetList(session, out var needed, ref count, null, out _);
                if (result.Error == 234 && needed is > 0 and <= 64)
                {
                    var apps = new ProcessInfo[needed]; count = needed;
                    result.Error = RmGetList(session, out _, ref count, apps, out _);
                    if (result.Error == 0) { result.ProcessIds = apps.Take((int)count).Select(app => app.Process.ProcessId).ToArray(); }
                }
            }
        }
        finally { result.EndError = RmEndSession(session); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public int ProcessId; public FILETIME StartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? ServiceName;
        public uint ApplicationType;
        public uint Status;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmStartSession(out uint handle, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmRegisterResources(uint handle, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmGetList(uint handle, out uint needed, ref uint count,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] ProcessInfo[]? apps, out uint rebootReasons);
    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmEndSession(uint handle);
}
