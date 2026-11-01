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

    private readonly Dictionary<ProcessKey, ProcessInfo> _live = [];
    private readonly Dictionary<int, ProcessInfo> _byPid = [];
    private readonly List<ProcessInfo> _list = [];
    private readonly List<ProcessInfo> _new = [];
    private readonly List<ProcessKey> _gone = [];
    private readonly int _cpuCount = (int)Math.Max(1, Native.GetActiveProcessorCount(Native.ALL_PROCESSOR_GROUPS));
    private readonly int _selfPid = Environment.ProcessId;

    private byte* _buffer;
    private uint _capacity;
    private long _lastTimestamp;
    private long _tick;
    private long _lastIdle, _lastKernel, _lastUser;

    public ProcessSampler()
    {
        _capacity = 1 << 20;
        _buffer = (byte*)NativeMemory.Alloc(_capacity);
    }

    public IReadOnlyList<ProcessInfo> Processes => _list;
    public double SystemCpuPercent { get; private set; }
    public double TotalIoBytesPerSec { get; private set; }
    public ulong MemoryTotal { get; private set; }
    public ulong MemoryUsed { get; private set; }

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
        if (status < 0) return;

        long now = Stopwatch.GetTimestamp();
        double dt = _lastTimestamp == 0 ? 0 : (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        _lastTimestamp = now;
        _tick++;
        _list.Clear();
        _byPid.Clear();
        double totalIo = 0;

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
                    Path = pid is 0 or 4 ? null : QueryPath(pid),
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
            long io = *(long*)(p + OffReadXfer) + *(long*)(p + OffWriteXfer) + *(long*)(p + OffOtherXfer);
            if (!isNew && dt > 0)
            {
                info.CpuPercent = Math.Max(0, (cpu - info.LastCpuTime) / (dt * 1e7 * _cpuCount) * 100.0);
                info.IoBytesPerSec = Math.Max(0, (io - info.LastIoBytes) / dt);
            }
            info.LastCpuTime = cpu;
            info.LastIoBytes = io;
            info.Threads = *(int*)(p + OffThreads);
            info.Handles = *(int*)(p + OffHandles);
            info.BasePriority = *(int*)(p + OffBasePrio);
            info.WorkingSet = (long)*(nuint*)(p + OffWorkingSet);
            info.PrivateBytes = (long)*(nuint*)(p + OffPrivate);
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
            }
            else
            {
                info.Parent = null;
            }
        }
        _new.Clear();

        foreach (var (k, v) in _live)
            if (v.LastSeenTick != _tick) _gone.Add(k);
        foreach (var k in _gone) _live.Remove(k);
        _gone.Clear();

        TotalIoBytesPerSec = totalIo;
        SampleSystem();
    }

    private void SampleSystem()
    {
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long dIdle = idle - _lastIdle, dTotal = kernel - _lastKernel + (user - _lastUser);
            if (_lastKernel != 0 && dTotal > 0) SystemCpuPercent = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
        }

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

    private static string? QueryPath(int pid)
    {
        nint h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
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
