using System.Text;

namespace SystemAutorun.Agent;

internal static class AgentFallbackLog
{
    internal static async Task WriteAsync(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemAutorun",
                "logs");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(
                Path.Combine(directory, "agent-bootstrap.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // The GUI subsystem has no stderr. Bootstrap logging is best effort only.
        }
    }
}
