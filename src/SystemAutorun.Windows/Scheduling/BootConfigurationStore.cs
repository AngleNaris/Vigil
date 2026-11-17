using System.Security.AccessControl;
using System.Security.Principal;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Windows.Scheduling;

public sealed class BootConfigurationStore
{
    private const string ProductDirectoryName = "Vigil";
    private const string BootDirectoryName = "Boot";
    private static readonly string[] StagingFileNames =
    [
        "autorun.yaml",
        "autorun.overrides.json",
        ".previous-autorun.yaml",
        ".previous-autorun.overrides.json",
    ];

    private readonly ConfigurationLoader _loader;

    public BootConfigurationStore(ConfigurationLoader loader)
    {
        _loader = loader;
    }

    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductDirectoryName,
        BootDirectoryName);

    public static string ResolveTrustedAgentPath()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Vigil.Agent.exe"));
        var programFiles = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!File.Exists(path)
            || !IsWithinDirectory(path, programFiles)
            || !string.Equals(Path.GetFileName(path), "Vigil.Agent.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The SYSTEM Agent must be installed beside the elevated helper under Program Files.");
        }

        EnsureNoReparsePoints(path, programFiles);
        return path;
    }

    public async Task<ProjectPaths> PrepareAsync(
        ProjectPaths sourcePaths,
        CancellationToken cancellationToken = default)
    {
        _ = await _loader.LoadAsync(sourcePaths.RootDirectory, cancellationToken).ConfigureAwait(false);
        EnsureRegularFile(sourcePaths.ConfigPath, "Boot configuration source");
        if (File.Exists(sourcePaths.OverridesPath))
        {
            EnsureRegularFile(sourcePaths.OverridesPath, "Boot overrides source");
        }

        var productDirectory = Path.GetDirectoryName(RootDirectory)
            ?? throw new InvalidOperationException("Unable to resolve the protected product directory.");
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        EnsureDirectoryPathHasNoReparsePoints(productDirectory, commonApplicationData);
        EnsureProtectedDirectory(productDirectory);

        var stagingDirectory = Path.Combine(productDirectory, $".boot-stage-{Guid.NewGuid():N}");
        EnsureProtectedDirectory(stagingDirectory);
        Exception? prepareFailure = null;
        try
        {
            File.Copy(sourcePaths.ConfigPath, Path.Combine(stagingDirectory, "autorun.yaml"));
            if (File.Exists(sourcePaths.OverridesPath))
            {
                File.Copy(sourcePaths.OverridesPath, Path.Combine(stagingDirectory, "autorun.overrides.json"));
            }

            var stagedConfiguration = await _loader.LoadAsync(stagingDirectory, cancellationToken)
                .ConfigureAwait(false);
            EnsureContainedDataPath(stagedConfiguration.Settings.LogDirectory, stagingDirectory, "log_dir");
            EnsureContainedDataPath(stagedConfiguration.Settings.BackupDirectory, stagingDirectory, "backup_dir");

            EnsureProtectedDirectory(RootDirectory);
            var stagedConfig = Path.Combine(stagingDirectory, "autorun.yaml");
            var stagedOverrides = Path.Combine(stagingDirectory, "autorun.overrides.json");
            var targetConfig = Path.Combine(RootDirectory, "autorun.yaml");
            var targetOverrides = Path.Combine(RootDirectory, "autorun.overrides.json");
            var previousConfig = Path.Combine(stagingDirectory, ".previous-autorun.yaml");
            var previousOverrides = Path.Combine(stagingDirectory, ".previous-autorun.overrides.json");
            var hadConfig = BackupIfPresent(targetConfig, previousConfig);
            var hadOverrides = BackupIfPresent(targetOverrides, previousOverrides);
            var targetMutationStarted = false;

            try
            {
                targetMutationStarted = true;
                CopyAtomically(stagedConfig, targetConfig);
                if (File.Exists(stagedOverrides))
                {
                    CopyAtomically(stagedOverrides, targetOverrides);
                }
                else
                {
                    File.Delete(targetOverrides);
                }

                var protectedConfiguration = await _loader.LoadAsync(RootDirectory, cancellationToken)
                    .ConfigureAwait(false);
                EnsureContainedDataPath(protectedConfiguration.Settings.LogDirectory, RootDirectory, "log_dir");
                EnsureContainedDataPath(protectedConfiguration.Settings.BackupDirectory, RootDirectory, "backup_dir");
                EnsureProtectedDataDirectory(protectedConfiguration.Settings.LogDirectory);
                EnsureProtectedDataDirectory(protectedConfiguration.Settings.BackupDirectory);
                return new ProjectPaths(RootDirectory);
            }
            catch (Exception installException) when (targetMutationStarted)
            {
                try
                {
                    RestoreFile(previousConfig, targetConfig, hadConfig);
                    RestoreFile(previousOverrides, targetOverrides, hadOverrides);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Boot configuration deployment failed and rollback was incomplete.",
                        installException,
                        rollbackException);
                }

                throw;
            }
        }
        catch (Exception exception)
        {
            prepareFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                DeleteStagingDirectory(stagingDirectory);
            }
            catch when (prepareFailure is not null)
            {
                // Preserve the deployment failure; a protected orphaned stage can be cleaned later.
            }
        }
    }

    internal static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(
            fullDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureProtectedDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        var security = CreateProtectedDirectorySecurity();
        if (!directory.Exists)
        {
            directory.Create(security);
        }

        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"Protected directory cannot be a reparse point: {path}");
        }

        directory.SetAccessControl(security);
        var applied = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!applied.AreAccessRulesProtected || !administrators.Equals(applied.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException($"Protected directory ACL verification failed: {path}");
        }
    }

    private static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static void EnsureNoReparsePoints(string path, string trustedRoot)
    {
        var current = new FileInfo(path);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"Trusted executable cannot be a reparse point: {path}");
        }

        var trusted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        for (var directory = current.Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Trusted executable path contains a reparse point: {directory.FullName}");
            }

            if (string.Equals(
                Path.TrimEndingDirectorySeparator(directory.FullName),
                trusted,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException("Trusted executable is outside Program Files.");
    }

    private static void EnsureContainedDataPath(string path, string root, string settingName)
    {
        if (!IsWithinDirectory(path, root))
        {
            throw new ConfigurationException(
                $"Boot configuration setting '{settingName}' must stay inside the protected boot root.");
        }
    }

    private static void EnsureProtectedDataDirectory(string path)
    {
        EnsureDirectoryPathHasNoReparsePoints(path, RootDirectory);
        EnsureProtectedDirectory(path);
        EnsureDirectoryPathHasNoReparsePoints(path, RootDirectory);
    }

    private static void EnsureDirectoryPathHasNoReparsePoints(string path, string trustedRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        if (!string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            && !IsWithinDirectory(fullPath, fullRoot))
        {
            throw new UnauthorizedAccessException($"Protected path is outside its trusted root: {path}");
        }

        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Protected path contains a reparse point: {current.FullName}");
            }

            if (string.Equals(
                Path.TrimEndingDirectorySeparator(current.FullName),
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException($"Protected path is outside its trusted root: {path}");
    }

    private static void EnsureRegularFile(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException($"{description} must be a regular file: {path}");
        }
    }

    private static bool BackupIfPresent(string source, string backup)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        EnsureRegularFile(source, "Protected boot configuration");
        File.Copy(source, backup);
        return true;
    }

    private static void RestoreFile(string backup, string destination, bool existed)
    {
        if (existed)
        {
            CopyAtomically(backup, destination);
        }
        else
        {
            File.Delete(destination);
        }
    }

    private static void CopyAtomically(string source, string destination)
    {
        var temporaryPath = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.Copy(source, temporaryPath);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void DeleteStagingDirectory(string stagingDirectory)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            return;
        }

        var productDirectory = Path.GetDirectoryName(RootDirectory)
            ?? throw new InvalidOperationException("Unable to resolve the protected product directory.");
        if (!IsWithinDirectory(stagingDirectory, productDirectory)
            || !Path.GetFileName(stagingDirectory).StartsWith(".boot-stage-", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Refusing to delete an unexpected staging directory.");
        }

        if ((File.GetAttributes(stagingDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Refusing to traverse a staging reparse point.");
        }

        DeleteKnownStagingDirectory(stagingDirectory);
    }

    internal static void DeleteKnownStagingDirectory(string stagingDirectory)
    {
        foreach (var fileName in StagingFileNames)
        {
            EnsureStagingRootIsNotReparsePoint(stagingDirectory);
            var path = Path.Combine(stagingDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            EnsureRegularFile(path, "Protected staging file");
            File.Delete(path);
        }

        EnsureStagingRootIsNotReparsePoint(stagingDirectory);
        var unexpectedEntry = Directory.EnumerateFileSystemEntries(stagingDirectory).FirstOrDefault();
        if (unexpectedEntry is not null)
        {
            throw new UnauthorizedAccessException(
                $"Refusing to recursively delete unexpected staging content: {unexpectedEntry}");
        }

        Directory.Delete(stagingDirectory);
    }

    private static void EnsureStagingRootIsNotReparsePoint(string stagingDirectory)
    {
        var attributes = File.GetAttributes(stagingDirectory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Refusing to traverse a staging reparse point.");
        }
    }
}
