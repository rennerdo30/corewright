using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Corewright.Service.Windows;

/// <summary>One dynamic WFP session. BFE removes its sublayer and filters on handle close or crash.</summary>
internal sealed unsafe partial class NetworkBlocker : IDisposable
{
    private nint _engine;
    private readonly Guid _sublayer = Guid.NewGuid();
    private static readonly Guid[] Layers = [new("c38d57d1-05a7-4c33-904f-7fbceee60e82"),
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), new("a3b42c97-9f04-4672-b87e-cee9c483257f")];
    private readonly Dictionary<string, ulong[]> _filters = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, bool blocked)
    {
        if (blocked == _filters.ContainsKey(path)) return;
        if (_engine == 0)
        {
            fixed (char* name = "Corewright network blocking")
            {
                var session = new Session { Name = name, Flags = 1, Timeout = 2000 };
                Check(FwpmEngineOpen0(null, 10, 0, ref session, out _engine));
                var layer = new Sublayer { Key = _sublayer, Name = name, Weight = 0xFFFF };
                try { Check(FwpmSubLayerAdd0(_engine, ref layer, 0)); }
                catch { Dispose(); throw; }
            }
        }
        Check(FwpmTransactionBegin0(_engine, 0));
        nint appId = 0;
        try
        {
            if (blocked)
            {
                Check(FwpmGetAppIdFromFileName0(path, out appId));
                var condition = new Condition { Key = new("d78e1e87-8644-4ea5-9437-d809ecefc971"), Value = new() { Type = 12, Data = appId } };
                var ids = new ulong[Layers.Length];
                fixed (char* name = "Corewright block program")
                    for (int i = 0; i < Layers.Length; i++)
                    {
                        var filter = new Filter { Name = name, Layer = Layers[i], Sublayer = _sublayer,
                            Count = 1, Conditions = &condition, Action = 0x1001 };
                        Check(FwpmFilterAdd0(_engine, ref filter, 0, out ids[i]));
                    }
                Check(FwpmTransactionCommit0(_engine));
                _filters.Add(path, ids);
            }
            else
            {
                foreach (ulong id in _filters[path]) Check(FwpmFilterDeleteById0(_engine, id));
                Check(FwpmTransactionCommit0(_engine));
                _filters.Remove(path);
            }
        }
        catch { FwpmTransactionAbort0(_engine); throw; }
        finally { if (appId != 0) FwpmFreeMemory0(ref appId); }
    }
    private static void Check(uint result) { if (result != 0) throw new Win32Exception(unchecked((int)result)); }
    public void Dispose() { if (_engine != 0) Check(FwpmEngineClose0(_engine)); _engine = 0; _filters.Clear(); }
    public bool Closed => _engine == 0;

    // Layouts from the Windows SDK fwpmtypes.h; pointer-sized members keep the native alignment.
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Value { public uint Type; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Condition { public Guid Key; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Session
    { public Guid Key; public char* Name; public nint Description; public uint Flags, Timeout, Pid; public nint Sid, User; public int Kernel; }
    [StructLayout(LayoutKind.Sequential)] private struct Sublayer
    { public Guid Key; public char* Name; public nint Description; public uint Flags; public nint Provider; public Blob Data; public ushort Weight; }
    [StructLayout(LayoutKind.Sequential)] private struct Filter
    {
        public Guid Key; public char* Name; public nint Description; public uint Flags; public nint Provider;
        public Blob Data; public Guid Layer, Sublayer; public Value Weight; public uint Count; public Condition* Conditions;
        public uint Action; public Guid ActionKey; public ulong Context, ContextHigh; public nint Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmEngineOpen0(string? server, uint auth, nint identity, ref Session session, out nint engine);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmEngineClose0(nint engine);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmSubLayerAdd0(nint engine, ref Sublayer layer, nint security);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmFilterAdd0(nint engine, ref Filter filter, nint security, out ulong id);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmFilterDeleteById0(nint engine, ulong id);
    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmGetAppIdFromFileName0(string path, out nint blob);
    [LibraryImport("fwpuclnt.dll")] private static partial void FwpmFreeMemory0(ref nint memory);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmTransactionBegin0(nint engine, uint flags);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmTransactionCommit0(nint engine);
    [LibraryImport("fwpuclnt.dll")] private static partial uint FwpmTransactionAbort0(nint engine);
}
