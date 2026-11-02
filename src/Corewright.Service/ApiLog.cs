using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Corewright.Service;

/// <summary>One API call that changed something or was rejected. <c>Client</c> is the calling program, when Windows still
/// knew the connection's owner.</summary>
public sealed record ApiCallDto(long Time, string Method, string Path, int Status, int Ms, int? Pid, string? Client);

/// <summary>
/// The last 100 API calls that change something (PUT, POST, DELETE) and every rejected call, for Integrations → Recent
/// API calls (owner, 2026-11-02: "see the API calls … like a history … keep the last 10 or last 100 entries").
/// Reads are not logged: the window itself polls several endpoints every second and would push everything else out.
/// The tray's foreground reports are not logged either (one per window switch). Bodies and tokens are never stored.
/// Cost: nothing for reads; for a logged call one TCP table lookup to name the caller.
/// </summary>
internal sealed partial class ApiLog
{
    private const int Capacity = 100;
    private readonly ApiCallDto?[] _ring = new ApiCallDto?[Capacity];
    private readonly Lock _lock = new();
    private long _count;

    public static bool ShouldLog(string method, string path, int status) =>
        status is 401 or 421 ||
        (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) &&
         !path.Equals("/api/v1/session/foreground", StringComparison.OrdinalIgnoreCase));

    public void Add(ApiCallDto call)
    {
        lock (_lock) _ring[_count++ % Capacity] = call;
    }

    /// <summary>Newest first.</summary>
    public ApiCallDto[] Recent()
    {
        lock (_lock)
        {
            int n = (int)Math.Min(_count, Capacity);
            var list = new ApiCallDto[n];
            for (int i = 0; i < n; i++) list[i] = _ring[(_count - 1 - i) % Capacity]!;
            return list;
        }
    }

    /// <summary>The process that owns the client end of a loopback connection to the API, or null.</summary>
    public static unsafe int? OwnerOf(int clientPort, int serverPort)
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
        uint size = 0;
        GetExtendedTcpTable(null, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        if (size == 0 || size > 16 << 20) return null;
        var buffer = new byte[size + 1024];
        size = (uint)buffer.Length;
        fixed (byte* b = buffer)
        {
            if (GetExtendedTcpTable(b, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return null;
            uint rows = *(uint*)b;
            // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owning pid.
            for (uint i = 0; i < rows; i++)
            {
                if (4 + (i + 1) * 24 > size) break;
                uint* row = (uint*)(b + 4 + i * 24);
                int local = BinaryPrimitives.ReverseEndianness((ushort)row[2]);
                int remote = BinaryPrimitives.ReverseEndianness((ushort)row[4]);
                if (local == clientPort && remote == serverPort) return (int)row[5];
            }
        }
        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
