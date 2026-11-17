using System.IO;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.App.ViewModels;

// A view owns reads and unconfirmed preparation, never a claimed submission.
public sealed class RecoveryViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RecoveryWorkflow _workflow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _readToken;
    private readonly HashSet<Task> _reads = [];
    private RecoveryWorkflowSnapshot _snapshot;
    private RecoveryResumePreparation? _ownedPreparation;
    private Task? _initialization, _disposal;
    private bool _closed, _actionBusy;
    private string _feedback = "";

    internal RecoveryViewModel(ProjectPaths paths, string name)
    {
        _workflow = RecoveryWorkflow.ForTask(paths, name); _snapshot = _workflow.Current; _readToken = _lifetime.Token;
        RootDirectory = AgentProtocol.ForRoot(paths.RootDirectory).RootDirectory; TaskName = name;
        try { using var identity = WindowsIdentity.GetCurrent(); CurrentAccount = identity.Name; }
        catch (System.Security.SecurityException) { CurrentAccount = "当前 Windows 主体（名称不可用）"; }
        RefreshCommand = new AsyncRelayCommand(() => Track(ReadAsync(false)), () => CanRead);
        PrepareCommand = new AsyncRelayCommand(() => Track(ReadAsync(true)), () => CanPrepare);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        QueryCommand = new AsyncRelayCommand(() => Track(QueryAsync()), () => CanQuery);
    }
    public string RootDirectory { get; }
    public string TaskName { get; }
    public string CurrentAccount { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }
    public IAsyncRelayCommand QueryCommand { get; }
    public RecoveryWorkflowSnapshot Snapshot => _snapshot;
    public bool CanRead => !_closed && !_actionBusy && _snapshot.CanRead;
    public bool CanPrepare => !_closed && !_actionBusy && _snapshot.CanPrepare;
    public bool ShowConfirmation => _snapshot.CanConfirm && ReferenceEquals(_ownedPreparation, _snapshot.Preparation);
    public bool CanConfirm => !_closed && !_actionBusy && ShowConfirmation;
    public bool CanQuery => !_closed && !_actionBusy && _snapshot.CanQuery;
    public bool CanCopy => !_closed && _snapshot.Pending is not null && OperationId.Length != 0;
    public bool ShowRead => !ShowConfirmation && (!_snapshot.IsLocked || _snapshot is { State: RecoveryWorkflowState.StorageBlocked, Pending: null });
    public bool ShowQuery => _snapshot.IsLocked;
    public bool ShowPrepare => !ShowConfirmation && !_snapshot.IsLocked && _snapshot.Observation?.CanResume == true;
    public bool ShowOperation => OperationId.Length != 0 && (_snapshot.IsLocked || _snapshot.State is RecoveryWorkflowState.Reserving or RecoveryWorkflowState.Submitting or RecoveryWorkflowState.Finalizing);
    public bool ShowPausedAt => _snapshot.Observation?.Entry?.Paused == true;
    public bool HasFeedback => _feedback.Length != 0;
    public bool HasReceipt => ReceiptText.Length != 0;
    public string Feedback => _feedback;
    public string OperationId => _snapshot.OperationId ?? "";
    public string CloseToolTip => ShowConfirmation ? "取消未确认准备并关闭" : "关闭；已确认操作不会撤回";
    public string Heading => ShowConfirmation ? "确认解除恢复暂停" : _snapshot.State switch
    {
        RecoveryWorkflowState.OutcomeUnknown => "结果未确认",
        RecoveryWorkflowState.StorageBlocked => "恢复状态暂不可用",
        _ => "自动恢复状态"
    };
    public string Notice => ShowConfirmation ? "仅解除恢复暂停，不直接启动服务。"
        : _snapshot.State switch
        {
            RecoveryWorkflowState.Uninitialized or RecoveryWorkflowState.Loading => "正在读取原操作记录…",
            RecoveryWorkflowState.CheckingConfirmation => "正在复核当前恢复意图；关闭不撤回已确认操作。",
            RecoveryWorkflowState.Reserving or RecoveryWorkflowState.Submitting or RecoveryWorkflowState.Finalizing => "仅提交一次；关闭不代表撤销。",
            RecoveryWorkflowState.OutcomeUnknown => "保留原操作，不要重新提交。",
            RecoveryWorkflowState.StorageBlocked => "待查记录无法读取、保留或核实清理；不猜操作 ID，不重新提交。",
            RecoveryWorkflowState.ReadFailed => ReadFailureNotice,
            RecoveryWorkflowState.Completed => "请求已返回；以下状态来自独立读取，不代表服务已运行或健康。",
            RecoveryWorkflowState.NotSent => "本次未发送；需重新读取、准备和确认。",
            RecoveryWorkflowState.AwaitingConfirmation => "另一视图正在等待确认；本视图不能提交该准备。",
            _ when _snapshot.Reading => "正在读取当前恢复意图…",
            _ when _snapshot.ErrorCode == "preparation_changed" => "恢复意图或资格已变化，未提交；需重新准备和确认。",
            _ when _snapshot.Observation?.EligibilityReason == "not_found" => "当前配置中没有该服务，不提供解除暂停。",
            _ when _snapshot.Observation?.EligibilityReason == "disabled" => "服务已禁用，不提供解除暂停。",
            _ when _snapshot.Observation?.EligibilityReason == "unsupported_scope" => "仅当前用户登录任务支持解除暂停，不提权或更改资格。",
            _ => "仅管理此服务的自动恢复暂停。"
        };
    private string ReadFailureNotice => (_snapshot.ReadErrorCode ?? _snapshot.ErrorCode) switch
    {
        "protocol_mismatch" => "Agent 返回的协议或作用域不符，未提交；可以重新读取。",
        "permission_denied" => "读取被拒绝访问，未提交；不提权或修改权限。",
        "timeout" => "Agent 读取超时，当前恢复意图未知；可以重新读取。",
        "configuration_error" => "当前任务配置无法读取，未提交；本工具不修复配置。",
        "io_error" => "状态数据无法读取或损坏，未提交；本工具不重置数据。",
        "cancelled" => "读取已取消，未提交；可以重新读取。",
        _ => "读取未完成，原因尚未确认；未提交，不修复、不重置数据，可以重新读取。"
    };
    public string PolicyNotice => "不修改全局自动恢复开关，后台仍按原策略判断是否恢复。";
    public string StatusText => _snapshot.Reading ? "正在读取…"
        : _snapshot.ReadErrorCode is not null || _snapshot.Observation is null ? "当前恢复意图尚未核对"
        : _snapshot.Observation is { Initialized: false } ? "恢复状态尚未初始化"
        : _snapshot.Observation.Entry is null ? "未记录恢复暂停"
        : _snapshot.Observation.Entry.Paused ? "自动恢复已暂停" : "自动恢复未暂停";
    public string PausedAtText => _snapshot.Observation?.Entry?.PausedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") : "时间不详";
    public string ObservedAtText => _snapshot.Observation is { } observed ? $"读取于 {observed.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : "";
    public string ReceiptText => _snapshot.Querying ? "正在只读查询原操作…" : _snapshot.QueryErrorCode is not null ? "查询失败；原操作仍保留，不重新提交。" : _snapshot.Receipt?.State switch
    {
        RecoveryReceiptState.NotFound => "未找到回执；不代表未执行，仍保留原操作。",
        RecoveryReceiptState.Running => "回执为 Running；仍保留原操作。",
        RecoveryReceiptState.Unknown => "回执为 Unknown；仍保留原操作。",
        RecoveryReceiptState.ResultUnavailable => "结果未保留；不能据此重新提交。",
        RecoveryReceiptState.CompletedHistory => "回执为 Completed（历史结果）；不代表当前已解除或允许重新提交。",
        _ => ""
    };
    public string TechnicalDetails => "configuration 与 recovery-state 是两次独立读取，不是原子联合快照；配置修订不是目标授权或配置 CAS。\n"
        + (_snapshot.Observation is { } observed ? $"资格：{observed.EligibilityReason ?? "eligible"}；配置修订：{observed.ConfigurationRevision}\n恢复修订：{observed.RecoveryRevision}\n" : "尚无当前观察。\n")
        + (_snapshot.ErrorCode is { } code ? $"工作流代码：{code}\n" : "")
        + (_snapshot.ReadErrorCode is { } read ? $"读取代码：{read}\n" : "")
        + (_snapshot.QueryErrorCode is { } query ? $"查询代码：{query}\n" : "")
        + (_snapshot.OperationId is { } id ? $"最近请求标识：{id}\n" : "");

    public Task InitializeAsync() => _closed ? Task.CompletedTask : _initialization ??= Track(InitializeCoreAsync());
    private async Task InitializeCoreAsync()
    {
        try
        {
            await _workflow.OpenAsync(); if (_closed) { return; }
            RefreshSnapshot(); if (_snapshot.CanRead && !_snapshot.IsLocked) { await ReadAsync(false); }
        }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "读取未完成，请稍后重新读取。"; } }
        finally { RefreshSnapshot(); }
    }
    private async Task ReadAsync(bool prepare)
    {
        if (prepare ? !CanPrepare : !CanRead) { return; }
        _actionBusy = true; _feedback = ""; _ownedPreparation = null;
        try
        {
            var task = prepare ? _workflow.PrepareAsync(token: _readToken) : _workflow.RefreshAsync(token: _readToken); RefreshSnapshot();
            var result = await task;
            // Dispose invalidates only our token; a late return can describe another
            // view's preparation. Never adopt/discard that after this view closes.
            if (!_closed && prepare) { _ownedPreparation = result.Preparation; }
        }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "读取或准备未完成，未提交。"; } }
        finally { _actionBusy = false; RefreshSnapshot(); }
    }
    private async Task ConfirmAsync()
    {
        if (!CanConfirm || _ownedPreparation is not { } prepared) { return; }
        _actionBusy = true; _feedback = "";
        try { var task = _workflow.SubmitAsync(prepared with { Confirmed = true }); _ownedPreparation = null; RefreshSnapshot(); await task; }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "请以保留的工作流状态为准，不自动重新提交。"; } }
        finally { _actionBusy = false; RefreshSnapshot(); }
    }
    private async Task QueryAsync()
    {
        if (!CanQuery) { return; }
        _actionBusy = true; _feedback = "";
        try
        {
            var task = _workflow.QueryAsync(token: _readToken); RefreshSnapshot(); await task;
            // History and current intent are separate reads/labels; neither can unlock.
            if (!_closed && _workflow.Current.CanRead)
            { var current = _workflow.RefreshAsync(token: _readToken); RefreshSnapshot(); await current; }
        }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "查询未完成；原操作仍保留。"; } }
        finally { _actionBusy = false; RefreshSnapshot(); }
    }
    private Task Track(Task task) { _reads.Add(task); _ = RemoveReadAsync(task); return task; }
    private async Task RemoveReadAsync(Task task) { try { await task; } finally { _reads.Remove(task); } }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
        or AgentCommandException or InvalidOperationException or ArgumentException or System.Security.SecurityException;
    internal void ObserveSnapshot() { if (!_closed && _workflow.Current != _snapshot) { RefreshSnapshot(); } }
    public void RefreshSnapshot()
    {
        if (_closed) { return; } _snapshot = _workflow.Current; OnPropertyChanged(string.Empty);
        RefreshCommand.NotifyCanExecuteChanged(); PrepareCommand.NotifyCanExecuteChanged(); ConfirmCommand.NotifyCanExecuteChanged(); QueryCommand.NotifyCanExecuteChanged();
    }
    internal void ReportCopy(bool success)
    { if (!_closed) { _feedback = success ? "已复制原操作 ID。" : "未能复制，请手动选取原操作 ID。"; OnPropertyChanged(nameof(Feedback)); OnPropertyChanged(nameof(HasFeedback)); } }
    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) { return new(_disposal); } _closed = true;
        _workflow.DismissUnconfirmed(_readToken);
        if (_ownedPreparation is { } owned) { _workflow.TryDiscardPreparation(owned); } _ownedPreparation = null;
        _lifetime.Cancel(); return new(_disposal = DisposeReadsAsync());
    }
    private async Task DisposeReadsAsync() { try { await Task.WhenAll(_reads.ToArray()); } finally { _lifetime.Dispose(); } }
}
