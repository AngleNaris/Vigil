using System.Text;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Native;

namespace SystemAutorun.Windows.Execution;

public sealed class NativeProcessLauncher
{
    public int Start(AutorunTask task)
    {
        if (task.Type != AutorunTaskType.Windows || string.IsNullOrWhiteSpace(task.Executable))
        {
            throw new ArgumentException("A Windows task with an executable is required.", nameof(task));
        }

        var executable = ExecutableResolver.Resolve(task.Executable, task.WorkingDirectory);
        var workingDirectory = ResolveWorkingDirectory(task.WorkingDirectory, executable);
        var commandLine = new StringBuilder(BuildCommandLine(executable, task.Arguments));
        var startupInfo = new NativeMethods.StartupInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.StartupInfo>(),
        };
        var flags = NativeMethods.CreateUnicodeEnvironment;

        switch (task.Launch.WindowMode)
        {
            case WindowMode.Hidden:
            case WindowMode.Tray:
                flags |= NativeMethods.CreateNoWindow;
                startupInfo.Flags |= NativeMethods.StartfUseShowWindow;
                startupInfo.ShowWindow = NativeMethods.SwHide;
                break;
            case WindowMode.NoConsole:
                flags |= NativeMethods.CreateNoWindow;
                break;
            case WindowMode.Minimized:
                startupInfo.Flags |= NativeMethods.StartfUseShowWindow;
                startupInfo.ShowWindow = NativeMethods.SwShowMinNoActive;
                break;
            case WindowMode.Normal:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(task), task.Launch.WindowMode, null);
        }

        if (!NativeMethods.CreateProcess(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                flags,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            throw NativeMethods.LastError($"CreateProcessW failed for '{executable}'");
        }

        try
        {
            return processInformation.ProcessId;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(processInformation.Thread);
            _ = NativeMethods.CloseHandle(processInformation.Process);
        }
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var values = new List<string>(arguments.Count + 1) { QuoteArgument(executable) };
        values.AddRange(arguments.Select(QuoteArgument));
        return string.Join(' ', values);
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static string ResolveWorkingDirectory(string? configured, string executable)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            if (!Directory.Exists(expanded))
            {
                throw new DirectoryNotFoundException($"Working directory not found: {expanded}");
            }

            return Path.GetFullPath(expanded);
        }

        return Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException($"Executable has no parent directory: {executable}");
    }
}
