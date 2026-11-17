using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    public static IEnumerable<object[]> ElevatedClosureCases()
    {
        foreach (var command in new[] { "startup-disable", "startup-restore", "startup-rebuild" })
        foreach (var shape in new[] { "bare", "malformed", "legacy-options" }) { yield return [command, shape]; }
    }

    [Theory]
    [MemberData(nameof(ElevatedClosureCases))]
    public async Task ElevatedClosureRejectsTheActualManagedEntryBeforeRootAndProfileEffects(string command, string shape)
    {
        using var fixture = new Fixture(); var root = fixture.Root; var notCreated = Path.Combine(root, "must-not-resolve");
        string[] requested = shape switch
        {
            "bare" => [command],
            "malformed" => [command, "--root"],
            _ => [command, "--root", notCreated, "--source", "UNSUPPORTED_OWNED_SOURCE", "--name", "never-matches-" + Guid.NewGuid().ToString("N"),
                "--origin", Path.Combine(root, "not-a-real-startup", "owned.lnk"), "--backup-id", Guid.NewGuid().ToString("N")],
        };
        File.WriteAllBytes(Path.Combine(root, "elevated-denied-request.json"), JsonSerializer.SerializeToUtf8Bytes(requested));
        var bundle = new CrashHostBundle(root); var before = ElevatedClosureImages(root); var directories = ElevatedClosureDirectories(root);
        var profile = ElevatedProfileFingerprint();
        await using (var child = bundle.Start("elevated-denied"))
        {
            using var report = await child.ReadReportAsync(); Assert.Equal(ElevatedCommandPolicy.RejectedExitCode, report.RootElement.GetProperty("helperExitCode").GetInt32());
        }
        Assert.False(Directory.Exists(notCreated)); var after = ElevatedClosureImages(root);
        Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); }
        Assert.Equal(directories, ElevatedClosureDirectories(root)); Assert.Equal(profile, ElevatedProfileFingerprint());
        fixture.AssertNoTransactionOrTemps();
    }

    private static Dictionary<string, byte[]> ElevatedClosureImages(string root)
    {
        var excluded = Path.Combine(root, "history-crash-host") + Path.DirectorySeparatorChar;
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.StartsWith(excluded, StringComparison.Ordinal))
            .Order().ToDictionary(path => path, File.ReadAllBytes);
    }
    private static string[] ElevatedClosureDirectories(string root)
    {
        var excluded = Path.Combine(root, "history-crash-host");
        return Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(path => path != excluded
            && !path.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order().ToArray();
    }
    private static string? ElevatedProfileFingerprint()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil", "logs", "elevated.log");
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(input)); // Never read or echo the user's log text.
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
