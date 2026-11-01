using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Corewright.Service.Windows;

/// <summary>Configuration Manager interface notifications work in session 0 without a window.
/// The native callback only marks dirty generations; enumeration stays on the demand-driven worker.</summary>
internal sealed unsafe partial class DeviceNotifications : IDisposable
{
    private readonly nint[] _registrations = new nint[4];
    private readonly DeviceMonitor _devices;
    private readonly ILogger _log;
    private GCHandle _context;

    public DeviceNotifications(DeviceMonitor devices, ILogger log)
    {
        _devices = devices; _log = log;
        _context = GCHandle.Alloc(this);
        ReadOnlySpan<Guid> classes = [
            new("53f56307-b6bf-11d0-94f2-00a0c91efb8b"), // disk
            new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b"), // volume
            new("5b45201d-f2f2-4f3b-85bb-30ff1f953599"), // display adapter
            new("cac88484-7515-4c03-82e6-71a87abac361"), // network interface
        ];
        for (int i = 0; i < classes.Length; i++)
        {
            var filter = new Filter { Size = (uint)sizeof(Filter), ClassGuid = classes[i] };
            uint result = CM_Register_Notification(&filter, GCHandle.ToIntPtr(_context), &Changed, out _registrations[i]);
            if (result != 0) _log.LogWarning("Device notification registration {Class} failed: CONFIGRET {Code}", classes[i], result);
        }
    }

    // CM_NOTIFY_FILTER contains a union whose largest member is WCHAR InstanceId[200].
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct Filter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint Type; // DEVICEINTERFACE = 0
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Changed(nint notification, nint context, int action, void* data, uint size)
    {
        if (action is 0 or 1 && GCHandle.FromIntPtr(context).Target is DeviceNotifications owner)
            owner._devices.MarkInventoryDirty();
        return 0;
    }

    public void Dispose()
    {
        bool unregistered = true;
        for (int i = 0; i < _registrations.Length; i++)
        {
            if (_registrations[i] == 0) continue;
            uint result = CM_Unregister_Notification(_registrations[i]);
            if (result != 0) { unregistered = false; _log.LogWarning("Device notification unregister failed: CONFIGRET {Code}", result); }
            else _registrations[i] = 0;
        }
        // Unregister waits for callbacks. Never free their context if a registration remains live.
        if (unregistered && _context.IsAllocated) _context.Free();
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(Filter* filter, nint context,
        delegate* unmanaged[Stdcall]<nint, nint, int, void*, uint, uint> callback, out nint notification);
    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint notification);
}
