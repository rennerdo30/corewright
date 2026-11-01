namespace Corewright.Service.Windows;

/// <summary>A machine-wide, crash-released lease. File sharing is enforced by Windows, across sessions.
/// Unlike a mutex it can be released by the sampler's continuation thread without a parked owner thread.</summary>
internal sealed class NetworkOwnership : IDisposable
{
    private FileStream? _lease;
    private string? _error;
    public static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corewright", "Network");
    public bool TryAcquire(out string? error)
    {
        if (_lease is not null) { error = null; return true; }
        _error = null; // A later demand/rule attempt may acquire a lease released by another instance.
        if (!Environment.IsPrivilegedProcess)
            _error = "needs administrator (needs the installed service)";
        else
        {
            try
            {
                string folder = StateDirectory;
                Directory.CreateDirectory(folder);
                // Never delete/replace this file: that would allow two owners of different file objects.
                _lease = new FileStream(Path.Combine(folder, "network-owner.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { _error = "network enforcement unavailable: another Corewright instance owns the machine lease, or its lock is inaccessible: " + e.Message; }
        }
        error = _error;
        return _lease is not null;
    }
    public void Dispose() { _lease?.Dispose(); _lease = null; }
}
