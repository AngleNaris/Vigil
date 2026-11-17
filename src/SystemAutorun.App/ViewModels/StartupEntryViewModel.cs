using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public sealed class StartupEntryViewModel(StartupEntry entry)
{
    public StartupEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Source => Entry.Source;
    public string Target => Entry.Target ?? string.Empty;
    public string Arguments => Entry.Arguments ?? string.Empty;
    public string WorkingDirectory => Entry.WorkingDirectory ?? string.Empty;
    public string Origin => Entry.Origin;
    public bool HasError => !string.IsNullOrEmpty(Entry.Error);
    public string Status => HasError ? "UNKNOWN" : Entry.IsDisabled ? "DISABLED" : "REGISTERED";
    public string StatusDescription => HasError ? "不可读 · 只读" : Entry.IsDisabled ? "已禁用 · 备份未验证" : Entry.IsManaged ? "已登记 · 已纳管" : "已登记";
    public string Icon => HasError ? "Warning" : Source is "Startup" or "Common Startup" ? "File" : "Code";
    public string Managed => Entry.IsManaged ? "是" : "否";
    public string ManagedDetail => Entry.IsManaged ? $"由任务 {Entry.ManagedTask} 纳管" : "未由本配置纳管";
    public string Permission => Entry.RequiresAdministrator ? "管理员范围" : "当前用户";
    public string SourceDetail => $"{Source} · {Permission}";
    public string TargetDetail => string.IsNullOrWhiteSpace(Arguments) ? Target : $"{Target}\n{Arguments}";
}
