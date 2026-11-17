using System.Security.Principal;
using Microsoft.Win32;

namespace SystemAutorun.Windows.Startup;

// Internal, fixed-source access only. Neither configuration nor RPC can redirect native locations.
// Tests map these sources to owned HKCU subkeys/folders; the mutation/COM code stays real.
internal interface IStartupNativeAccess
{
    bool IsAdministrator { get; }
    string GetShortcutDirectory(string source);
    RegistryKey? OpenRunKey(string source, bool writable, bool create);
}

internal sealed class WindowsStartupNativeAccess : IStartupNativeAccess
{
    internal static readonly WindowsStartupNativeAccess Instance = new();
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private WindowsStartupNativeAccess() { }

    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string GetShortcutDirectory(string source) => Environment.GetFolderPath(source switch
    {
        StartupManager.UserStartupSource => Environment.SpecialFolder.Startup,
        StartupManager.CommonStartupSource => Environment.SpecialFolder.CommonStartup,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported shortcut source."),
    });

    internal static (RegistryHive Hive, RegistryView View) RegistryLocation(string source) => source switch
    {
        StartupManager.HkcuRunSource => (RegistryHive.CurrentUser, RegistryView.Default),
        StartupManager.HklmRunSource => (RegistryHive.LocalMachine, RegistryView.Registry64),
        StartupManager.HklmWow64RunSource => (RegistryHive.LocalMachine, RegistryView.Registry32),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported startup source."),
    };

    public RegistryKey? OpenRunKey(string source, bool writable, bool create)
    {
        var (hive, view) = RegistryLocation(source);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        return create ? baseKey.CreateSubKey(RunSubKey, writable) : baseKey.OpenSubKey(RunSubKey, writable);
    }
}
