using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Native;
using SystemAutorun.Windows.Scheduling;

namespace SystemAutorun.Windows.Execution;

public sealed record ObservedProcessMatch(
    int ProcessId,
    string ProcessName,
    long CreateFileTimeUtc,
    long TotalProcessorTimeTicks,
    long WorkingSetBytes,
    int HandleCount,
    long PeakWorkingSetBytes = 0,
    int ThreadCount = 0);

public sealed class ProcessMaintenanceExecutor
{
    private const string RequestFileName = "maintenance-request.json";
    private const string ResultFileName = "maintenance-result.json";
    private static readonly TimeSpan RequestFreshnessWindow = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly MaintenanceTaskScheduler _scheduler = new();

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public MaintenanceTaskStatus GetMaintenanceTaskStatus(string? rootDirectory = null) =>
        _scheduler.GetStatus(rootDirectory);

    public IReadOnlyDictionary<int, ObservedProcessMatch> ReadAllProcessMetrics()
    {
        var raw = NativeMethods.TryReadProcessMetrics();
        if (raw is null || raw.Count == 0)
        {
            return new Dictionary<int, ObservedProcessMatch>();
        }

        var result = new Dictionary<int, ObservedProcessMatch>(raw.Count);
        foreach (var (pid, metric) in raw)
        {
            result[pid] = new ObservedProcessMatch(
                metric.ProcessId,
                metric.ProcessName,
                metric.CreateFileTimeUtc,
                metric.TotalProcessorTimeTicks,
                Math.Max(0, metric.WorkingSetBytes),
                Math.Max(0, metric.HandleCount),
                Math.Max(Math.Max(0, metric.WorkingSetBytes), metric.PeakWorkingSetBytes),
                Math.Max(0, metric.ThreadCount));
        }

        return result;
    }

    public static bool TryReadGuiResourceCounts(int processId, out int gdiObjects, out int userObjects) =>
        NativeMethods.TryGetGuiResourceCounts(processId, out gdiObjects, out userObjects);

