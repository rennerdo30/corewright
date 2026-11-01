namespace Corewright.Service.Windows;

/// <summary>One background thread, one outstanding pass, no queue and no timer. A driver stuck in
/// synchronous I/O can occupy only this worker; the engine, HTTP requests and shutdown never join it.</summary>
internal sealed class DeviceProbeWorker : IDisposable
{
    private readonly DeviceMonitor _devices;
    private readonly Lock _sync;
    private readonly ILogger _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private volatile bool _stopping;
    private bool _busy, _cleanupNeeded;
    private long _lastRequest, _nextPassMs;
    private readonly Func<bool> _clientActive;
    private GpuMonitor? _gpu;

    public DeviceProbeWorker(DeviceMonitor devices, Lock sync, ILogger log)
    {
        _devices = devices; _sync = sync; _log = log;
        _clientActive = ClientActive;
        new Thread(Run) { IsBackground = true, Name = "Device probes" }.Start();
    }

    public void Request(bool active)
    {
        lock (_gate)
        {
            if (_stopping) return;
            long now = Environment.TickCount64;
            if (active) { _lastRequest = now; _cleanupNeeded = true; }
            if (_busy || (!active && (!_cleanupNeeded || now - _lastRequest <= 60_000))) return;
            if (active && now < _nextPassMs) return;
            _nextPassMs = now + 1000;
            _busy = true;
            _wake.Set();
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                lock (_gate) { if (_stopping) return; }
                try
                {
                    bool active = ClientActive();
                    if (active)
                    {
                        _devices.RefreshProbes(_clientActive);
                        if (ClientActive()) { _gpu ??= new(); _gpu.Sample(); }
                    }
                    active = ClientActive();
                    if (!active) { _gpu?.Dispose(); _gpu = null; }
                    _devices.SampleProbes(_gpu, active, Environment.TickCount64, _clientActive);
                    lock (_sync)
                    {
                        // A slow pass must not publish a fresh-looking sample after client demand ended.
                        if (!_stopping && (ClientActive() || !active))
                            _devices.Publish(_gpu, active);
                    }
                }
                catch (Exception e) { _log.LogWarning("Device probe pass failed: {Message}", e.Message); }
                finally
                {
                    lock (_gate)
                    {
                        _busy = false;
                        // Cleanup after a slow pass is still required; only a completed idle pass clears it.
                        if (_gpu is null && Environment.TickCount64 - _lastRequest > 60_000) _cleanupNeeded = false;
                    }
                }
            }
        }
        finally
        {
            _gpu?.Dispose();
            _devices.CloseProbes();
            lock (_gate) _wake.Dispose();
        }
    }

    private bool ClientActive() => !_stopping && Environment.TickCount64 - Volatile.Read(ref _lastRequest) < 5000;

    public void Dispose()
    {
        lock (_gate) { if (_stopping) return; _stopping = true; _wake.Set(); }
    }
}
