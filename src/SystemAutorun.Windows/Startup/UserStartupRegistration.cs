using Microsoft.Win32;

namespace SystemAutorun.Windows.Startup;

public static class UserStartupRegistration
{
    public const string ValueName = "Vigil";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string executablePath, string rootDirectory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var current = key?.GetValue(ValueName) as string;
        return string.Equals(
            current,
            BuildCommand(executablePath, rootDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled, string executablePath, string rootDirectory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current-user Run registry key.");
        if (enabled)
        {
            key.SetValue(
                ValueName,
                BuildCommand(executablePath, rootDirectory),
                RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string BuildCommand(string executablePath, string rootDirectory) =>
        $"{Quote(executablePath)} --background --root {Quote(rootDirectory)}";

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
