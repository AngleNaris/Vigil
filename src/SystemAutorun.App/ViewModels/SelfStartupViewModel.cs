using System.IO;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

/// <summary>A disposable view of the shared workflow. Its lifetime never owns a confirmed submission.</summary>
public sealed class SelfStartupViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SelfStartupWorkflow _workflow;
    private readonly AgentStartupRegistrationClient _registration;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _reads = [];
    private SelfStartupWorkflowSnapshot _snapshot;
    private SelfStartupMutationRequest? _ownedPreparation;
    private StartupRegistrationSnapshot? _observed;
    private Task? _initialization, _disposal;
    private bool _closed, _actionBusy, _readingRegistration;
    private long _registrationGeneration;
    private string _feedback = "";

    internal SelfStartupViewModel(ProjectPaths paths)
    {
        _workflow = SelfStartupWorkflow.ForRoot(paths); _snapshot = _workflow.Current;
        _registration = new(new AgentClient(paths.RootDirectory), paths.RootDirectory);
        RootDirectory = AgentProtocol.ForRoot(paths.RootDirectory).RootDirectory;
        try { using var identity = WindowsIdentity.GetCurrent(); CurrentAccount = identity.Name; }
        catch (System.Security.SecurityException) { CurrentAccount = "当前 Windows 主体（名称不可用）"; }
        EnableCommand = new AsyncRelayCommand(() => Track(PrepareAsync(true)), () => CanChoose);
        DisableCommand = new AsyncRelayCommand(() => Track(PrepareAsync(false)), () => CanChoose);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        QueryCommand = new AsyncRelayCommand(() => Track(QueryAsync()), () => CanQuery);
    }

    public string RootDirectory { get; }
    public string CurrentAccount { get; }
    public IAsyncRelayCommand EnableCommand { get; }
    public IAsyncRelayCommand DisableCommand { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }
    public IAsyncRelayCommand QueryCommand { get; }
    public SelfStartupWorkflowSnapshot Snapshot => _snapshot;
    public bool CanChoose => !_closed && !_actionBusy && _snapshot.CanPrepare;
    public bool CanConfirm => !_closed && !_actionBusy && _snapshot.CanConfirm && ReferenceEquals(_ownedPreparation, _snapshot.Preparation);
    public bool CanQuery => !_closed && !_actionBusy && _snapshot.CanQuery;
    public bool CanCopy => !_closed && OperationId.Length != 0;
    public bool ShowConfirmation => _snapshot.CanConfirm && ReferenceEquals(_ownedPreparation, _snapshot.Preparation);
    public bool ShowChoice => !ShowConfirmation && !_snapshot.IsLocked && !_snapshot.IsBusy;
    public bool ShowScope => !_snapshot.IsLocked;
    public bool ShowOperation => OperationId.Length != 0;
    public bool ShowReadOnlyActions => _snapshot.IsLocked;
    public string OperationId => _snapshot.OperationId ?? "";
    public string CloseCaption => _snapshot.State is SelfStartupWorkflowState.Preparing or SelfStartupWorkflowState.AwaitingConfirmation ? "取消" : "关闭";
    public string ConfirmCaption => _snapshot.Preparation?.Enabled == true ? "确认启用" : "确认停用";
    public string Direction => _snapshot.Preparation is { } preparation ? preparation.Enabled ? "启用" : "停用" : "尚未选择";
    public string ChangeSummary => _snapshot.Preparation is not { } preparation ? "选择启用或停用，仅开始准备，不会立即提交。"
        : preparation.ExpectedRegistration.Registered == preparation.Enabled ? "无需原生变更；提交时服务端仍会重新核验。"
        : preparation.Enabled ? "建立当前根的登录登记。" : "移除已核验的登录登记。";
    public string RegistrationText => _snapshot.CanConfirm && _snapshot.Preparation is { } preparation
        ? preparation.ExpectedRegistration.Registered switch { true => "已登记（准备时读取）", false => "未登记（准备时读取）", _ => "登记尚未核对" }
        : _readingRegistration ? "正在独立核对登记…"
        : _observed is not { Available: true, Registration: { } data } ? "登记尚未核对"
        : data.Registered == false ? "未登记"
        : _observed.RootAssociation switch
        {
            "same-root-lexical" => "已登记 · 命令关联当前根",
            "other-root-lexical" => "已登记 · 命令关联其他根",
            _ => "已登记 · 关联根未确认"
        };
    public string RegistrationSource => _snapshot.CanConfirm ? "准备时的原生前态"
        : _observed is { Available: true, ObservedAt: { } at } ? $"独立读取 {at.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : "";
    public string Heading => _snapshot.State switch
    {
        SelfStartupWorkflowState.AwaitingConfirmation => _snapshot.Preparation?.Enabled == true ? "确认启用登录启动" : "确认停用登录启动",
        SelfStartupWorkflowState.Preparing => "正在核验登录登记",
        SelfStartupWorkflowState.Reserving => "正在保留原操作标识",
        SelfStartupWorkflowState.Submitting => "正在提交登录启动",
        SelfStartupWorkflowState.Finalizing => "正在核对待查记录",
        SelfStartupWorkflowState.OutcomeUnknown => "结果未知",
        SelfStartupWorkflowState.StorageBlocked => "登录启动暂不可用",
        _ => "登录启动"
    };
    public string HeroIcon => _snapshot.IsLocked ? "Help" : "Login";
    public string Notice => _snapshot.State switch
    {
        SelfStartupWorkflowState.Uninitialized or SelfStartupWorkflowState.Loading => "正在读取本根的待查记录…",
        SelfStartupWorkflowState.OutcomeUnknown => "保留原操作，不要重新提交。",
        SelfStartupWorkflowState.StorageBlocked => "待查记录无法读取、保留或核实清理，仅封锁登录启动。",
        SelfStartupWorkflowState.Reserving or SelfStartupWorkflowState.Submitting or SelfStartupWorkflowState.Finalizing => "仅提交一次；关闭不代表撤销，请保留原操作。",
        SelfStartupWorkflowState.Preparing => "准备尚未提交；可以取消或关闭。",
        SelfStartupWorkflowState.Completed => "命令已返回；请以独立登记读取为准，不代表已验证登录启动。",
        SelfStartupWorkflowState.Refused => "服务端明确未执行；需重新准备和确认。",
        SelfStartupWorkflowState.NotSent => "本次未发送；需重新准备和确认。",
        SelfStartupWorkflowState.PreparationFailed => "准备未完成，未提交。请核对技术详情或稍后重新准备。",
        _ => "仅修改登录登记，不立即启动程序。"
    };
    public string ObservationText => _snapshot.Querying ? "正在只读查询原 ID…" : _snapshot.Observation?.State switch
    {
        SelfStartupReceiptState.NotFound => "未找到回执；不代表未执行，仍保留原操作。",
        SelfStartupReceiptState.Running => "回执为 Running；仍保留原操作。",
        SelfStartupReceiptState.Unknown => "回执为 Unknown；仍保留原操作。",
        SelfStartupReceiptState.ResultUnavailable => "结果未保留；不能据此重新提交。",
        SelfStartupReceiptState.CompletedWithResponse => "回执为 Completed（历史响应）；不代表当前登记或允许重新提交。",
        SelfStartupReceiptState.QueryFailed => "查询失败；原操作仍保留，不重新提交。",
        _ => ""
    };
    public string Feedback => _feedback;
    public bool HasObservation => ObservationText.Length != 0;
    public bool HasFeedback => _feedback.Length != 0;
    public string TechnicalDetails => "服务端核对当前运行包的清单与资产完整性，不是发布者认证，也不抵御本账户恶意篡改。摘要、指纹与根关联不是授权。\n"
        + "登记读取只描述当时元数据，不保证 Windows 会实际启动；准备和提交均由服务端重新校验主体、根、目标及原生前态。\n"
        + (_observed is { } value ? $"独立读取：{value.CompletedAt:O}；{(value.Available ? value.RootAssociation : value.Reason)}\n" : "独立登记尚未核对。\n")
        + (_snapshot.ErrorCode is { } error ? $"工作流代码：{error}\n" : "")
        + (_snapshot.RequestId is { } request ? $"请求标识：{request}" : "");

    public Task InitializeAsync() => _closed ? Task.CompletedTask : _initialization ??= Track(InitializeCoreAsync());
    private async Task InitializeCoreAsync()
    {
        try
        {
            await _workflow.OpenAsync().WaitAsync(_lifetime.Token);
            if (_closed) { return; }
            RefreshSnapshot();
            if (!_snapshot.IsLocked && !_snapshot.IsBusy) { await Track(ReadRegistrationAsync()); }
        }
        catch (OperationCanceledException) { }
        finally { RefreshSnapshot(); }
    }
    private async Task PrepareAsync(bool enabled)
    {
        if (!CanChoose) { return; }
        _actionBusy = true; _feedback = ""; _observed = null; ++_registrationGeneration; _readingRegistration = false; RefreshSnapshot();
        try
        {
            var task = _workflow.PrepareAsync(enabled, token: _lifetime.Token); RefreshSnapshot();
            var result = await task; _ownedPreparation = result.Preparation;
            // A late preparation is not confirmation, including after forced owner teardown.
            if (_closed) { DiscardOwnedPreparation(); }
        }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "准备未完成，未提交。"; } }
        finally { _actionBusy = false; RefreshSnapshot(); }
    }
    private async Task ConfirmAsync()
    {
        if (!CanConfirm || _snapshot.Preparation is not { } preparation) { return; }
        _actionBusy = true; _feedback = "";
        try
        {
            // This is the only promotion, reached solely through the explicit confirm command.
            // Never pass the view's token to the shared, already-confirmed workflow.
            var task = _workflow.SubmitAsync(preparation with { Confirmed = true }); RefreshSnapshot();
            var result = await task;
            if (!_closed && result.State is SelfStartupWorkflowState.Completed or SelfStartupWorkflowState.Refused)
            { await Track(ReadRegistrationAsync()); }
        }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "操作未完成，请以保留的工作流状态为准。"; } }
        finally { _actionBusy = false; RefreshSnapshot(); }
    }
    private async Task QueryAsync()
    {
        if (!CanQuery) { return; }
        _actionBusy = true; _feedback = "";
        try { var task = _workflow.QueryAsync(token: _lifetime.Token); RefreshSnapshot(); await task; }
        catch (Exception error) when (Expected(error)) { if (!_closed) { _feedback = "查询失败；不重新提交。"; } }
        finally { _actionBusy = false; RefreshSnapshot(); }
    }
    private async Task ReadRegistrationAsync()
    {
        if (_closed) { return; }
        var generation = ++_registrationGeneration;
        _observed = null; _readingRegistration = true; RefreshSnapshot();
        try { var value = await _registration.ReadAsync("self", _lifetime.Token); if (!_closed && generation == _registrationGeneration) { _observed = value; } }
        catch (Exception error) when (Expected(error)) { /* Unknown stays unknown; never apply expected state or ACK Data. */ }
        finally { if (generation == _registrationGeneration) { _readingRegistration = false; RefreshSnapshot(); } }
    }
    private Task Track(Task task)
    { _reads.Add(task); _ = RemoveReadAsync(task); return task; }
    private async Task RemoveReadAsync(Task task)
    { try { await task; } finally { _reads.Remove(task); } }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or TimeoutException
        or OperationCanceledException or AgentCommandException or InvalidOperationException or ArgumentException or System.Security.SecurityException;

    // The window's timer observes metadata only; no I/O or retry happens here.
    internal void ObserveSnapshot()
    { if (!_closed && _workflow.Current != _snapshot) { RefreshSnapshot(); } }
    public void RefreshSnapshot()
    {
        if (_closed) { return; }
        _snapshot = _workflow.Current;
        OnPropertyChanged(string.Empty);
        EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged(); QueryCommand.NotifyCanExecuteChanged();
    }
    internal void ReportCopy(bool success)
    { if (!_closed) { _feedback = success ? "已复制原操作 ID。" : "未能复制，请手动选取原操作 ID。"; OnPropertyChanged(nameof(Feedback)); OnPropertyChanged(nameof(HasFeedback)); } }
    private void DiscardOwnedPreparation()
    {
        if (_ownedPreparation is not null) { _workflow.TryDiscardPreparation(_ownedPreparation); }
        _ownedPreparation = null;
    }
    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) { return new(_disposal); }
        _closed = true;
        try { _lifetime.Cancel(); } finally { DiscardOwnedPreparation(); }
        // Wait only for view-owned reads. ConfirmAsync keeps its own continuation alive,
        // never updates a closed view, and leaves workflow/lease cleanup with the root.
        return new(_disposal = DisposeReadsAsync());
    }
    private async Task DisposeReadsAsync()
    { try { await Task.WhenAll(_reads.ToArray()); } finally { _lifetime.Dispose(); } }
}
