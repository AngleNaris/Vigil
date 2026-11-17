using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

// Display/cache state is never a native authorization. All current observations and
// configuration-only adoption use the same Agent contracts as the Console.
public partial class StartupManagerViewModel : ObservableObject, IDisposable
{
    private readonly AgentStartupClient _client;
    private readonly StartupCacheStore _cache;
    private readonly CancellationTokenSource _lifetime = new();
    private StartupInventorySnapshot? _inventory;
    private string? _revision;
    private long _selectionVersion;
    private bool _disposed;
    private StartupNativeInspectionSnapshot? _native;
    private string? _unknownOperationId; // Immutable latch; PendingOperationId is display-only.
    private long _receiptQueryVersion;

    [ObservableProperty] private StartupEntryViewModel? selectedEntry;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private FilterOption selectedFilter;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isCached;
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private bool showDetails;
    [ObservableProperty] private bool hasNotice;
    [ObservableProperty] private string statusMessage = "读取前状态未知。";
    [ObservableProperty] private string countMessage = "--";
    [ObservableProperty] private string observationLabel = "未读取";
    [ObservableProperty] private string emptyStateTitle = "启动项状态未知";
    [ObservableProperty] private string detailTitle = "元数据复核";
    [ObservableProperty] private string detailText = string.Empty;
    [ObservableProperty] private StartupInspectionSnapshot? inspection;
    [ObservableProperty] private string? pendingOperationId;

    internal StartupManagerViewModel(AgentStartupClient client, StartupCacheStore cache)
    {
        _client = client; _cache = cache;
        Filters = [new("全部", ""), new("已登记", "active"), new("已禁用", "disabled"), new("本配置纳管", "managed")];
        selectedFilter = Filters[0];
        EntriesView = CollectionViewSource.GetDefaultView(Entries); EntriesView.Filter = FilterEntry;
    }

    public ObservableCollection<StartupEntryViewModel> Entries { get; } = [];
    public ICollectionView EntriesView { get; }
    public IReadOnlyList<FilterOption> Filters { get; }
    public bool HasSelection => SelectedEntry is not null;
    public bool ConfigurationChanged { get; private set; }
    public string NativeWriteExplanation => "原生写入门禁尚未开放；禁用、原备份恢复与独立重建均不可提交。";
    public event EventHandler<StartupEntry>? AddProgramRequested;

