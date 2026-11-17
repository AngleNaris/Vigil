using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemAutorun.Windows.Health;

internal sealed record DiskIoSample(double? ReadBytesPerSecond, double? WriteBytesPerSecond, string? Error = null);

// PhysicalDisk byte throughput, not capacity, active-time %, or summed process IO.
// PDH computes rates from actual counter timestamps and works on non-English Windows.
internal sealed class PdhDiskIoReader : IDisposable
{
    private const uint FormatDoubleUnscaled = 0x00000200 | 0x00001000 | 0x00008000; // DOUBLE | NOSCALE | NOCAP100
    private readonly object _gate = new();
    private QueryHandle? _query;
    private nint _read, _write;
    private DateTimeOffset? _lastAt;
    private long? _failedAt;
    private string _error = "disk-warming-up";
    private bool _disposed;

    internal DiskIoSample Read(DateTimeOffset at)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failedAt is { } failed && Stopwatch.GetElapsedTime(failed) < TimeSpan.FromSeconds(30))
            { return new(null, null, _error); }
            _failedAt = null;
            try
            {
                uint status;
                if (_query is null)
                {
                    status = PdhOpenQuery(null, 0, out var query);
                    _query = query;
                    if (status != 0) { return Failed(status, "open"); }
                    status = PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", 0, out _read);
                    if (status != 0) { return Failed(status, "read-counter"); }
                    status = PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", 0, out _write);
                    if (status != 0) { return Failed(status, "write-counter"); }
                }
                status = PdhCollectQueryData(_query);
                if (status != 0) { return Failed(status, "collect"); }
                var previous = _lastAt; _lastAt = at;
                if (previous is null) { return new(null, null, "disk-warming-up"); }
                if (at <= previous || at - previous > TimeSpan.FromSeconds(3))
                { return new(null, null, "disk-sample-gap"); }
                var read = Value(_query, _read); var write = Value(_query, _write);
                if (read is null || write is null) { return Failed(0, "invalid-data", backoff: false); }
                return new(read, write);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { return Failed(0, exception.GetType().Name); }
        }
    }

    private DiskIoSample Failed(uint status, string stage, bool backoff = true)
    {
        _query?.Dispose(); _query = null; _lastAt = null; _read = _write = 0;
        _failedAt = backoff ? Stopwatch.GetTimestamp() : null; _error = $"disk-{stage}-0x{status:X8}";
        return new(null, null, _error);
    }

    private static double? Value(QueryHandle query, nint counter)
    {
        try
        {
            var status = PdhGetFormattedCounterValue(counter, FormatDoubleUnscaled, out _, out var value);
            return status == 0 && value.Status is 0 or 1 && double.IsFinite(value.Number) && value.Number >= 0
                ? value.Number : null;
        }
        finally { GC.KeepAlive(query); } // Child counter handles must not outlive their owning query.
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true; _query?.Dispose(); _query = null;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedValue
    {
        [FieldOffset(0)] internal uint Status;
        [FieldOffset(8)] internal double Number;
    }

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, nuint userData, out QueryHandle query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(QueryHandle query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(QueryHandle query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FormattedValue value);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
