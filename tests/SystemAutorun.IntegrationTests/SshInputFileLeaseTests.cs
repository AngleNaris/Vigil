using System.IO;
using SystemAutorun.Windows.Execution;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class SshInputFileLeaseTests
{
    [Fact]
    public void PinsRegularInputAgainstWriteReplacementAndAncestorRenameWithoutReadingItsContents()
    {
        using var fixture = new OwnedDirectory();
        var path = Path.Combine(fixture.Root, "known hosts"); File.WriteAllText(path, "synthetic");
        using (SshInputFileLease.Open(path, 1024))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
        }
        File.WriteAllText(path, "released"); Assert.Equal("released", File.ReadAllText(path));
    }

    [Fact]
    public void RejectsOversizedOrMissingInputsAndReleasesItsHandlesOnFailure()
    {
        using var fixture = new OwnedDirectory();
        var path = Path.Combine(fixture.Root, "large input");
        using (var stream = File.Create(path)) { stream.SetLength(1025); }
        Assert.Throws<IOException>(() => SshInputFileLease.Open(path, 1024));
        File.Delete(path);
        Assert.Throws<IOException>(() => SshInputFileLease.Open(path, 1024));
        File.WriteAllText(path, "small"); using var lease = SshInputFileLease.Open(path, 1024);
    }

    [Fact]
    public async Task RejectsReparseAncestorBeforeOpeningAnyRedirectedInput()
    {
        using var fixture = new OwnedDirectory();
        var target = Path.Combine(fixture.Root, "target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "known_hosts"), "synthetic");
        var link = Path.Combine(fixture.Root, "junction");
        var result = await new HiddenProcessRunner().RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "mklink", "/J", link, target], TimeSpan.FromSeconds(5), maximumOutputCharacters: 4096);
        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        try
        {
            var exception = Assert.Throws<IOException>(() => SshInputFileLease.Open(Path.Combine(link, "known_hosts"), 1024));
            Assert.Contains("Reparse", exception.Message, StringComparison.Ordinal);
            Assert.Equal("synthetic", File.ReadAllText(Path.Combine(target, "known_hosts")));
        }
        finally
        {
            var resolved = Path.GetFullPath(link);
            if (!resolved.Equals(Path.Combine(fixture.Root, "junction"), StringComparison.Ordinal)) { throw new InvalidOperationException("Non-owned junction."); }
            Directory.Delete(resolved);
        }
    }

    [Fact]
    public void ExistingPublicKeySidecarsArePinnedAndMissingCandidatesAreNotCreated()
    {
        using var fixture = new OwnedDirectory();
        var path = Path.Combine(fixture.Root, "identity.pub");
        Assert.Null(SshInputFileLease.OpenOptional(path, 1024)); Assert.False(File.Exists(path));
        File.WriteAllText(path, "synthetic public key");
        using (var lease = SshInputFileLease.OpenOptional(path, 1024))
        {
            Assert.NotNull(lease); Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
        }
        File.WriteAllText(path, "released");
    }

    [Fact]
    public void SharedConfigurationDirectoryPinsAlsoDenyBackupAndRootRename()
    {
        using var fixture = new OwnedDirectory();
        // Keep the lifetime helper internal: verify its native guard without adding a public production API.
        var type = typeof(SystemAutorun.Core.Configuration.ConfigurationWorkspace).Assembly
            .GetType("SystemAutorun.Core.Configuration.ConfigurationDocumentFiles", throwOnError: true)!;
        using var pins = (IDisposable)Activator.CreateInstance(type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [new SystemAutorun.Core.Infrastructure.ProjectPaths(fixture.Root)], null)!;
        var backup = (string)type.GetMethod("PinBackupDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(pins, null)!;
        Assert.Throws<IOException>(() => Directory.Move(backup, backup + "-must-not-move"));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-must-not-move"));
    }

    private sealed class OwnedDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-ssh-input-").FullName;
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-ssh-input-", StringComparison.Ordinal)) { throw new InvalidOperationException("Non-owned SSH fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
