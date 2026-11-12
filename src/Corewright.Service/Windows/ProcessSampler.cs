using System.Diagnostics;
using System.Runtime.InteropServices;
using Corewright.Core.Engine;

namespace Corewright.Service.Windows;

/// <summary>
/// One NtQuerySystemInformation(SystemProcessInformation) call per tick into a reused native buffer.
/// Known processes are updated in place; strings are read only for new processes.
/// </summary>
internal sealed unsafe class ProcessSampler : IDisposable
{
    // SYSTEM_PROCESS_INFORMATION offsets (x64).
    private const int OffNext = 0, OffThreads = 4, OffCreate = 32, OffUser = 40, OffKernel = 48;
    private const int OffNameLen = 56, OffNameBuf = 64, OffBasePrio = 72, OffPid = 80, OffParent = 88;
    private const int OffHandles = 96, OffSession = 100, OffWorkingSet = 144, OffPrivate = 200;
    private const int OffReadXfer = 232, OffWriteXfer = 240, OffOtherXfer = 248;
    private const int OffPrivateWs = 8, OffHardFaults = 16, OffCycles = 24, OffPeakVirtual = 112, OffVirtual = 120;
    private const int OffPageFaults = 128, OffPeakWs = 136, OffPagedPool = 160, OffNonPagedPool = 176, OffPeakPagefile = 192;
    private const int OffReadOps = 208, OffWriteOps = 216, OffOtherOps = 224;
    // SYSTEM_THREAD_INFORMATION (x64, 80 bytes) entries follow the 256-byte process entry; ThreadState@68, 1 = Ready.
    private const int OffThreadArray = 256, ThreadStride = 80, OffThreadState = 68, ThreadReady = 1;

    private readonly Dictionary<ProcessKey, ProcessInfo> _live = [];
    private readonly Dictionary<int, ProcessInfo> _byPid = [];
    private readonly List<ProcessInfo> _list = [];
    private readonly List<ProcessInfo> _new = [];
    private readonly List<ProcessKey> _gone = [];
    private readonly (ProcessKey Key, ProcessKey? GameRoot, long Expires)[] _recentParents = new (ProcessKey, ProcessKey?, long)[512];
    private int _nextParent;
    private readonly int _cpuCount = (int)Math.Max(1, Native.GetActiveProcessorCount(Native.ALL_PROCESSOR_GROUPS));
    private readonly int _selfPid = Environment.ProcessId;

    private byte* _buffer;
    private uint _capacity;
    private long _lastTimestamp;
    private long _tick;
    private long _lastIdle, _lastKernel, _lastUser;
    private long _faults;
    private double _faultSeconds;

    public ProcessSampler()
    {
        _capacity = 1 << 20;
        _buffer = (byte*)NativeMemory.Alloc(_capacity);
    }

    public IReadOnlyList<ProcessInfo> Processes => _list;
    public double SystemCpuPercent { get; private set; }
    public double TotalIoBytesPerSec { get; private set; }
    /// <summary>Seconds since the previous sample (0 at the first).</summary>
    public double IntervalSeconds { get; private set; }
    /// <summary>CPU time all logical processors had since the previous sample, and the part they were busy (100 ns units).</summary>
    public long CpuAvailableDelta { get; private set; }
    public long CpuBusyDelta { get; private set; }
    public ulong MemoryTotal { get; private set; }
    public ulong MemoryUsed { get; private set; }
    /// <summary>PM-51: threads waiting for a processor at the moment of the snapshot (Task Manager's processor queue).</summary>
    public int ReadyThreads { get; private set; }
    /// <summary>PM-51: hard page faults per second over all processes since the previous sample.</summary>
    public double HardFaultsPerSec { get; private set; }
    /// <summary>The last Sample() could not read the process list; its values are from the previous sample.</summary>
    public bool Failed { get; private set; }

    public bool TryGet(int pid, out ProcessInfo info) => _byPid.TryGetValue(pid, out info!);

    public bool IsAlive(ProcessKey key) => _live.ContainsKey(key);

    public void Sample()
    {
        int status;
        while ((status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buffer, _capacity, out uint needed))
               == Native.STATUS_INFO_LENGTH_MISMATCH)
        {
            NativeMemory.Free(_buffer);
            _capacity = Math.Max(needed + (64u << 10), _capacity * 2);
            _buffer = (byte*)NativeMemory.Alloc(_capacity);
        }
        if (status < 0)
        {
            // No sample: nothing to add up. The next good sample's deltas span this gap consistently.
            IntervalSeconds = 0;
            Failed = true;
            return;
        }

