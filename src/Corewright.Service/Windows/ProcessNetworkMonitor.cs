using System.ComponentModel;
using System.Runtime.InteropServices;
using Corewright.Core.Engine;

namespace Corewright.Service.Windows;

/// <summary>Bounded real-time ETW consumer, alive only while network data is requested. No event allocations.</summary>
internal sealed unsafe partial class ProcessNetworkMonitor : IDisposable
{
    private static readonly Guid Provider = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private sealed class Counter(ProcessKey key) { public readonly ProcessKey Key = key; public long Send, Receive; public bool Seen; }
    private readonly Lock _sync = new();
    private readonly Dictionary<int, Counter> _counters = [];
    private readonly List<int> _gone = [];
    // All callers hold NetworkOwnership, so a session with this name can only be abandoned.
    private const string SessionName = "Corewright-Network";
    private ulong _session, _trace = ulong.MaxValue;
    private GCHandle _self;
    private Thread? _reader;
    private long _sampleMs;
    private long _stopRetryAfter;
    private volatile bool _running;
    public string? Error { get; private set; }
    public bool Available => _running;

    public ProcessNetworkMonitor(NetworkOwnership ownership)
    {
        if (!ownership.TryAcquire(out var error)) { Error = error; return; }
        try
        {
            fixed (char* name = SessionName)
            {
                byte* stale = stackalloc byte[1024]; new Span<byte>(stale, 1024).Clear();
                ((Properties*)stale)->Size = 1024;
                ((Properties*)stale)->LoggerOffset = (uint)sizeof(Properties);
                uint stop = ControlTraceW(0, name, (Properties*)stale, 1);
                if (stop != 0 && stop != 4201) Check(stop); // ERROR_WMI_INSTANCE_NOT_FOUND is already stopped.
                byte* buffer = stackalloc byte[1024]; new Span<byte>(buffer, 1024).Clear();
                var props = (Properties*)buffer;
                props->Size = 1024; props->Flags = 0x20000; props->Clock = 2; props->Guid = Guid.NewGuid();
                props->BufferKb = 16; props->MinBuffers = 2; props->MaxBuffers = 4;
                props->Mode = 0x100 | 0x02000000 | 0x10000000; // real time, system logger, shared buffers (not per CPU)
                props->FlushSeconds = 1; props->LoggerOffset = (uint)sizeof(Properties);
                SessionName.AsSpan().CopyTo(new Span<char>(buffer + props->LoggerOffset, SessionName.Length + 1));
                Check(StartTraceW(out ulong session, name, props));
                _session = session; // Failed StartTrace output is not an owned handle.
                Check(EnableTraceEx2(_session, in Provider, 1, 4, 0x30, 0, 2000, 0));
                _self = GCHandle.Alloc(this);
                var file = new LogFile { Logger = name, Mode = 0x10000100, Callback = &OnEvent, BufferCallback = &OnBuffer, Context = GCHandle.ToIntPtr(_self) };
                _trace = OpenTraceW(ref file);
                if (_trace == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
                _running = true; _sampleMs = Environment.TickCount64;
                _reader = new Thread(Read) { IsBackground = true, Name = "Corewright network ETW" }; _reader.Start();
            }
        }
        catch (Exception e) { Error = e.Message; Dispose(); }
    }
    private void Read()
    {
        ulong trace = _trace;
        uint result = ProcessTrace(&trace, 1, 0, 0);
        if (_running) Error = "network ETW stopped: " + result;
        _running = false;
        if (_self.IsAllocated) _self.Free();
    }
    [UnmanagedCallersOnly]
    private static uint OnBuffer(LogFile* file)
    {
        if (file->EventsLost == 0) return 1;
        var monitor = (ProcessNetworkMonitor?)GCHandle.FromIntPtr(file->Context).Target;
        if (monitor is not null) { monitor.Error = "network ETW lost events; rates unavailable"; monitor._running = false; }
        return 0;
    }
    [UnmanagedCallersOnly]
    private static void OnEvent(Record* record)
    {
        // Version 0 templates start with payload PID and size, for TCP/UDP on IPv4/IPv6.
        if (record->Provider != Provider || record->Version != 0 || record->Length < 8 || record->Data == null || record->Context == 0) return;
        bool send = record->Id is 10 or 26 or 42 or 58;
        if (!send && record->Id is not (11 or 27 or 43 or 59)) return;
        var monitor = (ProcessNetworkMonitor?)GCHandle.FromIntPtr(record->Context).Target;
        if (monitor is null) return;
        lock (monitor._sync)
            if (monitor._counters.TryGetValue(*(int*)record->Data, out var c) && record->Timestamp >= c.Key.CreateTime)
            {
                uint size = *(uint*)(record->Data + 4);
                if (send) c.Send += size; else c.Receive += size;
            }
    }
    public void Sample(IReadOnlyList<ProcessInfo> processes)
    {
        long now = Environment.TickCount64; double seconds = Math.Max(0.001, (now - _sampleMs) / 1000.0); _sampleMs = now;
        lock (_sync)
        {
            foreach (var c in _counters.Values) c.Seen = false;
            for (int i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                bool warm = _counters.TryGetValue(p.Key.Pid, out var c) && c.Key == p.Key;
                if (!warm) _counters[p.Key.Pid] = c = new(p.Key);
                p.NetAvailable = Available && warm;
                c!.Seen = true;
                p.NetSend = p.NetAvailable ? c.Send / seconds : 0; p.NetReceive = p.NetAvailable ? c.Receive / seconds : 0;
                c.Send = c.Receive = 0;
            }
            foreach (var (pid, c) in _counters) if (!c.Seen) _gone.Add(pid);
            foreach (int pid in _gone) _counters.Remove(pid);
            _gone.Clear();
        }
    }
    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
    public void Dispose()
    {
        _running = false;
        if (Environment.TickCount64 < _stopRetryAfter) return;
        if (_session != 0)
        {
            byte* buffer = stackalloc byte[1024]; new Span<byte>(buffer, 1024).Clear(); ((Properties*)buffer)->Size = 1024;
            ((Properties*)buffer)->LoggerOffset = (uint)sizeof(Properties);
            uint result = ControlTraceW(_session, null, (Properties*)buffer, 1);
            if (result is 0 or 4201) _session = 0;
            else { Error = "network ETW stop failed: " + result; _stopRetryAfter = Environment.TickCount64 + 60_000; }
        }
        if (_trace != ulong.MaxValue)
        {
            uint result = CloseTrace(_trace);
            if (result is 0 or 7007) _trace = ulong.MaxValue; // ERROR_CTX_CLOSE_PENDING: consumer will finish.
            else { Error = "network ETW consumer close failed: " + result; _stopRetryAfter = Environment.TickCount64 + 60_000; }
        }
        // CloseTrace cancels ProcessTrace; do not free callback context until the consumer has returned.
        if (_reader is { IsAlive: true } && !_reader.Join(3000))
        {
            Error = "network ETW consumer did not stop; callback context retained";
            _stopRetryAfter = Environment.TickCount64 + 60_000;
            return;
        }
        if (_self.IsAllocated) _self.Free();
        _counters.Clear();
    }
    public bool Stopped => _session == 0 && _trace == ulong.MaxValue && (_reader is null || !_reader.IsAlive);
    [StructLayout(LayoutKind.Sequential)] private struct Properties
    {
        public uint Size, ProviderId; public ulong Historical; public long Timestamp; public Guid Guid; public uint Clock, Flags;
        public uint BufferKb, MinBuffers, MaxBuffers, MaxFileMb, Mode, FlushSeconds, EnableFlags;
        public int Age; public uint Buffers, Free, EventsLost, Written, LogLost, RealTimeLost; public nint LoggerThread;
        public uint FileOffset, LoggerOffset;
    }
    // EVENT_TRACE_LOGFILEW and EVENT_RECORD win-x64 ABI (SDK evntrace.h / evntcons.h).
    [StructLayout(LayoutKind.Explicit, Size = 448)] private struct LogFile
    {
        [FieldOffset(8)] public char* Logger; [FieldOffset(28)] public uint Mode;
        [FieldOffset(400)] public delegate* unmanaged<LogFile*, uint> BufferCallback; [FieldOffset(416)] public uint EventsLost;
        [FieldOffset(424)] public delegate* unmanaged<Record*, void> Callback; [FieldOffset(440)] public nint Context;
    }
    [StructLayout(LayoutKind.Explicit, Size = 112)] private struct Record
    {
        [FieldOffset(16)] public long Timestamp; [FieldOffset(24)] public Guid Provider;
        [FieldOffset(40)] public ushort Id; [FieldOffset(42)] public byte Version; [FieldOffset(86)] public ushort Length;
        [FieldOffset(96)] public byte* Data; [FieldOffset(104)] public nint Context;
    }
    [LibraryImport("advapi32.dll")] private static partial uint StartTraceW(out ulong session, char* name, Properties* props);
    [LibraryImport("advapi32.dll")] private static partial uint EnableTraceEx2(ulong session, in Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, nint parameters);
    [LibraryImport("advapi32.dll", SetLastError = true)] private static partial ulong OpenTraceW(ref LogFile file);
    [LibraryImport("advapi32.dll")] private static partial uint ProcessTrace(ulong* traces, uint count, nint start, nint end);
    [LibraryImport("advapi32.dll")] private static partial uint ControlTraceW(ulong session, char* name, Properties* props, uint control);
    [LibraryImport("advapi32.dll")] private static partial uint CloseTrace(ulong trace);
}
