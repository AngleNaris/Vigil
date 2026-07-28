using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.App.ViewModels;

public partial class StartupManagerViewModel : ObservableObject
{
    private readonly StartupManager _manager;
    private readonly StartupCacheStore _cache;
    private readonly Func<StartupEntry, string, Task<bool>> _runElevated;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToTasksCommand))]
    private StartupEntryViewModel? selectedEntry;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private FilterOption selectedFilter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToTasksCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "就绪。";

    [ObservableProperty]
    private string countMessage = string.Empty;

    internal StartupManagerViewModel(
        StartupManager manager,
        StartupCacheStore cache,
        Func<StartupEntry, string, Task<bool>> runElevated)
    {
        _manager = manager;
        _cache = cache;
        _runElevated = runElevated;
        Filters = new[]
        {
            new FilterOption("全部", string.Empty),
            new FilterOption("活动", "active"),
            new FilterOption("已禁用", "disabled"),
            new FilterOption("本工具托管", "managed"),
        };
        selectedFilter = Filters[0];
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;
    }

    public ObservableCollection<StartupEntryViewModel> Entries { get; } = new();

    public ICollectionView EntriesView { get; }

    public IReadOnlyList<FilterOption> Filters { get; }

    public Func<string, bool>? ConfirmRequested { get; set; }

    public event EventHandler<string>? ErrorRequested;

    public event EventHandler<StartupEntry>? AddProgramRequested;

    internal async Task InitializeAsync()
    {
        var cached = await _cache.LoadAsync().ConfigureAwait(true);
        if (cached is not null)
        {
            ReplaceEntries(cached);
            StatusMessage = "已显示缓存，正在扫描 Windows 启动项。";
        }

        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunGlobalCommand))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync("正在扫描 Windows 启动项...", async () =>
        {
            var snapshot = await _manager.GetSnapshotAsync();
            ReplaceEntries(snapshot);
            await _cache.SaveAsync(snapshot);
            StatusMessage = "启动项扫描完成。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync()
    {
        var entry = SelectedEntry?.Entry;
        if (entry is null || !(ConfirmRequested?.Invoke($"确定禁用启动项“{entry.Name}”吗？") ?? false))
        {
            return;
        }

        await RunBusyAsync($"正在禁用 {entry.Name}...", async () =>
        {
            if (entry.RequiresAdministrator)
            {
                if (!await _runElevated(entry, "disable"))
                {
                    throw new InvalidOperationException("管理员禁用操作失败。");
                }
            }
            else
            {
                _ = await _manager.DisableAsync(entry.Source, entry.Name, entry.Origin);
            }

            await RefreshCoreAsync();
            StatusMessage = $"已禁用 {entry.Name}。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var entry = SelectedEntry?.Entry;
        if (entry?.BackupId is null
            || !(ConfirmRequested?.Invoke($"确定恢复启动项“{entry.Name}”吗？") ?? false))
        {
            return;
        }

        await RunBusyAsync($"正在恢复 {entry.Name}...", async () =>
        {
            if (entry.RequiresAdministrator)
            {
                if (!await _runElevated(entry, "restore"))
                {
                    throw new InvalidOperationException("管理员恢复操作失败。");
                }
            }
            else
            {
                _ = await _manager.RestoreAsync(entry.BackupId);
            }

            await RefreshCoreAsync();
            StatusMessage = $"已恢复 {entry.Name}。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanAddToTasks))]
    private void AddToTasks()
    {
        if (SelectedEntry is not null)
        {
            AddProgramRequested?.Invoke(this, SelectedEntry.Entry);
        }
    }

    partial void OnSelectedEntryChanged(StartupEntryViewModel? value)
    {
        _ = value;
        NotifyCommandStates();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        RefreshFilter();
    }

    partial void OnSelectedFilterChanged(FilterOption value)
    {
        _ = value;
        RefreshFilter();
    }

    private bool CanRunGlobalCommand() => !IsBusy;

    private bool CanDisable() => SelectedEntry is { Entry.IsDisabled: false } && !IsBusy;

    private bool CanRestore() => SelectedEntry is { Entry.IsDisabled: true, Entry.BackupId: not null } && !IsBusy;

    private bool CanAddToTasks() => SelectedEntry is
    {
        Entry.IsDisabled: false,
        Entry.IsManaged: false,
        Entry.Target: not null,
    } && !IsBusy;

    private async Task RunBusyAsync(string message, Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = message;
        NotifyCommandStates();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            StatusMessage = $"操作失败：{exception.Message}";
            ErrorRequested?.Invoke(this, exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private async Task RefreshCoreAsync()
    {
        var snapshot = await _manager.GetSnapshotAsync();
        ReplaceEntries(snapshot);
        await _cache.SaveAsync(snapshot);
    }

    private void ReplaceEntries(StartupManagementSnapshot snapshot)
    {
        var selectedId = SelectedEntry?.Id;
        Entries.Clear();
        foreach (var entry in snapshot.Entries
            .OrderBy(item => item.IsDisabled)
            .ThenBy(item => item.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Entries.Add(new StartupEntryViewModel(entry));
        }

        EntriesView.Refresh();
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Id == selectedId);
        CountMessage = $"活动 {snapshot.TotalActive}  ·  托管 {snapshot.ManagedActive}  ·  已禁用 {snapshot.Disabled}";
    }

    private bool FilterEntry(object item)
    {
        if (item is not StartupEntryViewModel viewModel)
        {
            return false;
        }

        var filterMatch = SelectedFilter.Value switch
        {
            "active" => !viewModel.Entry.IsDisabled,
            "disabled" => viewModel.Entry.IsDisabled,
            "managed" => viewModel.Entry.IsManaged,
            _ => true,
        };
        if (!filterMatch)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return viewModel.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || viewModel.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || viewModel.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || viewModel.Arguments.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || viewModel.Origin.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || viewModel.Managed.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshFilter()
    {
        EntriesView.Refresh();
    }

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        AddToTasksCommand.NotifyCanExecuteChanged();
    }
}
