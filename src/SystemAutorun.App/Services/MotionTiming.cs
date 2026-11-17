using System.Diagnostics;

namespace SystemAutorun.App.Services;

/// <summary>Opt-in in-process timing for isolated regression probes; no observer, clocks or output in normal use.</summary>
internal static class MotionTiming
{
    internal static Action<string, string, double>? Observer { get; set; }

    internal static Scope Measure(string phase, object target) => Observer is { } observer
        ? new Scope(observer, phase, target is FrameworkElement element ? element.Name : target.GetType().Name)
        : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly Action<string, string, double>? _observer;
        private readonly string? _phase, _target;
        private readonly long _started;

        internal Scope(Action<string, string, double> observer, string phase, string target)
        { _observer = observer; _phase = phase; _target = target; _started = Stopwatch.GetTimestamp(); }

        public void Dispose()
        {
            if (_observer is not null)
            { _observer(_phase!, _target!, Stopwatch.GetElapsedTime(_started).TotalMilliseconds); }
        }
    }
}