    internal async Task InitializeAsync()
    {
        try
        {
            var cached = await _cache.LoadAsync(_lifetime.Token);
            if (_disposed) { return; }
            if (cached is not null)
            {
                IsCached = true; HasNotice = true; ObservationLabel = "历史缓存 · 未核验";
                ReplaceEntries(cached.Entries); StatusMessage = "缓存只供查看，正在读取当前后台。";
            }
        }
        catch (OperationCanceledException) when (_disposed) { return; }
        catch (Exception exception) { StatusMessage = $"缓存不可读：{exception.Message}"; }
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunGlobalCommand))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync("正在读取后台启动项…", async () =>
        {
            InvalidateReview(); _revision = null;
            var snapshot = await _client.ReadAsync(new(Limit: 200), _lifetime.Token);
            if (_disposed) { return; }
            _inventory = snapshot;
            if (!snapshot.Available)
            {
                MarkUnavailable(snapshot.Reason ?? "startup_unavailable"); return;
            }
            _revision = snapshot.ConfigurationRevision; IsCached = false;
            HasNotice = snapshot.HasEntryErrors || snapshot.SourceIssues.Count > 0 || snapshot.Truncated;
            ObservationLabel = HasNotice ? "部分观测" : "当前观测";
            ReplaceEntries(snapshot.Entries);
            StatusMessage = snapshot.Truncated ? "读取有界清单，结果已截断；搜索仅覆盖当前已读项目。"
                : snapshot.SourceIssues.Count > 0 ? "部分来源不可读，不能视为完整清单；可打开诊断查看。"
                : snapshot.HasEntryErrors ? "部分条目不可读，保留名称与来源；不可纳管或原生修改。"
                : "已登记不代表正在运行；备份记录不代表可恢复。";
            // Cache only display metadata, never a revision, inspection or native evidence.
            var cache = new StartupManagementSnapshot(snapshot.Entries, snapshot.Entries.Count(item => !item.IsDisabled),
                snapshot.Entries.Count(item => !item.IsDisabled && item.IsManaged), snapshot.Entries.Count(item => item.IsDisabled), snapshot.IsElevated)
            { Truncated = snapshot.Truncated, ReadableSources = snapshot.ReadableSources, SourceIssues = snapshot.SourceIssues };
            try { await _cache.SaveAsync(cache, _lifetime.Token); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { if (!_disposed) { StatusMessage += " 当前结果未能写入缓存。"; } }
            if (HasUnknownAdoption)
            { HasNotice = true; ObservationLabel += " · 纳管未知"; StatusMessage = UnknownAdoptionNotice; }
        }, unavailableOnFailure: true);
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectAsync()
    {
        var entry = SelectedEntry?.Entry; var revision = _revision; var version = _selectionVersion;
        if (entry is null || revision is null || !CanInspect()) { return; }
        await RunBusyAsync("正在复核所选元数据…", async () =>
        {
            var result = await _client.InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision, _lifetime.Token);
            if (!MatchesSelection(entry, revision, version))
            { if (!_disposed) { StatusMessage = "选择或清单已变化，旧复核不再显示；请按需重新复核。"; } return; }
            Inspection = result; _native = null; DetailTitle = "元数据复核";
            DetailText = MetadataText(result.Entry); ShowDetails = true;
            StatusMessage = "元数据已复核；原备份/目标可用性未检查，原生写仍关闭。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanInspectNative))]
    private async Task InspectNativeAsync()
    {
        var entry = SelectedEntry?.Entry; var revision = _revision; var version = _selectionVersion;
        if (entry is null || revision is null || !CanInspectNative()) { return; }
        await RunBusyAsync("正在只读观察两个原生槽…", async () =>
        {
            var result = await _client.InspectNativeAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision, _lifetime.Token);
            if (!MatchesSelection(entry, revision, version))
            { if (!_disposed) { StatusMessage = "选择或清单已变化，旧复核不再显示；请按需重新复核。"; } return; }
            _native = result; Inspection = result.Metadata; DetailTitle = "原生槽 · 只读观察"; ShowDetails = true;
            var archive = result.Slots.Archive; var original = result.Slots.Original;
            DetailText = $"归档槽：{archive.State} · {archive.Reason ?? "--"}\n{archive.ObservedAt:O}\n\n原入口槽：{original.State} · {original.Reason ?? "--"}\n{original.ObservedAt:O}\n\n两次独立观察，不是快照/CAS。历史身份未验证。\npresent 只证明当前固定名下有界普通对象；双 missing 也不授权恢复/重建。";
            StatusMessage = archive.State == "unknown" || original.State == "unknown"
                ? "原生观察不完整：unknown 不是缺失，另一槽结果独立保留。"
                : "只读观察完成，仍无原备份真实性、恢复/重建资格或写授权。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanAddToTasks))]
    private async Task AddToTasksAsync()
    {
        // The plus action prepares through the shared read-only contract, never directly
        // invokes the legacy task-add path with unreviewed startup metadata.
        if (!CanAddToTasks()) { return; }
        await InspectAsync();
        if (CanAddToTasks() && Inspection is { } prepared && SameEntry(prepared.Entry, SelectedEntry!.Entry)
            && prepared.Actions.Any(action => action.Action == "adopt" && action.State == "reviewable"))
        { AddProgramRequested?.Invoke(this, prepared.Entry); }
    }

    internal async Task<bool> AdoptAsync(AddTaskRequest task, bool confirmed)
    {
        var prepared = Inspection; var entry = SelectedEntry?.Entry; var revision = _revision;
        if (!confirmed || prepared is null || entry is null || revision is null || !CanAddToTasks()
            || prepared.ConfigurationRevision != revision || !SameEntry(prepared.Entry, entry)) { return false; }
        var succeeded = false;
        await RunBusyAsync("正在提交已确认的纳管配置…", async () =>
        {
            _ = await _client.AdoptAsync(entry.Id, new(prepared.Entry, task, Confirmed: true), revision, cancellationToken: _lifetime.Token);
            ConfigurationChanged = true; succeeded = true; InvalidateReview(); _revision = null;
            StatusMessage = "纳管配置已提交；没有禁用原启动登记或请求执行。";
        });
        if (succeeded && !_disposed)
        {
            await RefreshAsync();
            StatusMessage = IsCached || _revision is null
                ? "纳管配置已提交；清单刷新未成功。没有禁用原登记或请求执行，请勿因显示延迟重复纳管。"
                : "纳管配置已提交；没有禁用原启动登记或请求执行。";
        }
        return succeeded;
    }

    // Even callers that bypass ICommand.CanExecute cannot reach an old native path.
    [RelayCommand(CanExecute = nameof(NativeWriteUnavailable))]
    private void Disable() => StatusMessage = NativeWriteExplanation;
    [RelayCommand(CanExecute = nameof(NativeWriteUnavailable))]
    private void Restore() => StatusMessage = NativeWriteExplanation;
    private static bool NativeWriteUnavailable() => false;

    [RelayCommand(CanExecute = nameof(CanQueryPendingOperation))]
    private async Task QueryPendingOperationAsync()
    {
        if (!CanQueryPendingOperation()) { return; }
        var operationId = _unknownOperationId!;
        var version = ++_receiptQueryVersion;
        await RunBusyAsync("正在只读查询原纳管操作…", async () =>
        {
            DetailTitle = "原操作回执 · 只读"; DetailText = $"正在查询原操作 {operationId}…"; ShowDetails = true;
            try
            {
                var receipt = await _client.ReadAdoptionOperationAsync(operationId, _lifetime.Token);
                if (_disposed || version != _receiptQueryVersion) { return; }
                DetailText = $"原操作 ID：{operationId}\n仅日志观察，不确认业务生效；查询不重提，本窗口仍禁止纳管。\n\n"
                    + (receipt is null ? "未找到保留回执。可能从未存在或已不可得，不代表原操作未执行。"
                        : "日志状态：" + (receipt.State switch
                        {
                            OperationState.Running => "running（仍在执行）",
                            OperationState.Completed => "completed（已记回执，不等于业务成功）",
                            _ => "unknown（结果未知）",
                        }) + (receipt.ResultOmitted ? "\n详细正文未保留；不得据此重提。" : "")
                            + "\n\n" + JsonSerializer.Serialize(receipt, new JsonSerializerOptions(AgentProtocol.JsonOptions) { WriteIndented = true }));
            }
            catch (Exception exception)
            {
                if (_disposed || version != _receiptQueryVersion) { return; }
                // A failed READ must never re-latch an error's ID or erase the original mutation ID.
                DetailText = $"原操作 ID：{operationId}\n回执查询失败：{AgentErrorMessages.Format(exception)}\n查询失败不代表原操作失败；本窗口仍禁止纳管，不会重提。";
            }
        });
    }
    private bool CanQueryPendingOperation() => CanRunGlobalCommand() && HasUnknownAdoption && _unknownOperationId is not null;

    [RelayCommand]
    private void CloseDetails() { _receiptQueryVersion++; ShowDetails = false; }
    [RelayCommand]
    private void ShowDiagnostics()
    {
        _receiptQueryVersion++;
        DetailTitle = "诊断 · 原始元数据"; ShowDetails = true;
        DetailText = JsonSerializer.Serialize(new { state = ObservationLabel, cached = IsCached,
            inventory = _inventory, selected = SelectedEntry?.Entry, inspection = Inspection, native = _native,
            pendingOperationId = _unknownOperationId }, new JsonSerializerOptions(AgentProtocol.JsonOptions) { WriteIndented = true });
    }

    partial void OnSelectedEntryChanged(StartupEntryViewModel? value)
    { _ = value; _selectionVersion++; InvalidateReview(); OnPropertyChanged(nameof(HasSelection)); NotifyCommands(); }
    partial void OnIsBusyChanged(bool value) { _ = value; NotifyCommands(); }
    partial void OnIsCachedChanged(bool value) { _ = value; NotifyCommands(); }
    partial void OnPendingOperationIdChanged(string? value) { _ = value; NotifyCommands(); }
    partial void OnSearchTextChanged(string value) { _ = value; RefreshFilter(); }
    partial void OnSelectedFilterChanged(FilterOption value) { _ = value; RefreshFilter(); }
    private bool CanRunGlobalCommand() => !IsBusy && !_disposed;
    private bool CanInspect() => CanRunGlobalCommand() && !IsCached && _revision is not null && SelectedEntry is not null;
    private bool CanInspectNative() => CanInspect() && SelectedEntry is { Entry.IsDisabled: true, Entry.Source: "Startup" or "Common Startup" };
    private bool CanAddToTasks() => CanInspect() && !HasUnknownAdoption && PendingOperationId is null && SelectedEntry is { } selected
        && StartupInspectionPolicy.Describe(selected.Entry).Any(action => action.Action == "adopt" && action.State == "reviewable");
    private bool MatchesSelection(StartupEntry entry, string revision, long version) => !_disposed && _revision == revision
        && version == _selectionVersion && SelectedEntry is not null && SameEntry(entry, SelectedEntry.Entry);
    private static bool SameEntry(StartupEntry left, StartupEntry right) => left.Id == right.Id && left.MetadataDigest == right.MetadataDigest;
    private void InvalidateReview() { _receiptQueryVersion++; Inspection = null; _native = null; ShowDetails = false; DetailText = ""; }

    private async Task RunBusyAsync(string message, Func<Task> action, bool unavailableOnFailure = false)
    {
        if (IsBusy || _disposed) { return; }
        IsBusy = true; StatusMessage = message;
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (AgentOutcomeUnknownException exception) { MarkAdoptionUnknown(exception.OperationId); }
        catch (AgentCommandException exception) when (exception.Code == "outcome_unknown")
        { MarkAdoptionUnknown(exception.OperationId); }
        catch (Exception exception)
        {
            InvalidateReview();
            if (unavailableOnFailure) { MarkUnavailable(AgentErrorMessages.Format(exception)); }
            else { StatusMessage = $"复核/提交失败：{AgentErrorMessages.Format(exception)}"; }
        }
        finally { IsBusy = false; if (HasUnknownAdoption) { StatusMessage = UnknownAdoptionNotice; } }
    }

    // An accepted business-level unknown is as non-retriable as a lost transport reply.
    // This is a window-lifetime fail-closed gate, not persistent GUI receipt recovery.
    public bool HasUnknownAdoption { get; private set; }
    private void MarkAdoptionUnknown(string? operationId)
    {
        if (HasUnknownAdoption) { return; }
        _unknownOperationId = operationId;
        HasUnknownAdoption = true; OnPropertyChanged(nameof(HasUnknownAdoption));
        PendingOperationId = operationId; _revision = null; InvalidateReview();
        IsCached = Entries.Count > 0; HasNotice = true; ObservationLabel = "纳管结果未知";
        StatusMessage = UnknownAdoptionNotice; NotifyCommands();
    }
    private string UnknownAdoptionNotice => _unknownOperationId is { } operationId
        ? $"提交结果未知，本窗口禁止再次提交。原操作 {operationId}；更多→查询原纳管操作回执（只读），或同 root CLI：operation show {operationId}。查询和刷新均不解除封锁。"
        : "提交结果未知且缺少原操作 ID，本窗口禁止再次提交。请人工核对配置与操作记录，不要换 ID 重试。";

    private void MarkUnavailable(string reason)
    {
        _revision = null; IsCached = Entries.Count > 0; HasNotice = true;
        ObservationLabel = IsCached ? "历史缓存 · 当前读取失败" : "当前不可用";
        StatusMessage = $"当前启动项不可用：{reason}。未读取不等于没有启动项。"; UpdateCountAndEmpty(); NotifyCommands();
    }
    private void ReplaceEntries(IReadOnlyList<StartupEntry> entries)
    {
        var selectedId = SelectedEntry?.Id;
        Entries.Clear();
        foreach (var entry in entries.OrderBy(item => item.IsDisabled).ThenBy(item => item.Source).ThenBy(item => item.Name))
        { Entries.Add(new(entry)); }
        EntriesView.Refresh(); SelectedEntry = Entries.FirstOrDefault(item => item.Id == selectedId && EntriesView.Contains(item));
        UpdateCountAndEmpty(); NotifyCommands();
    }
    private bool FilterEntry(object item) => item is StartupEntryViewModel entry
        && (SelectedFilter.Value switch { "active" => !entry.Entry.IsDisabled, "disabled" => entry.Entry.IsDisabled, "managed" => entry.Entry.IsManaged, _ => true })
        && (string.IsNullOrWhiteSpace(SearchText) || new[] { entry.Name, entry.Source, entry.Target, entry.Arguments, entry.Origin, entry.Entry.ManagedTask }
            .Any(text => text?.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) == true));
    private void RefreshFilter()
    {
        EntriesView.Refresh(); if (SelectedEntry is not null && !EntriesView.Contains(SelectedEntry)) { SelectedEntry = null; }
        UpdateCountAndEmpty();
    }
    private void UpdateCountAndEmpty()
    {
        IsEmpty = EntriesView.IsEmpty;
        CountMessage = $"{EntriesView.Cast<object>().Count()} / {Entries.Count}" + (_inventory?.Truncated == true ? " · 截断" : "");
        EmptyStateTitle = Entries.Count > 0 ? "没有匹配结果" : _inventory is { Available: true, SourceIssues.Count: 0, Truncated: false }
            && !IsCached ? "当前已读清单为空" : "启动项状态未知";
    }
    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); InspectCommand.NotifyCanExecuteChanged(); InspectNativeCommand.NotifyCanExecuteChanged();
        AddToTasksCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged();
        QueryPendingOperationCommand.NotifyCanExecuteChanged();
    }
    private static string MetadataText(StartupEntry entry) =>
        $"目标（仅元数据）\n{entry.Target ?? "未可靠解析"}\n\n参数\n{entry.Arguments ?? "--"}\n\n工作目录\n{entry.WorkingDirectory ?? "--"}\n\n登记位置（不执行）\n{entry.Origin}\n\n原始命令\n{entry.RawCommand ?? "--"}\n\n{entry.Error ?? "复核不授原生写权限；原备份和目标可用性均未检查。"}";
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        InvalidateReview(); NotifyCommands();
    }
}