        Failed = false;
        long now = Stopwatch.GetTimestamp();
        double dt = _lastTimestamp == 0 ? 0 : (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        _lastTimestamp = now;
        IntervalSeconds = dt;
        _tick++;
        _list.Clear();
        double totalIo = 0;
        int ready = 0;
        long hardFaults = 0;

        byte* p = _buffer;
        while (true)
        {
            int pid = (int)*(nint*)(p + OffPid);
            long create = *(long*)(p + OffCreate);
            var key = new ProcessKey(pid, create);
            bool isNew = false;

            if (!_live.TryGetValue(key, out var info))
            {
                string name = pid == 0 ? "Idle" : ReadName(p);
                info = new ProcessInfo(key, name)
                {
                    SessionId = *(int*)(p + OffSession),
                    IsProtected = ProtectedProcesses.Is(pid, name, _selfPid),
                    Path = pid is 0 or 4 ? null : QueryPath(key),
                    // Parent pid only; resolved to a full key (with PID-reuse check) after the pass.
                    Parent = new ProcessKey((int)*(nint*)(p + OffParent), 0),
                };
                info.LastCpuTime = *(long*)(p + OffUser) + *(long*)(p + OffKernel);
                info.LastIoBytes = *(long*)(p + OffReadXfer) + *(long*)(p + OffWriteXfer) + *(long*)(p + OffOtherXfer);
                _live.Add(key, info);
                _new.Add(info);
                isNew = true;
            }

            long cpu = *(long*)(p + OffUser) + *(long*)(p + OffKernel);
            if (!isNew && info.Path is null && info.IdentityAttempts < 4 && pid is not (0 or 4)) info.Path = QueryPath(key);
            long io = *(long*)(p + OffReadXfer) + *(long*)(p + OffWriteXfer) + *(long*)(p + OffOtherXfer);
            // Usage history: exact CPU time and I/O since the previous sample. A process born between two samples did
            // all of its work in that interval; at the service's first sample nothing is attributed.
            info.CpuDelta = dt > 0 ? Math.Max(0, cpu - info.LastCpuTime) : 0;
            info.IoDelta = dt > 0 ? Math.Max(0, io - info.LastIoBytes) : 0;
            if (isNew && dt > 0) { info.CpuDelta = cpu; info.IoDelta = io; }
            if (!isNew && dt > 0)
            {
                info.CpuPercent = Math.Max(0, (cpu - info.LastCpuTime) / (dt * 1e7 * _cpuCount) * 100.0);
                info.IoBytesPerSec = Math.Max(0, (io - info.LastIoBytes) / dt);
            }
            info.LastCpuTime = cpu;
            info.LastIoBytes = io;
            info.Threads = *(int*)(p + OffThreads);
            if (pid != 0) // the idle process's per-CPU threads are not work waiting for a processor
            {
                uint entry = *(uint*)(p + OffNext);
                long room = (entry != 0 ? entry : _buffer + _capacity - p) - OffThreadArray;
                int threads = (int)Math.Clamp(Math.Min(info.Threads, room / ThreadStride), 0, int.MaxValue);
                byte* t = p + OffThreadArray;
                for (int i = 0; i < threads; i++, t += ThreadStride)
                    if (*(uint*)(t + OffThreadState) == ThreadReady) ready++;
            }
            uint hard = *(uint*)(p + OffHardFaults);
            if (!isNew && hard > info.HardFaults) hardFaults += hard - info.HardFaults;
            info.Handles = *(int*)(p + OffHandles);
            info.BasePriority = *(int*)(p + OffBasePrio);
            info.WorkingSet = (long)*(nuint*)(p + OffWorkingSet);
            info.PrivateBytes = (long)*(nuint*)(p + OffPrivate);
            info.PrivateWorkingSet = *(long*)(p + OffPrivateWs);
            info.HardFaults = hard;
            info.CycleTime = *(long*)(p + OffCycles);
            info.PeakVirtualSize = (long)*(nuint*)(p + OffPeakVirtual);
            info.VirtualSize = (long)*(nuint*)(p + OffVirtual);
            info.PageFaults = *(uint*)(p + OffPageFaults);
            info.PeakWorkingSet = (long)*(nuint*)(p + OffPeakWs);
            info.PagedPool = (long)*(nuint*)(p + OffPagedPool);
            info.NonPagedPool = (long)*(nuint*)(p + OffNonPagedPool);
            info.PeakPrivateBytes = (long)*(nuint*)(p + OffPeakPagefile);
            info.UserTime = *(long*)(p + OffUser);
            info.KernelTime = *(long*)(p + OffKernel);
            info.ReadOperations = *(long*)(p + OffReadOps);
            info.WriteOperations = *(long*)(p + OffWriteOps);
            info.OtherOperations = *(long*)(p + OffOtherOps);
            info.ReadBytes = *(long*)(p + OffReadXfer);
            info.WriteBytes = *(long*)(p + OffWriteXfer);
            info.OtherBytes = *(long*)(p + OffOtherXfer);
            info.LastSeenTick = _tick;

            _list.Add(info);
            _byPid[pid] = info;
            if (pid != 0) totalIo += info.IoBytesPerSec;

            uint next = *(uint*)(p + OffNext);
            if (next == 0) break;
            p += next;
        }

        // Resolve parents of new processes: the parent must exist and be older than the child (PID reuse).
        foreach (var info in _new)
        {
            int parentPid = info.Parent?.Pid ?? 0;
            if (parentPid != info.Key.Pid && _byPid.TryGetValue(parentPid, out var parent) &&
                parent.Key.CreateTime <= info.Key.CreateTime)
            {
                info.Parent = parent.Key;
                info.ParentName = parent.Name;
                info.GameRoot = parent.GameRoot;
            }
            else
            {
                info.Parent = null;
                foreach (var recent in _recentParents)
                    if (recent.Key.Pid == parentPid && recent.Key.CreateTime <= info.Key.CreateTime && recent.Expires > Environment.TickCount64)
                    { info.Parent = recent.Key; info.GameRoot = recent.GameRoot; break; }
                info.HasUnknownParent = parentPid != 0;
            }
        }
        _new.Clear();

        foreach (var (k, v) in _live)
            if (v.LastSeenTick != _tick) _gone.Add(k);
        foreach (var k in _gone)
        {
            _recentParents[_nextParent++ % _recentParents.Length] = (k, _live[k].GameRoot, Environment.TickCount64 + 30_000);
            if (_byPid.TryGetValue(k.Pid, out var old) && old.Key == k) _byPid.Remove(k.Pid);
            _live.Remove(k);
        }
        _gone.Clear();

        TotalIoBytesPerSec = totalIo;
        ReadyThreads = ready;
        // A rate over a few milliseconds (an early tick) is noise: add up until half a second has passed.
        _faults += hardFaults;
        _faultSeconds += dt;
        if (_faultSeconds >= 0.5)
        {
            HardFaultsPerSec = _faults / _faultSeconds;
            _faults = 0;
            _faultSeconds = 0;
        }
        SampleSystem();
    }

