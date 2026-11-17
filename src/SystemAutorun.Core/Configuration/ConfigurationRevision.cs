using System.Security.Cryptography;
using System.Text;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Configuration;

public static class ConfigurationRevision
{
    public static async Task<string> ReadAsync(
        ProjectPaths paths,
        bool includePreferences = true,
        CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = includePreferences
            ? new[] { paths.ConfigPath, paths.OverridesPath, paths.GuiSettingsPath }
            : [paths.ConfigPath, paths.OverridesPath];
        foreach (var path in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\0"));
            FileStream stream;
            try
            {
                stream = SnapshotFile.OpenRead(path);
            }
            catch (FileNotFoundException)
            {
                hash.AppendData([0]);
                continue;
            }

            await using (stream.ConfigureAwait(false))
            {
                hash.AppendData([1]);
                hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
