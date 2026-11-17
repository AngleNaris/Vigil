namespace SystemAutorun.App.Services;

/// <summary>View cleanup only; committed operations remain owned by their workflow coordinator.</summary>
internal sealed class ToolWindowLifetimes
{
    private readonly object _sync = new();
    private readonly Dictionary<ToolWindow, Entry> _entries = [];
    // Keep diagnostics for already-retired views without retaining every failed window/task.
    private readonly Dictionary<Type, (int Count, Exception Last)> _failures = [];
    private ExitBatch? _exit;
    internal int Count { get { lock (_sync) { return _entries.Count; } } }

    internal void Track(ToolWindow window)
    {
        window.Dispatcher.VerifyAccess();
        lock (_sync)
        {
            if (_exit is not null) { throw new InvalidOperationException("Tool admission is closed during application exit."); }
            if (_entries.ContainsKey(window)) { return; }
            if (window.ClosureTask.IsCompleted) { throw new InvalidOperationException("Cannot register an already closed tool."); }
            var entry = new Entry(window);
            _entries.Add(window, entry);
            entry.Completion = ObserveAsync(window);
        }
    }

    private async Task ObserveAsync(ToolWindow window)
    {
        Exception? failure = null;
        try
        {
            await window.ClosureTask.ConfigureAwait(false);
            // Closed handlers establish the cleanup task. Reading it before Closed can yield CompletedTask.
            await window.Dispatcher.InvokeAsync(() => window.ResourceCleanupTask).Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_sync)
            {
                _entries.Remove(window);
                if (failure is not null)
                {
                    var type = window.GetType(); var previous = _failures.GetValueOrDefault(type);
                    _failures[type] = (previous.Count + 1, failure);
                }
            }
        }
    }

    internal ExitBatch BeginExit()
    {
        lock (_sync) { return _exit ??= new(this, _entries.Values.ToArray()); }
    }

    private Exception[] TakeFailures()
    {
        lock (_sync)
        {
            var failures = _failures.Select(pair => new InvalidOperationException(
                $"{pair.Key.Name} view cleanup failed {pair.Value.Count} time(s); latest failure follows.", pair.Value.Last)).ToArray();
            _failures.Clear(); return failures;
        }
    }

    internal sealed class Entry(ToolWindow window)
    {
        internal ToolWindow Window { get; } = window;
        internal Task Completion { get; set; } = Task.CompletedTask;
    }

    internal sealed class ExitBatch(ToolWindowLifetimes registry, Entry[] entries)
    {
        private Task<IReadOnlyList<Exception>>? _drain;
        internal Task<IReadOnlyList<Exception>> CloseAndDrainAsync(Action closeOwner) => _drain ??= DrainAsync(closeOwner);

        private async Task<IReadOnlyList<Exception>> DrainAsync(Action closeOwner)
        {
            var errors = new List<Exception>(); var waits = new List<Task>();
            try { closeOwner(); }
            catch (Exception error) { errors.Add(new InvalidOperationException("Main window cleanup failed.", error)); }
            foreach (var entry in entries)
            {
                var window = entry.Window;
                try
                {
                    if (!window.ClosureTask.IsCompleted)
                    {
                        if (window.Dispatcher.CheckAccess()) { window.CloseForApplicationExit(); }
                        else { await window.Dispatcher.InvokeAsync(window.CloseForApplicationExit); }
                    }
                }
                catch (Exception error) { errors.Add(new InvalidOperationException($"{window.GetType().Name} close failed.", error)); }
                if (window.ClosureTask.IsCompleted) { waits.Add(entry.Completion); }
                else
                {
                    // A synchronous guard can still refuse. Do not hang forever or claim its cleanup completed.
                    errors.Add(new InvalidOperationException($"{window.GetType().Name} refused application-exit closure; view cleanup is incomplete."));
                }
            }
            await Task.WhenAll(waits);
            errors.AddRange(registry.TakeFailures()); return errors;
        }
    }
}
