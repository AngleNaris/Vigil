using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public sealed class StartupEntryViewModel
{
    public StartupEntryViewModel(StartupEntry entry) => Entry = entry;

    public StartupEntry Entry { get; }

    public string Id => Entry.Id;

    public string Status => Entry.IsDisabled ? "已禁用" : "活动";

    public string Managed => Entry.IsManaged ? $"是 · {Entry.ManagedTask}" : "否";

    public string Permission => Entry.RequiresAdministrator ? "管理员" : "当前用户";

    public string Source => Entry.Source;

    public string Name => Entry.Name;

    public string Target => Entry.Target ?? string.Empty;

    public string Arguments => Entry.Arguments ?? string.Empty;

    public string Origin => Entry.Origin;
}
