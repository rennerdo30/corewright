using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;

namespace Corewright.Service.Windows;

/// <summary>NativeAOT WMI client. No runtime COM interop, System.Management or child processes.</summary>
internal sealed unsafe partial class QosPolicies
{
    private const string Class = "MSFT_NetQosPolicySettingData";
    private readonly Dictionary<string, (long Upload, long Dscp)> _owned = new(StringComparer.OrdinalIgnoreCase);
    public void Cleanup() { using var wmi = new Wmi(); wmi.DeleteMatching(); }
    public void RemoveOwned() { foreach (string path in _owned.Keys.ToArray()) Set(path, 0, -1); }
    public void Set(string path, long upload, long dscp)
    {
        if (!_owned.TryGetValue(path, out var old)) old = (0, -1);
        if (old == (upload, dscp)) return;
        string name = "Corewright-" + Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(path.ToUpperInvariant())))[..32];
        using var wmi = new Wmi();
        // One combined policy: Windows chooses a policy rather than merging independent throttle/DSCP policies.
        using var policy = wmi.Find(name);
        if (upload == 0 && dscp < 0)
        {
            if (policy is not null) wmi.Delete(policy);
            _owned.Remove(path);
            return;
        }
        using var type = wmi.Get(Class);
        Check(type.Api.SpawnInstance(0, out nint instance));
        using var fresh = new Com<IWbemObject>(instance);
        var target = policy ?? fresh;
        Put(target, "Name", name); Put(target, "AppPathNameMatchCondition", path);
        Put(target, "DSCPAction", dscp); Put(target, "ThrottleRateAction", upload.ToString(CultureInfo.InvariantCulture));
        Check(wmi.Services.Api.PutInstance(target.Pointer, policy is null ? 2 : 1, wmi.Context.Pointer, 0));
        _owned[path] = (upload, dscp);
    }
    private static void Put(Com<IWbemObject> obj, string name, string text)
    {
        var v = new Variant { Type = 8, Data = Marshal.StringToBSTR(text) };
        try { Check(obj.Api.Put(name, 0, ref v, 0)); } finally { Marshal.FreeBSTR((nint)v.Data); }
    }
    private static void Put(Com<IWbemObject> obj, string name, long number)
    { var v = new Variant { Type = 3, Data = number }; Check(obj.Api.Put(name, 0, ref v, 0)); }
    private static string Read(Com<IWbemObject> obj, string name)
    {
        Check(obj.Api.Get(name, 0, out var v, 0, 0));
        try { return v.Type == 8 ? Marshal.PtrToStringBSTR((nint)v.Data) : ""; }
        finally { VariantClear(ref v); }
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct Variant
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public long Data; }
    private sealed class Com<T> : IDisposable where T : class
    {
        private static readonly StrategyBasedComWrappers Wrappers = new();
        private readonly ComObject _object;
        public nint Pointer { get; }
        public T Api { get; }
        public Com(nint pointer)
        {
            Pointer = pointer;
            _object = (ComObject)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            Api = (T)(object)_object;
        }
        public void Dispose() { _object.FinalRelease(); Marshal.Release(Pointer); }
    }
    private sealed class Wmi : IDisposable
    {
        public readonly Com<IWbemServices> Services;
        public readonly Com<IWbemContext> Context;
        private readonly bool _uninitialize;
        public Wmi()
        {
            int hr = CoInitializeEx(0, 0); _uninitialize = hr >= 0;
            if (hr < 0 && hr != unchecked((int)0x80010106)) Check(hr);
            Com<IWbemContext>? context = null; Com<IWbemServices>? services = null;
            try
            {
                using var locator = Create<IWbemLocator>(new("4590f811-1d3a-11d0-891f-00aa004b2e24"), new("dc12a687-737f-11cf-884d-00aa004b2e24"));
                Check(locator.Api.ConnectServer(@"ROOT\StandardCimv2", 0, 0, 0, 0x80, 0, 0, out nint svc));
                services = new(svc);
                Check(CoSetProxyBlanket(svc, 10, 0, 0, 3, 3, 0, 0));
                context = Create<IWbemContext>(new("674b6698-ee92-11d0-ad71-00c04fd8fdff"), new("44aca674-e8fc-11d0-a07c-00c04fb68820"));
                var store = new Variant { Type = 8, Data = Marshal.StringToBSTR("ActiveStore") };
                try { Check(context.Api.SetValue("PolicyStore", 0, ref store)); } finally { Marshal.FreeBSTR((nint)store.Data); }
                Services = services; Context = context;
            }
            catch { context?.Dispose(); services?.Dispose(); if (_uninitialize) CoUninitialize(); throw; }
        }
        public Com<IWbemObject> Get(string path)
        { Check(Services.Api.GetObject(path, 0, Context.Pointer, out nint obj, 0)); return new(obj); }
        public Com<IWbemObject>? Find(string name)
        {
            using var items = Query("SELECT * FROM " + Class + " WHERE Name='" + name + "'");
            int hr = items.Api.Next(2000, 1, out nint obj, out uint count); Check(hr);
            if (hr == 0x40004 && count == 0) throw new TimeoutException("QoS enumeration timed out");
            return count == 0 ? null : new(obj);
        }
        private Com<IWbemEnum> Query(string query)
        { Check(Services.Api.ExecQuery("WQL", query, 0x30, Context.Pointer, out nint items)); return new(items); }
        public void Delete(Com<IWbemObject> obj)
        { Check(Services.Api.DeleteInstance(Read(obj, "__RELPATH"), 0, Context.Pointer, 0)); }
        public void DeleteMatching()
        {
            using var items = Query("SELECT * FROM " + Class + " WHERE Name LIKE 'Corewright-%'");
            while (true)
            {
                int hr = items.Api.Next(2000, 1, out nint obj, out uint count); Check(hr);
                if (count == 0) { if (hr == 0x40004) throw new TimeoutException("QoS enumeration timed out"); return; }
                using var policy = new Com<IWbemObject>(obj);
                // Do not touch persistent policies: every operation uses the same explicit ActiveStore context.
                if (Read(policy, "Name").StartsWith("Corewright-", StringComparison.Ordinal)) Delete(policy);
            }
        }
        public void Dispose() { Context.Dispose(); Services.Dispose(); if (_uninitialize) CoUninitialize(); }
    }
    private static Com<T> Create<T>(Guid clsid, Guid iid) where T : class
    { Check(CoCreateInstance(in clsid, 0, 1, in iid, out nint obj)); return new(obj); }
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);
    [LibraryImport("ole32.dll")] private static partial int CoSetProxyBlanket(nint proxy, uint auth, uint authz, nint principal, uint level, uint impersonation, nint identity, uint capabilities);
    [LibraryImport("oleaut32.dll")] private static partial int VariantClear(ref Variant variant);
}

