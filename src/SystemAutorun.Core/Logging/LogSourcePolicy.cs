using System.Text.RegularExpressions;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Logging;

public static partial class LogSourcePolicy
{
    public static void ValidateDefinitions(IReadOnlyList<LogSourceDefinition>? sources)
    {
        if (sources is null || sources.Count > 32) { throw new ConfigurationException("log_sources must be an array with at most 32 sources."); }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source is null || !IdPattern().IsMatch(source.Id ?? "") || !ids.Add(source.Id!))
            { throw new ConfigurationException("Log source IDs must be unique, 1..64 ASCII letters/digits/dot/underscore/hyphen, starting with a letter/digit."); }
            if (source.Kind is not ("file" or "wsl-journal" or "windows-event"))
            { throw new ConfigurationException("Log source kind must be file, wsl-journal, or windows-event."); }
            if (source.TaskName is { } task && (string.IsNullOrWhiteSpace(task) || task.Length > 512))
            { throw new ConfigurationException("A log source task name must be 1..512 characters."); }
            if (source.Kind == "file")
            {
                try { _ = ResolveFilePath(source.Path!, System.IO.Path.GetTempPath()); }
                catch (ArgumentException exception) { throw new ConfigurationException("Invalid log source path: " + exception.Message, exception); }
            }
            else if (source.Path is not null) { throw new ConfigurationException("System log providers do not accept a file path or command."); }
            if (source.AdditionalSettings?.Keys.Any(key => new[] { "id", "kind", "taskName", "path" }.Contains(key, StringComparer.OrdinalIgnoreCase)) == true)
            { throw new ConfigurationException("Additional log source properties must not shadow built-in properties."); }
        }
    }

    public static string ResolveFilePath(string path, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1024 || path.StartsWith('\\') || path.StartsWith('/') || path.Any(char.IsControl)
            || path.IndexOf(':', Math.Min(path.Length, 2)) >= 0 || path.Contains('*') || path.Contains('?'))
        { throw new ArgumentException("Only local DOS file paths or root-relative paths are supported; UNC, device paths, streams and wildcards are rejected."); }
        if (path.Contains(':') && (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')))
        { throw new ArgumentException("Drive-relative paths are not supported."); }
        foreach (var component in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(path.Contains(':') ? 1 : 0))
        {
            if (component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ')
                || component.IndexOfAny(['<', '>', '"', '|']) >= 0 || DevicePattern().IsMatch(component.Split('.')[0].TrimEnd(' ')))
            { throw new ArgumentException("Ambiguous, reserved or traversing log file components are rejected."); }
        }
        var full = System.IO.Path.GetFullPath(path, root);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':')
        { throw new ArgumentException("A local DOS file path is required."); }
        return full;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();
}
