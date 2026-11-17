using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SystemAutorun.Windows.Startup;

// Fixed internal member names only, never RPC input. Bypass the DLR COM discovery
// path without caching native interfaces here. No RCW escapes in a DTO.
internal static class StartupComDispatch
{
    internal static object Call(object target, string member, params object?[] arguments) =>
        // Automation Item(index) may be a property getter rather than a method.
        Invoke(target, member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, arguments)
        ?? throw new InvalidOperationException($"COM member {member} returned no object.");

    internal static void Command(object target, string member, params object?[] arguments) =>
        _ = Invoke(target, member, BindingFlags.InvokeMethod, arguments);

    internal static T Get<T>(object target, string member) =>
        (T)(Invoke(target, member, BindingFlags.GetProperty, [])
            ?? throw new InvalidOperationException($"COM property {member} returned no value."));

    internal static string? Text(object target, string member) =>
        Invoke(target, member, BindingFlags.GetProperty, []) as string;

    internal static void SetText(object target, string member, string value) =>
        _ = Invoke(target, member, BindingFlags.SetProperty, [value]);

    private static object? Invoke(object target, string member, BindingFlags operation, object?[] arguments)
    {
        try
        {
            return target.GetType().InvokeMember(member, operation | BindingFlags.Public | BindingFlags.Instance,
                binder: null, target, arguments, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        finally { GC.KeepAlive(target); }
    }
}