// wbemcli.h vtable order. Unused entries retain their slot; all calls preserve HRESULTs.
[GeneratedComInterface, Guid("dc12a687-737f-11cf-884d-00aa004b2e24")]
internal partial interface IWbemLocator
{
    [PreserveSig] int ConnectServer([MarshalAs(UnmanagedType.BStr)] string ns, nint user, nint password, nint locale, int flags, nint authority, nint context, out nint services);
}
[GeneratedComInterface, Guid("9556dc99-828c-11cf-a37e-00aa003240c7")]
internal partial interface IWbemServices
{
    void OpenNamespace(); void CancelAsyncCall(); void QueryObjectSink();
    [PreserveSig] int GetObject([MarshalAs(UnmanagedType.BStr)] string path, int flags, nint context, out nint obj, nint result);
    void GetObjectAsync(); void PutClass(); void PutClassAsync(); void DeleteClass(); void DeleteClassAsync(); void CreateClassEnum(); void CreateClassEnumAsync();
    [PreserveSig] int PutInstance(nint obj, int flags, nint context, nint result);
    void PutInstanceAsync();
    [PreserveSig] int DeleteInstance([MarshalAs(UnmanagedType.BStr)] string path, int flags, nint context, nint result);
    void DeleteInstanceAsync(); void CreateInstanceEnum(); void CreateInstanceEnumAsync();
    [PreserveSig] int ExecQuery([MarshalAs(UnmanagedType.BStr)] string language, [MarshalAs(UnmanagedType.BStr)] string query, int flags, nint context, out nint items);
}
[GeneratedComInterface, Guid("dc12a681-737f-11cf-884d-00aa004b2e24")]
internal partial interface IWbemObject
{
    void GetQualifierSet();
    [PreserveSig] int Get([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, out QosPolicies.Variant value, nint type, nint flavor);
    [PreserveSig] int Put([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, ref QosPolicies.Variant value, int type);
    void Delete(); void GetNames(); void BeginEnumeration(); void Next(); void EndEnumeration(); void GetPropertyQualifierSet();
    void Clone(); void GetObjectText(); void SpawnDerivedClass();
    [PreserveSig] int SpawnInstance(int flags, out nint instance);
}
[GeneratedComInterface, Guid("027947e1-d731-11ce-a357-000000000001")]
internal partial interface IWbemEnum
{
    void Reset(); [PreserveSig] int Next(int timeout, uint count, out nint obj, out uint returned);
}
[GeneratedComInterface, Guid("44aca674-e8fc-11d0-a07c-00c04fb68820")]
internal partial interface IWbemContext
{
    void Clone(); void GetNames(); void BeginEnumeration(); void Next(); void EndEnumeration();
    [PreserveSig] int SetValue([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, ref QosPolicies.Variant value);
}