    private void SampleSystem()
    {
        CpuAvailableDelta = CpuBusyDelta = 0;
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long dIdle = idle - _lastIdle, dTotal = kernel - _lastKernel + (user - _lastUser);
            if (_lastKernel != 0 && dTotal > 0) SystemCpuPercent = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            CpuAvailableDelta = _lastKernel != 0 && dTotal > 0 ? dTotal : 0;
            CpuBusyDelta = _lastKernel != 0 && dTotal > 0 ? Math.Clamp(dTotal - dIdle, 0, dTotal) : 0;
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
        }
        else _lastKernel = 0; // the next good reading only sets a new baseline, so system and process deltas stay aligned

        Native.MEMORYSTATUSEX m = default;
        m.Length = (uint)sizeof(Native.MEMORYSTATUSEX);
        if (Native.GlobalMemoryStatusEx(&m))
        {
            MemoryTotal = m.TotalPhys;
            MemoryUsed = m.TotalPhys - m.AvailPhys;
        }
    }

    private static string ReadName(byte* entry)
    {
        int len = *(ushort*)(entry + OffNameLen) / 2;
        char* buf = (char*)*(nint*)(entry + OffNameBuf);
        return len == 0 || buf == null ? "" : new string(buf, 0, len);
    }

    private static string? QueryPath(ProcessKey key)
    {
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (h == 0) return null;
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != key.CreateTime) return null;
            char* buf = stackalloc char[1024];
            uint size = 1024;
            return Native.QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }
}
