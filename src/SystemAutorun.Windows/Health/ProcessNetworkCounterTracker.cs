using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SystemAutorun.Windows.Health;

public interface IProcessNetworkCounterSource
{
    (long ReceivedBytes, long SentBytes) ReadCumulativeBytes(int processId);
}

internal sealed class ProcessNetworkCounterTracker : IProcessNetworkCounterSource, IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorAlreadyExists = 183;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const byte TraceLevelInformation = 4;
    private const ulong KernelNetworkIpKeywords = 0x30; // IPv4 (0x10) | IPv6 (0x20)
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const ulong InvalidProcessTraceHandle = ulong.MaxValue;
    private const int PropertiesSize = 120;
    private const int PropertiesBufferSize = PropertiesSize + 512;
    private const int LogfileSize = 448;
    private const int MaximumTrackedProcesses = 4096;

    // Microsoft-Windows-Kernel-Network {7dd42a49-5329-4832-8dfd-43d979153a88}
    private static readonly Guid KernelNetworkProviderId = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    private static readonly Lazy<ProcessNetworkCounterTracker> SharedInstance =
        new(CreateShared, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IProcessNetworkCounterSource Shared => SharedInstance.Value;

    private readonly string _sessionName;
    private readonly ConcurrentDictionary<int, CounterCell> _counters = new();
    private readonly object _pruneLock = new();
    private readonly EtwEventRecordCallback _eventCallback;
    private readonly EtwBufferCallback _bufferCallback;

    private ulong _sessionHandle;
    private ulong _consumerHandle = InvalidProcessTraceHandle;
    private IntPtr _logfilePtr;
    private IntPtr _loggerNamePtr;
    private Thread? _consumerThread;
    private long _lastPruneTick;
    private int _disposed;

    internal ProcessNetworkCounterTracker(string? sessionName = null)
    {
        _sessionName = string.IsNullOrWhiteSpace(sessionName)
            ? "Vigil-KernelNetwork"
            : sessionName;
        _eventCallback = OnEventRecord;
        _bufferCallback = OnBufferCallback;
        Start();
    }

    internal bool IsActive => _sessionHandle != 0 && _consumerHandle != InvalidProcessTraceHandle && Volatile.Read(ref _disposed) == 0;

    private static ProcessNetworkCounterTracker CreateShared()
    {
        var tracker = new ProcessNetworkCounterTracker();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.Dispose();
        return tracker;
    }

    public (long ReceivedBytes, long SentBytes) ReadCumulativeBytes(int processId)
    {
        if (processId <= 4 || Volatile.Read(ref _disposed) != 0)
        {
            return (0, 0);
        }

        if (_counters.TryGetValue(processId, out var cell))
        {
            return (Interlocked.Read(ref cell.ReceivedBytes), Interlocked.Read(ref cell.SentBytes));
        }

        return (0, 0);
    }

    private void Start()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            return;
        }

        var propertiesPtr = AllocateProperties(_sessionName);
        try
        {
            var status = StartTraceW(out _sessionHandle, _sessionName, propertiesPtr);
            if (status == ErrorAlreadyExists)
            {
                StopSessionByName(_sessionName);
                RtlZeroMemory(propertiesPtr, (nuint)PropertiesBufferSize);
                InitializeProperties(propertiesPtr, _sessionName);
                status = StartTraceW(out _sessionHandle, _sessionName, propertiesPtr);
            }

            if (status != ErrorSuccess || _sessionHandle == 0)
            {
                _sessionHandle = 0;
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(propertiesPtr);
        }

        var provider = KernelNetworkProviderId;
        var enableStatus = EnableTraceEx2(
            _sessionHandle,
            ref provider,
            EventControlCodeEnableProvider,
            TraceLevelInformation,
            KernelNetworkIpKeywords,
            0,
            0,
            IntPtr.Zero);
        if (enableStatus != ErrorSuccess)
        {
            StopTraceSession();
            return;
        }

        _loggerNamePtr = Marshal.StringToHGlobalUni(_sessionName);
        _logfilePtr = Marshal.AllocHGlobal(LogfileSize);
        RtlZeroMemory(_logfilePtr, (nuint)LogfileSize);

        // EVENT_TRACE_LOGFILEW (x64, evntrace.h):
        // 8: LoggerName, 28: ProcessTraceMode, 400: BufferCallback, 424: EventRecordCallback
        Marshal.WriteIntPtr(_logfilePtr, 8, _loggerNamePtr);
        Marshal.WriteInt32(_logfilePtr, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
        Marshal.WriteIntPtr(_logfilePtr, 400, Marshal.GetFunctionPointerForDelegate(_bufferCallback));
        Marshal.WriteIntPtr(_logfilePtr, 424, Marshal.GetFunctionPointerForDelegate(_eventCallback));

        _consumerHandle = OpenTraceW(_logfilePtr);
        if (_consumerHandle == InvalidProcessTraceHandle)
        {
            FreeLogfileResources();
            StopTraceSession();
            return;
        }

        _consumerThread = new Thread(ConsumeLoop)
        {
            Name = "Vigil-KernelNetwork-ETW",
            IsBackground = true,
        };
        _consumerThread.Start();
    }

    private void ConsumeLoop()
    {
        var handles = new[] { _consumerHandle };
        try
        {
            _ = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Graceful degradation: network bytes remain zero if ETW terminates unexpectedly.
        }
    }

    private uint OnBufferCallback(IntPtr logfile) =>
        Volatile.Read(ref _disposed) == 0 ? 1u : 0u;

    private void OnEventRecord(IntPtr eventRecordPtr)
    {
        if (eventRecordPtr == IntPtr.Zero || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // EVENT_RECORD / EVENT_HEADER (x64, evntcons.h):
        // 45: EventHeader.EventDescriptor.Opcode (byte)
        // 86: UserDataLength (ushort)
        // 96: UserData (IntPtr)
        var opcode = Marshal.ReadByte(eventRecordPtr, 45);
        bool isSend;
        if (opcode is 10 or 26 or 42 or 58)
        {
            isSend = true;
        }
        else if (opcode is 11 or 27 or 43 or 59)
        {
            isSend = false;
        }
        else
        {
            return;
        }

        var userDataLength = unchecked((ushort)Marshal.ReadInt16(eventRecordPtr, 86));
        if (userDataLength < 8)
        {
            return;
        }

        var userData = Marshal.ReadIntPtr(eventRecordPtr, 96);
        if (userData == IntPtr.Zero)
        {
            return;
        }

        var pid = unchecked((uint)Marshal.ReadInt32(userData, 0));
        var size = unchecked((uint)Marshal.ReadInt32(userData, 4));
        if (pid <= 4 || pid > int.MaxValue || size == 0)
        {
            return;
        }

        var cell = _counters.GetOrAdd((int)pid, static _ => new CounterCell());
        Volatile.Write(ref cell.LastSeenTick, Environment.TickCount64);
        if (isSend)
        {
            Interlocked.Add(ref cell.SentBytes, size);
        }
        else
        {
            Interlocked.Add(ref cell.ReceivedBytes, size);
        }

        if (_counters.Count > MaximumTrackedProcesses)
        {
            PruneStaleCounters();
        }
    }

    private void PruneStaleCounters()
    {
        if (!Monitor.TryEnter(_pruneLock))
        {
            return;
        }

        try
        {
            var nowTick = Environment.TickCount64;
            if (nowTick - _lastPruneTick < 60_000)
            {
                return;
            }
            _lastPruneTick = nowTick;

            foreach (var pair in _counters)
            {
                if (nowTick - Volatile.Read(ref pair.Value.LastSeenTick) > 300_000)
                {
                    _counters.TryRemove(pair.Key, out _);
                }
            }
        }
        finally
        {
            Monitor.Exit(_pruneLock);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopTraceSession();

        if (_consumerHandle != InvalidProcessTraceHandle)
        {
            _ = CloseTrace(_consumerHandle);
            _consumerHandle = InvalidProcessTraceHandle;
        }

        var exitedCleanly = _consumerThread is null || _consumerThread.Join(2000);
        _consumerThread = null;

        if (exitedCleanly)
        {
            FreeLogfileResources();
        }
    }

    private void FreeLogfileResources()
    {
        if (_logfilePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_logfilePtr);
            _logfilePtr = IntPtr.Zero;
        }

        if (_loggerNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_loggerNamePtr);
            _loggerNamePtr = IntPtr.Zero;
        }
    }

    private void StopTraceSession()
    {
        if (_sessionHandle == 0)
        {
            return;
        }

        var propertiesPtr = AllocateProperties(_sessionName);
        try
        {
            _ = ControlTraceW(_sessionHandle, _sessionName, propertiesPtr, EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(propertiesPtr);
            _sessionHandle = 0;
        }
    }

    private static void StopSessionByName(string sessionName)
    {
        var propertiesPtr = AllocateProperties(sessionName);
        try
        {
            _ = ControlTraceW(0, sessionName, propertiesPtr, EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(propertiesPtr);
        }
    }

    private static IntPtr AllocateProperties(string sessionName)
    {
        var ptr = Marshal.AllocHGlobal(PropertiesBufferSize);
        RtlZeroMemory(ptr, (nuint)PropertiesBufferSize);
        InitializeProperties(ptr, sessionName);
        return ptr;
    }

    private static void InitializeProperties(IntPtr ptr, string sessionName)
    {
        // EVENT_TRACE_PROPERTIES (x64, evntrace.h):
        // 0: Wnode.BufferSize, 40: Wnode.ClientContext (1 = QPC), 44: Wnode.Flags
        // 48: BufferSize (KB), 64: LogFileMode, 68: FlushTimer (sec), 116: LoggerNameOffset
        Marshal.WriteInt32(ptr, 0, PropertiesBufferSize);
        Marshal.WriteInt32(ptr, 40, 1);
        Marshal.WriteInt32(ptr, 44, unchecked((int)WnodeFlagTracedGuid));
        Marshal.WriteInt32(ptr, 48, 64);
        Marshal.WriteInt32(ptr, 64, unchecked((int)EventTraceRealTimeMode));
        Marshal.WriteInt32(ptr, 68, 1);
        Marshal.WriteInt32(ptr, 116, PropertiesSize);

        var nameBytes = System.Text.Encoding.Unicode.GetBytes(sessionName);
        var copyLength = Math.Min(nameBytes.Length, PropertiesBufferSize - PropertiesSize - 2);
        Marshal.Copy(nameBytes, 0, ptr + PropertiesSize, copyLength);
    }

    private sealed class CounterCell
    {
        public long ReceivedBytes;
        public long SentBytes;
        public long LastSeenTick;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EtwEventRecordCallback(IntPtr eventRecord);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint EtwBufferCallback(IntPtr logfile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartTraceW(
        out ulong traceHandle,
        string instanceName,
        IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(
        ulong traceHandle,
        string? instanceName,
        IntPtr properties,
        uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EnableTraceEx2(
        ulong traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint ProcessTrace(
        [In] ulong[] handleArray,
        uint handleCount,
        IntPtr startTime,
        IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void RtlZeroMemory(IntPtr destination, nuint length);
}
