using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

/// <summary>A single-source, bounded reader. Closing it never controls the daemon or workload.</summary>
public sealed class LogViewerViewModel : ObservableObject, IAsyncDisposable
{
    public const string GlobalSources = "全局来源";
    private readonly AgentClient _client;
    private readonly ProjectPaths _paths;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Task> _reads = [];
    private IReadOnlyList<LogSourceInfo> _allSources = [];
    private CancellationTokenSource? _activeRead;
    private CancellationTokenSource? _follow;
    private Task _followTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _hasMore;
    private long _generation;
    private bool _disposed, _loadingSources, _isBusy, _isFollowing;
    private string _selectedTask, _sinceText = "", _statusMessage = "正在读取来源…";
    private LogSourceChoice? _selectedSource;
    private string _selectedLevel = "全部级别";
    private int _pageSize = 100;
    private LogPage? _page;
    private AgentResponse? _response;
    private string _sourceNotice = "";
    private string? _cursor;

    internal LogViewerViewModel(AgentClient client, ProjectPaths paths, string? taskName = null, TimeSpan? pollInterval = null)
    {
        _client = client; _paths = paths; _selectedTask = taskName ?? GlobalSources;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        TaskNames.Add(GlobalSources);
        if (taskName is not null) { TaskNames.Add(taskName); }
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(reloadSources: true));
        RecentCommand = new AsyncRelayCommand(() => LoadAsync());
        FromStartCommand = new AsyncRelayCommand(() => LoadAsync(fromStart: true));
        NextCommand = new AsyncRelayCommand(() => LoadAsync(cursor: _cursor), () => !IsBusy && !IsFollowing && _hasMore && Page is { Available: true });
        FollowCommand = new AsyncRelayCommand(ToggleFollowAsync, () => IsFollowing || !IsBusy && Page is { Available: true, NextCursor: not null });
    }

    public ObservableCollection<string> TaskNames { get; } = [];
    public ObservableCollection<LogSourceChoice> Sources { get; } = [];
    public ObservableCollection<LogEntryRowViewModel> Entries { get; } = [];
    public IReadOnlyList<string> Levels { get; } = ["全部级别", "trace", "debug", "info", "warn", "error", "critical"];
    public IReadOnlyList<int> PageSizes { get; } = [50, 100, 200];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand RecentCommand { get; }
    public IAsyncRelayCommand FromStartCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand FollowCommand { get; }
    public string SelectedTask
    {
        get => _selectedTask;
        set { if (value is not null && SetProperty(ref _selectedTask, value)) { Invalidate(); SelectSources(); } }
    }
    public LogSourceChoice? SelectedSource
    {
        get => _selectedSource;
        set { if (SetProperty(ref _selectedSource, value)) { Invalidate(); } }
    }
    public string SelectedLevel { get => _selectedLevel; set { if (SetProperty(ref _selectedLevel, value)) { Invalidate(); } } }
    public string SinceText { get => _sinceText; set { if (SetProperty(ref _sinceText, value)) { Invalidate(); } } }
    public int PageSize
    {
        get => _pageSize;
        set { if (PageSizes.Contains(value) && SetProperty(ref _pageSize, value)) { Invalidate(); } }
    }
    public LogPage? Page { get => _page; private set { SetProperty(ref _page, value); NotifyCommands(); OnPropertyChanged(nameof(CanExport)); } }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); NotifyCommands(); OnPropertyChanged(nameof(CanExport)); } }
    public bool IsFollowing { get => _isFollowing; private set { SetProperty(ref _isFollowing, value); NotifyCommands(); OnPropertyChanged(nameof(FollowLabel)); OnPropertyChanged(nameof(FollowIcon)); OnPropertyChanged(nameof(CanExport)); } }
    public string FollowLabel => IsFollowing ? "正在追踪" : "追踪已暂停";
    public string FollowIcon => IsFollowing ? "Pause" : "Play";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool CanExport => !_disposed && !IsBusy && !IsFollowing && Page is { Available: true } && _response is not null;
    internal Task FollowTask => _followTask;

    private void NotifyCommands()
    {
        NextCommand?.NotifyCanExecuteChanged(); FollowCommand?.NotifyCanExecuteChanged();
    }
    private void Invalidate()
    {
        if (_loadingSources) { return; }
        ++_generation; _activeRead?.Cancel(); _follow?.Cancel(); IsFollowing = false; IsBusy = false;
        _hasMore = false; Page = null; _response = null; _cursor = null; Entries.Clear();
        if (!_loadingSources) { StatusMessage = "条件已更改 · 刷新读取最近页，或选择从头"; }
    }
    private void SelectSources()
    {
        var previous = SelectedSource?.Source.Id;
        Sources.Clear();
        foreach (var source in _allSources.Where(source => SelectedTask == GlobalSources || source.TaskName == SelectedTask))
        { Sources.Add(new(source)); }
        SelectedSource = Sources.FirstOrDefault(item => item.Source.Id == previous)
            ?? Sources.FirstOrDefault(item => item.Source.Origin == "control") ?? Sources.FirstOrDefault();
    }

    public Task InitializeAsync() => LoadAsync(reloadSources: true);
    public Task RefreshAsync() => LoadAsync();

    private Task LoadAsync(bool reloadSources = false, bool fromStart = false, string? cursor = null, CancellationToken token = default)
    {
        if (_disposed) { return Task.CompletedTask; }
        // Explicit reads pause follow. The poll path passes its own token and remains single-flight.
        if (!token.CanBeCanceled) { _follow?.Cancel(); IsFollowing = false; }
        _activeRead?.Cancel();
        var generation = ++_generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        _activeRead = cancellation;
        var task = ReadCoreAsync(generation, cancellation, reloadSources, fromStart, cursor);
        _reads.Add(task); _ = RemoveReadAsync(task); return task;
    }
    private async Task RemoveReadAsync(Task task)
    { try { await task; } finally { _reads.Remove(task); } }

    private async Task ReadCoreAsync(long generation, CancellationTokenSource cancellation, bool reloadSources, bool fromStart, string? cursor)
    {
        var entered = false;
        try
        {
            IsBusy = true; StatusMessage = "正在读取日志…";
            await _gate.WaitAsync(cancellation.Token); entered = true;
            if (reloadSources)
            {
                var snapshot = await _client.SendAsync<LogSourcesSnapshot>("log-sources", timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellation.Token);
                if (generation != _generation) { return; }
                if (snapshot.SchemaVersion != 1 || snapshot.Sources is null || snapshot.Sources.Count > 256)
                { throw new JsonException("不兼容的日志来源快照。"); }
                _loadingSources = true;
                try
                {
                    _allSources = snapshot.Sources;
                    var selected = SelectedTask;
                    var names = snapshot.Sources.Select(source => source.TaskName).OfType<string>()
                        .Prepend(GlobalSources).Append(selected).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    // Keep the selected item/container in place. Reset+reselect can leave
                    // ComboBox.SelectionBoxItem blank despite a valid SelectedItem.
                    foreach (var name in names.Where(name => !TaskNames.Contains(name))) { TaskNames.Add(name); }
                    foreach (var name in TaskNames.Where(name => !names.Contains(name)).ToArray()) { TaskNames.Remove(name); }
                    // Updating choices must not invalidate the read that is installing them.
                    var oldId = _selectedSource?.Source.Id;
                    Sources.Clear();
                    foreach (var source in _allSources.Where(source => selected == GlobalSources || source.TaskName == selected)) { Sources.Add(new(source)); }
                    _selectedSource = Sources.FirstOrDefault(item => item.Source.Id == oldId)
                        ?? Sources.FirstOrDefault(item => item.Source.Origin == "control") ?? Sources.FirstOrDefault();
                    OnPropertyChanged(nameof(SelectedSource));
                    _sourceNotice = snapshot.Truncated ? $"来源列表已截断（{snapshot.Sources.Count}/{snapshot.TotalSources}） · " : "";
                }
                finally { _loadingSources = false; }
            }
            if (SelectedSource is null) { Page = null; Entries.Clear(); StatusMessage = _sourceNotice + "没有声明的日志来源"; return; }
            DateTimeOffset? since = null;
            if (!string.IsNullOrWhiteSpace(SinceText))
            {
                var text = SinceText.Trim();
                if (!(text.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$"))
                    || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                { throw new ArgumentException("起始时间须含日期和时区，例如 2026-11-03 09:00 +08:00；留空不限制。"); }
                since = parsed;
            }
            var chosenSource = SelectedSource.Source;
            var query = new LogQuery(chosenSource.Id, SelectedTask == GlobalSources ? null : SelectedTask,
                SelectedLevel == "全部级别" ? null : SelectedLevel, since, PageSize, cursor, fromStart);
            var response = await _client.SendResponseAsync("logs", payload: query, timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellation.Token, requireData: true);
            if (generation != _generation) { return; }
            if (!response.Ok) { throw new AgentCommandException(response.ErrorCode ?? "log_read_failed", response.Error ?? "日志读取失败"); }
            var page = response.Data is JsonElement data ? data.Deserialize<LogPage>(AgentProtocol.JsonOptions) : null;
            if (page is null || page.SchemaVersion != 1 || page.Source is null || page.Source.Id != chosenSource.Id
                || page.Source.TaskName != chosenSource.TaskName || page.Source.Kind != chosenSource.Kind || page.Source.Origin != chosenSource.Origin
                || page.Entries is null || page.Entries.Count > PageSize
                || page.Entries.Any(entry => entry is null || entry.Message is null || entry.FileIdentity is null || entry.Message.Length > 4096)
                || page.Available && string.IsNullOrWhiteSpace(page.NextCursor)
                || response.OperationId is not null || response.Replayed
                || JsonSerializer.SerializeToUtf8Bytes(page, AgentProtocol.JsonOptions).Length > 128 * 1024)
            { throw new JsonException("不兼容或不匹配的日志页。"); }
            _cursor = page.NextCursor; _hasMore = page.HasMore; NotifyCommands();
            // Keep the displayed page and its export receipt together. An empty poll advances
            // only the follow cursor, so pausing cannot export a different page than is visible.
            if (page.Entries.Count > 0 || !IsFollowing || !page.Available)
            {
                _response = response; Page = page;
                Entries.Clear(); foreach (var entry in page.Entries) { Entries.Add(new(entry)); }
            }
            var notices = new List<string>();
            if (!page.Available) { notices.Add("来源不可用 · " + page.Reason); }
            else if (page.Entries.Count == 0) { notices.Add(IsFollowing ? "等待新事件" : "此页没有匹配事件"); }
            else { notices.Add($"当前页 {page.Entries.Count} 条"); }
            if (page.Reason is not null && page.Available) { notices.Add(page.Reason); }
            if (page.Truncated) { notices.Add("有界读取已截断"); }
            if (page.UnknownTimestampExcluded > 0) { notices.Add($"排除未知时间 {page.UnknownTimestampExcluded} 条 · 移除时间过滤可查看"); }
            if (page.Entries.Any(entry => entry.Truncated)) { notices.Add("含截断事件"); }
            if (page.Entries.Any(entry => entry.LossyUtf8)) { notices.Add("含 UTF-8 损失解码"); }
            StatusMessage = _sourceNotice + string.Join(" · ", notices);
            if (!page.Available) { _follow?.Cancel(); IsFollowing = false; }
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation && !_disposed)
            { _follow?.Cancel(); IsFollowing = false; StatusMessage = "读取已取消或超时 · 可刷新重试"; }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or ArgumentException or AgentCommandException)
        {
            if (generation == _generation && !_disposed)
            {
                Page = null; _response = null; Entries.Clear(); _follow?.Cancel(); IsFollowing = false;
                StatusMessage = AgentErrorMessages.Format(exception);
            }
        }
        finally
        {
            if (entered) { _gate.Release(); }
            if (ReferenceEquals(_activeRead, cancellation)) { _activeRead = null; }
            cancellation.Dispose();
            if (generation == _generation) { IsBusy = false; }
        }
    }

    private async Task ToggleFollowAsync()
    {
        if (IsFollowing) { await PauseAsync(); return; }
        await _followTask;
        if (_disposed || IsBusy || Page is not { Available: true, NextCursor: not null }) { return; }
        _follow?.Dispose(); _follow = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        IsFollowing = true; _followTask = FollowCoreAsync(_follow.Token);
    }
    private async Task FollowCoreAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && IsFollowing)
            {
                await Task.Delay(_pollInterval, token);
                var cursor = _cursor;
                if (cursor is null) { break; }
                await LoadAsync(cursor: cursor, token: token);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsFollowing = false; }
    }
    public async Task PauseAsync()
    { _follow?.Cancel(); await _followTask; IsFollowing = false; }

    public async Task<bool> ExportAsync(string destination, CancellationToken token = default)
    {
        if (!CanExport || Page is not { } page || _response is not { } response) { return false; }
        try
        {
            token.ThrowIfCancellationRequested();
            var path = Core.Logging.LogSourcePolicy.ResolveFilePath(destination, Environment.CurrentDirectory);
            var protectedPaths = new[] { _paths.ConfigPath, _paths.OverridesPath, _paths.GuiSettingsPath, _paths.GuiCachePath, _paths.StartupCachePath, _paths.StartupBackupPath };
            if (protectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) { throw new ArgumentException("不能导出到配置或状态文件。"); }
            // Same ten-field, single-line NDJSON envelope as CLI logs --export.
            var envelope = new { SchemaVersion = 1, RequestId = response.Id, OperationId = response.OperationId,
                Ok = true, Data = response.Data, Error = (object?)null, response.Revision, ObservedAt = page.ObservedAt, response.RootId, response.Replayed };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, AgentProtocol.JsonOptions);
            if (bytes.Length + 1 > 128 * 1024) { throw new ArgumentException("当前页超过导出上限。"); }
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await output.WriteAsync(bytes, token); await output.WriteAsync(new byte[] { 10 }, token); await output.FlushAsync(token);
            StatusMessage = "已导出当前页 · " + path; return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { StatusMessage = "导出未完成（不覆盖已有文件） · " + exception.Message; return false; }
    }
    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _disposed = true; ++_generation; _lifetime.Cancel(); _activeRead?.Cancel(); _follow?.Cancel();
        try { await Task.WhenAll(_reads.Append(_followTask)); }
        finally
        {
            _follow?.Dispose(); _lifetime.Dispose(); _gate.Dispose();
            IsBusy = false;
        }
    }
}

public sealed record LogSourceChoice(LogSourceInfo Source)
{
    public string Label => $"{Source.Origin switch { "control" => "控制", "business" => "业务", var origin => origin }} · {Source.Id} · {Source.Kind}";
}

public sealed class LogEntryRowViewModel(LogEntry entry)
{
    public LogEntry Entry { get; } = entry;
    public string Time => Entry.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "未知时间";
    public string Level => Entry.Level ?? "未知";
    public string Summary
    {
        get
        {
            var first = Entry.Message.Split('\n', 2)[0].TrimEnd('\r');
            if (Entry.Timestamp is not null && first.StartsWith('[') && first.IndexOf(']') is var end && end >= 0)
            { first = first[(end + 1)..].TrimStart(); }
            if (Entry.Level is not null)
            { first = System.Text.RegularExpressions.Regex.Replace(first, @"^\[?(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|CRITICAL|FATAL)\]?(?:[ :]+|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant); }
            return first;
        }
    }
    public string Raw => Entry.Message;
    public string Notice => string.Join(" · ", new[] { Entry.Truncated ? "事件已截断" : null, Entry.LossyUtf8 ? "损失 UTF-8" : null }.OfType<string>());
}
