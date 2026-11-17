using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Scheduling;

namespace SystemAutorun.Windows.Startup;

public sealed class StartupRegistrationReader : IStartupRegistrationReader
{
    public Task<StartupRegistrationData> ReadAsync(string kind, CancellationToken cancellationToken = default)
    {
        if (!StartupRegistrationContract.ValidKind(kind)) { throw new ArgumentException("Unknown registration kind."); }
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<StartupRegistrationData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(kind == "self" ? ReadSelf() : ReadBoot(CreateScheduler));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "SystemAutorun registration metadata" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static StartupRegistrationData ReadSelf()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return ReadSelfValue(key, UserStartupRegistration.ValueName);
    }

    // Only the owned registry fixture uses the alternate key/name; RPC has no such input.
    internal static StartupRegistrationData ReadSelfValue(RegistryKey? key, string name)
    {
        if (key is null) { return new(false); }
        uint bytes = 0;
        var code = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var kind, null, ref bytes);
        if (code == 2) { return new(false); }
        CheckRegistryError(code);
        var kindName = kind switch { 0 => "None", 1 => "String", 2 => "ExpandString", 3 => "Binary", 4 => "DWord", 7 => "MultiString", 11 => "QWord", _ => "Unknown" };
        if (kind is not (1 or 2)) { return new(true, kindName); }
        var maximum = (StartupRegistrationContract.MaximumTextCharacters + 1) * 2;
        if (bytes > maximum) { throw new StartupRegistrationSourceException("registration_too_large"); }
        var buffer = new byte[maximum]; bytes = (uint)buffer.Length;
        code = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var actualKind, buffer, ref bytes);
        if (code == 2 || code == 0 && actualKind != kind) { throw new StartupRegistrationSourceException("registration_changed"); }
        if (code == 234 || bytes > buffer.Length) { throw new StartupRegistrationSourceException("registration_too_large"); }
        CheckRegistryError(code);
        if (bytes % 2 != 0) { throw new StartupRegistrationSourceException("registration_invalid"); }
        string command;
        try { command = new UnicodeEncoding(false, false, true).GetString(buffer, 0, (int)bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { throw new StartupRegistrationSourceException("registration_invalid"); }
        if (command.Length > StartupRegistrationContract.MaximumTextCharacters) { throw new StartupRegistrationSourceException("registration_too_large"); }
        return new(true, kindName, command);
    }

    internal static StartupRegistrationData ReadBoot(Func<object> createScheduler)
    {
        object? service = null, folder = null, task = null, definition = null, actions = null, action = null;
        try
        {
            service = createScheduler();
            StartupComDispatch.Command(service, "Connect");
            // Only missing-folder/task errors mean absent. Access denial and service errors escape.
            try
            {
                folder = StartupComDispatch.Call(service, "GetFolder", BootTaskScheduler.FolderPath);
                task = StartupComDispatch.Call(folder, "GetTask", BootTaskScheduler.TaskName);
            }
            catch (Exception exception) when (IsMissing(exception)) { return new(false); }
            var enabled = StartupComDispatch.Get<bool>(task, "Enabled");
            var state = BootTaskScheduler.MapState(StartupComDispatch.Get<int>(task, "State")).ToString().ToLowerInvariant();
            DateTime? last = NormalizeDate(StartupComDispatch.Get<DateTime>(task, "LastRunTime"));
            var result = StartupComDispatch.Get<int>(task, "LastTaskResult");
            DateTime? next = NormalizeDate(StartupComDispatch.Get<DateTime>(task, "NextRunTime"));
            var missed = StartupComDispatch.Get<int>(task, "NumberOfMissedRuns");
            definition = StartupComDispatch.Get<object>(task, "Definition");
            actions = StartupComDispatch.Get<object>(definition, "Actions");
            var count = StartupComDispatch.Get<int>(actions, "Count");
            int? type = null; string? executable = null, arguments = null;
            if (count == 1)
            {
                action = StartupComDispatch.Call(actions, "Item", 1);
                type = StartupComDispatch.Get<int>(action, "Type");
                if (type == 0)
                {
                    executable = StartupComDispatch.Text(action, "Path");
                    arguments = StartupComDispatch.Text(action, "Arguments");
                    CheckText(executable); CheckText(arguments);
                }
            }
            return new(true, Enabled: enabled, State: state, ActionCount: count, ActionType: type,
                Executable: executable, Arguments: arguments, LastRunTime: last, LastTaskResult: result,
                NextRunTime: next, MissedRuns: missed);
        }
        finally
        {
            // DLR/managed aliases are not reflected in the RCW import count. Keep
            // the graph live through observation; let the CLR reclaim it safely.
            foreach (var value in new[] { action, actions, definition, task, folder, service })
            { StartupManager.KeepComObjectAlive(value); }
        }
    }

    private static bool IsMissing(Exception exception) =>
        exception is COMException or FileNotFoundException or DirectoryNotFoundException
        && (uint)exception.HResult is 0x80070002 or 0x80070003;
    private static DateTime? NormalizeDate(DateTime value) => value.Year <= 1900 ? null : value;
    private static void CheckText(string? text)
    { if (text?.Length > StartupRegistrationContract.MaximumTextCharacters) { throw new StartupRegistrationSourceException("registration_too_large"); } }
    private static void CheckRegistryError(int code)
    {
        if (code == 5) { throw new UnauthorizedAccessException("Registration metadata is inaccessible."); }
        if (code != 0) { throw new Win32Exception(code, "Registration metadata query failed."); }
    }
    private static object CreateScheduler() => Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)
        ?? throw new PlatformNotSupportedException("Task Scheduler COM is unavailable.");

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved,
        out uint kind, [Out] byte[]? data, ref uint byteCount);
}