    public IReadOnlyList<ObservedProcessMatch> MatchProcesses(
        AutoRestartRule rule,
        IReadOnlyDictionary<int, ObservedProcessMatch> allProcesses)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.TargetKind != AutoRestartTargetKind.Process)
        {
            return [];
        }

        var targetName = AutoRestartRule.NormalizeProcessName(rule.ProcessName);
        if (string.IsNullOrWhiteSpace(targetName) || AutoRestartElevatedPolicy.IsCriticalDeniedProcess(targetName))
        {
            return [];
        }

        var filter = string.IsNullOrWhiteSpace(rule.CommandLineContains) ? null : rule.CommandLineContains.Trim();
        var currentPid = Environment.ProcessId;
        var matched = new List<ObservedProcessMatch>();
        foreach (var proc in allProcesses.Values)
        {
            if (proc.ProcessId <= 4 || proc.ProcessId == currentPid)
            {
                continue;
            }

            if (!string.Equals(proc.ProcessName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter is not null)
            {
                var cmd = NativeMethods.TryGetProcessCommandLine(proc.ProcessId);
                if (string.IsNullOrEmpty(cmd) || !cmd.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            matched.Add(proc);
        }

        return matched;
    }

    public async Task<AutoRestartRunResult> ExecuteProcessRuleAsync(
        ProjectPaths paths,
        AutoRestartRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(rule);
        var normalized = rule.Normalize();
        AutoRestartSettingsValidator.ValidateRule(normalized);

        if (normalized.TargetKind != AutoRestartTargetKind.Process)
        {
            throw new ArgumentException("ExecuteProcessRuleAsync only handles Process rules.", nameof(rule));
        }

        var now = DateTimeOffset.UtcNow;
        var isElevated = IsCurrentProcessElevated();
        var allProcesses = ReadAllProcessMetrics();
        var matches = MatchProcesses(normalized, allProcesses);

        string? relaunchPath = normalized.ExecutablePath;
        if (normalized.ActionMode == AutoRestartActionMode.TerminateAndRelaunch && string.IsNullOrWhiteSpace(relaunchPath))
        {
            foreach (var match in matches)
            {
                var path = NativeMethods.TryGetProcessImagePath(match.ProcessId);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    relaunchPath = path;
                    break;
                }
            }
        }

        if (normalized.RequireElevation && !isElevated)
        {
            var elevatedResult = await ExecuteViaElevatedChannelAsync(paths, normalized, cancellationToken).ConfigureAwait(false);
            if (!elevatedResult.Succeeded || normalized.ActionMode != AutoRestartActionMode.TerminateAndRelaunch)
            {
                return elevatedResult;
            }

            if (string.IsNullOrWhiteSpace(relaunchPath) || !File.Exists(relaunchPath))
            {
                return new AutoRestartRunResult(
                    normalized.Id,
                    false,
                    $"已通过提权通道结束 {elevatedResult.AffectedProcessCount} 个进程，但无法确定重启程序路径。",
                    elevatedResult.AffectedProcessCount,
                    true,
                    DateTimeOffset.UtcNow);
            }

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            LaunchProcess(relaunchPath, normalized.Arguments, normalized.WorkingDirectory);
            return new AutoRestartRunResult(
                normalized.Id,
                true,
                $"已通过提权通道结束 {elevatedResult.AffectedProcessCount} 个旧进程并重新启动 {Path.GetFileName(relaunchPath)}。",
                elevatedResult.AffectedProcessCount,
                true,
                DateTimeOffset.UtcNow);
        }

        if (matches.Count == 0)
        {
            if (normalized.ActionMode == AutoRestartActionMode.TerminateOnly)
            {
                return new AutoRestartRunResult(normalized.Id, true, "未发现匹配的运行中进程，无需操作。", 0, isElevated, now);
            }

            if (string.IsNullOrWhiteSpace(relaunchPath) || !File.Exists(relaunchPath))
            {
                return new AutoRestartRunResult(normalized.Id, false, "未发现运行中的进程，且未配置有效的重启程序路径。", 0, isElevated, now);
            }

            LaunchProcess(relaunchPath, normalized.Arguments, normalized.WorkingDirectory);
            return new AutoRestartRunResult(normalized.Id, true, $"进程未运行，已直接启动：{Path.GetFileName(relaunchPath)}", 1, isElevated, DateTimeOffset.UtcNow);
        }

        var gracefulFirst = normalized.ActionMode == AutoRestartActionMode.TerminateAndRelaunch;
        var (terminated, accessDenied, lastError) = await TerminateMatchesAsync(
            matches,
            normalized.ProcessName!,
            normalized.CommandLineContains,
            gracefulFirst,
            cancellationToken).ConfigureAwait(false);

        if (accessDenied && terminated == 0)
        {
            var hint = "权限不足（拒绝访问）。可在规则设置中勾选「通过提权维护通道结束进程」，或以管理员身份运行 Vigil。";
            return new AutoRestartRunResult(normalized.Id, false, hint, 0, isElevated, DateTimeOffset.UtcNow);
        }

        if (terminated == 0 && !string.IsNullOrWhiteSpace(lastError))
        {
            return new AutoRestartRunResult(normalized.Id, false, $"结束进程失败：{lastError}", 0, isElevated, DateTimeOffset.UtcNow);
        }

        if (normalized.ActionMode == AutoRestartActionMode.TerminateOnly)
        {
            return new AutoRestartRunResult(
                normalized.Id,
                true,
                $"已结束 {terminated} 个 {normalized.ProcessName} 进程。",
                terminated,
                isElevated,
                DateTimeOffset.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(relaunchPath) || !File.Exists(relaunchPath))
        {
            return new AutoRestartRunResult(
                normalized.Id,
                false,
                $"已结束 {terminated} 个进程，但无法确定重启程序路径。",
                terminated,
                isElevated,
                DateTimeOffset.UtcNow);
        }

        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        LaunchProcess(relaunchPath, normalized.Arguments, normalized.WorkingDirectory);
        return new AutoRestartRunResult(
            normalized.Id,
            true,
            $"已重启 {Path.GetFileName(relaunchPath)}（结束了 {terminated} 个旧进程）。",
            terminated,
            isElevated,
            DateTimeOffset.UtcNow);
    }

    private async Task<AutoRestartRunResult> ExecuteViaElevatedChannelAsync(
        ProjectPaths paths,
        AutoRestartRule rule,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (AutoRestartElevatedPolicy.IsCriticalDeniedProcess(rule.ProcessName))
        {
            return new AutoRestartRunResult(rule.Id, false, "不能通过提权通道结束 Vigil 自身或内核保留进程。", 0, false, now);
        }

        var status = _scheduler.GetStatus(paths.RootDirectory);
        if (!status.Installed || !status.MatchesRoot)
        {
            var detail = !status.Installed
                ? "系统维护通道尚未启用；请在「自动重启规则」窗口中点击「启用提权通道…」。"
                : "系统维护通道指向了其他工作区；请重新点击「启用提权通道…」绑定当前工作区。";
            return new AutoRestartRunResult(rule.Id, false, detail, 0, false, now);
        }

        var stateDir = StateDirectoryForCurrentUser(paths);
        Directory.CreateDirectory(stateDir);
        var requestPath = Path.Combine(stateDir, RequestFileName);
        var resultPath = Path.Combine(stateDir, ResultFileName);
        var requestId = Guid.NewGuid().ToString("N");
        var request = new ElevatedMaintenanceRequest(
            requestId,
            rule.ProcessName!,
            rule.CommandLineContains,
            DateTimeOffset.UtcNow);

        await WriteJsonAtomicAsync(requestPath, request, cancellationToken).ConfigureAwait(false);
        try
        {
            _scheduler.Trigger();
        }
        catch (Exception exception)
        {
            TryDelete(requestPath);
            return new AutoRestartRunResult(
                rule.Id,
                false,
                $"触发系统维护通道失败：{exception.Message}",
                0,
                true,
                DateTimeOffset.UtcNow);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TryReadJsonAsync<ElevatedMaintenanceResult>(resultPath, cancellationToken).ConfigureAwait(false);
            if (result is not null && string.Equals(result.RequestId, requestId, StringComparison.Ordinal))
            {
                TryDelete(requestPath);
                return new AutoRestartRunResult(
                    rule.Id,
                    result.Succeeded,
                    result.Message,
                    result.TerminatedCount,
                    true,
                    result.CompletedAtUtc);
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        TryDelete(requestPath);
        return new AutoRestartRunResult(
            rule.Id,
            false,
            "等待系统维护通道回执超时（15 秒）；请检查计划任务 \\Vigil\\Maintenance 状态。",
            0,
            true,
            DateTimeOffset.UtcNow);
    }

    public async Task<int> ExecuteElevatedRequestAsync(
        ProjectPaths paths,
        string callerSid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(callerSid))
        {
            throw new ArgumentException("Caller SID is required.", nameof(callerSid));
        }

        var stateDir = StateDirectoryForSid(paths, callerSid);
        var requestPath = Path.Combine(stateDir, RequestFileName);
        var resultPath = Path.Combine(stateDir, ResultFileName);

        var request = await TryReadJsonAsync<ElevatedMaintenanceRequest>(requestPath, cancellationToken).ConfigureAwait(false);
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return 0;
        }

        ElevatedMaintenanceResult result;
        try
        {
            var age = DateTimeOffset.UtcNow - request.RequestedAtUtc;
            if (age > RequestFreshnessWindow || age < TimeSpan.FromMinutes(-5))
            {
                result = new ElevatedMaintenanceResult(request.RequestId, false, "维护请求已过期，已拒绝执行。", 0, DateTimeOffset.UtcNow);
            }
            else if (AutoRestartElevatedPolicy.IsCriticalDeniedProcess(request.ProcessName))
            {
                result = new ElevatedMaintenanceResult(request.RequestId, false, $"目标 '{request.ProcessName}' 为 Vigil 自身或内核保留进程，已拒绝执行。", 0, DateTimeOffset.UtcNow);
            }
            else
            {
                var syntheticRule = new AutoRestartRule
                {
                    Id = "elevated-request",
                    Name = request.ProcessName,
                    TargetKind = AutoRestartTargetKind.Process,
                    ProcessName = request.ProcessName,
                    CommandLineContains = request.CommandLineContains,
                    ActionMode = AutoRestartActionMode.TerminateOnly,
                    RequireElevation = true,
                }.Normalize();
                var allProcesses = ReadAllProcessMetrics();
                var matches = MatchProcesses(syntheticRule, allProcesses);
                if (matches.Count == 0)
                {
                    result = new ElevatedMaintenanceResult(request.RequestId, true, $"未发现运行中的 {syntheticRule.ProcessName} 进程，无需操作。", 0, DateTimeOffset.UtcNow);
                }
                else
                {
                    var (terminated, _, lastError) = await TerminateMatchesAsync(
                        matches,
                        syntheticRule.ProcessName!,
                        syntheticRule.CommandLineContains,
                        gracefulFirst: false,
                        cancellationToken).ConfigureAwait(false);
                    result = terminated > 0
                        ? new ElevatedMaintenanceResult(request.RequestId, true, $"已通过系统维护通道热重启 {terminated} 个 {syntheticRule.ProcessName} 进程。", terminated, DateTimeOffset.UtcNow)
                        : new ElevatedMaintenanceResult(request.RequestId, false, $"未能结束 {syntheticRule.ProcessName}：{lastError ?? "未知错误"}", 0, DateTimeOffset.UtcNow);
                }
            }
        }
        catch (Exception exception)
        {
            result = new ElevatedMaintenanceResult(request.RequestId, false, $"提权维护执行异常：{exception.Message}", 0, DateTimeOffset.UtcNow);
        }

        await WriteJsonAtomicAsync(resultPath, result, cancellationToken).ConfigureAwait(false);
        TryDelete(requestPath);
        return result.Succeeded ? 0 : 1;
    }

    private static async Task<(int Terminated, bool AccessDenied, string? LastError)> TerminateMatchesAsync(
        IReadOnlyList<ObservedProcessMatch> matches,
        string expectedProcessName,
        string? commandLineContains,
        bool gracefulFirst,
        CancellationToken cancellationToken)
    {
        var terminated = 0;
        var accessDenied = false;
        string? lastError = null;
        var normalizedExpected = AutoRestartRule.NormalizeProcessName(expectedProcessName) ?? expectedProcessName;
        var filter = string.IsNullOrWhiteSpace(commandLineContains) ? null : commandLineContains.Trim();

        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(match.ProcessId);
                if (process.HasExited)
                {
                    continue;
                }

                // Re-verify process name & command line right before termination to guard against PID reuse.
                if (!string.Equals(process.ProcessName, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (filter is not null)
                {
                    var cmd = NativeMethods.TryGetProcessCommandLine(match.ProcessId);
                    if (string.IsNullOrEmpty(cmd) || !cmd.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (gracefulFirst)
                {
                    try
                    {
                        if (process.CloseMainWindow())
                        {
                            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            graceCts.CancelAfter(TimeSpan.FromSeconds(2.5));
                            try { await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    exitCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }

                terminated++;
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
            {
                accessDenied = true;
                lastError = exception.Message;
            }
            catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
            {
                accessDenied = true;
                lastError = exception.Message;
            }
        }

        return (terminated, accessDenied, lastError);
    }

    private static void LaunchProcess(string executablePath, string? arguments, string? workingDirectory)
    {
        var resolvedWorkingDir = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = resolvedWorkingDir,
            UseShellExecute = true,
        };
        _ = Process.Start(startInfo);
    }

    private static string StateDirectoryForCurrentUser(ProjectPaths paths) =>
        Path.Combine(paths.RootDirectory, ".autorun-state", CurrentUserIdentity.Key);

    private static string StateDirectoryForSid(ProjectPaths paths, string sid) =>
        Path.Combine(paths.RootDirectory, ".autorun-state", sid.Trim().Replace('-', '_'));

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            SnapshotFile.Publish(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task<T?> TryReadJsonAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using var stream = SnapshotFile.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
