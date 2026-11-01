// Shared by the installer and the dashboard's embedded elevated bootstrap.
// C# 5 / Windows PowerShell 5.1 compatible; never executed while packaging.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

public static class SecureFiles
{
    public static bool MachinePathReady = true;
    const uint ReadAttributes = 0x80, ReadControl = 0x20000;
    const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    [StructLayout(LayoutKind.Sequential)]
    struct Info
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, HighSize, LowSize, Links, HighIndex, LowIndex;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes
    {
        public int Length; public IntPtr Root, Name; public uint Flags; public IntPtr Security, Quality;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoStatus { public IntPtr Status, Information; }
    [DllImport("ntdll.dll")]
    static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus status, IntPtr size, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaSize);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, IntPtr size);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint flags, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
    // Resolve exactly one component relative to a held directory object, never
    // re-traverse its pathname. WRITE_ATTRIBUTES handles are not excluded by
    // share modes, so pathname-only traversal would still leave a reparse race.
    static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, bool create, bool directory, IntPtr security)
    {
        if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(new char[] { '\\', '/', ':', '\0' }) >= 0)
            throw new IOException("Invalid path component.");
        IntPtr text = Marshal.StringToHGlobalUni(name);
        IntPtr unicode = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
        try
        {
            UnicodeString value = new UnicodeString();
            value.Length = checked((ushort)(name.Length * 2)); value.MaximumLength = value.Length; value.Buffer = text;
            Marshal.StructureToPtr(value, unicode, false);
            ObjectAttributes attributes = new ObjectAttributes();
            attributes.Length = Marshal.SizeOf(typeof(ObjectAttributes));
            attributes.Root = parent.DangerousGetHandle(); attributes.Name = unicode;
            attributes.Flags = 0x40; // OBJ_CASE_INSENSITIVE
            attributes.Security = security;
            IoStatus io;
            SafeFileHandle handle;
            int status = NtCreateFile(out handle, access | ReadAttributes | 0x100000, ref attributes,
                out io, IntPtr.Zero, 0x80, 1, create ? 2u : 1u,
                0x00204020u | (directory ? 1u : 0u), IntPtr.Zero, 0);
            if (status < 0) { if (handle != null) handle.Dispose(); throw new Win32Exception((int)RtlNtStatusToDosError(status)); }
            try
            {
                Info info;
                if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception();
                if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) == 0 && info.Links != 1))
                    throw new IOException("Reparse points and multiply-linked files are not supported.");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
    }

    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr data, uint size);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct RenameInfo { public uint Flags; public IntPtr Root; public uint Length; public char First; }


    static Info Information(SafeFileHandle handle)
    {
        Info info;
        if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception();
        return info;
    }

    static void Rename(SafeFileHandle handle, string destination)
    {
        // The Win32 wrapper resolves relative names against its current directory.
        // Supply an absolute name and a terminating NUL as required by FileName,
        // with FileNameLength excluding the terminator. The parent is pinned.
        byte[] name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        int offset = (int)Marshal.OffsetOf(typeof(RenameInfo), "First");
        int size = offset + name.Length + 2;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size); // ReplaceIfExists = false, RootDirectory = NULL
            Marshal.WriteInt32(buffer, (int)Marshal.OffsetOf(typeof(RenameInfo), "Length"), name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, IntPtr data, uint size);

    static IEnumerable<string> Children(SafeFileHandle directory)
    {
        const int size = 65536;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int kind = 11; // FileIdBothDirectoryRestartInfo, then FileIdBothDirectoryInfo
            while (GetFileInformationByHandleEx(directory, kind, buffer, size))
            {
                kind = 10;
                int offset = 0;
                while (true)
                {
                    // FILE_ID_BOTH_DIR_INFO has its UTF-16 name at byte 104.
                    int length = Marshal.ReadInt32(buffer, offset + 60);
                    int next = Marshal.ReadInt32(buffer, offset);
                    if (length < 0 || (length & 1) != 0 || offset > size - 104 - length)
                        throw new IOException("Invalid directory information.");
                    string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 104), length / 2);
                    if (name != "." && name != "..") yield return name;
                    if (next == 0) break;
                    if (next < 104 || next > size - offset - 104) throw new IOException("Invalid directory offset.");
                    offset += next;
                }
            }
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static void CopyTree(SafeFileHandle source, SafeFileHandle destination)
    {
        foreach (string name in Children(source))
        {
            using (SafeFileHandle handle = OpenRelative(source, name, 0x80000000, false, false, IntPtr.Zero))
            {
                if ((Information(handle).Attributes & 0x10) != 0)
                {
                    using (SafeFileHandle target = NewDirectory(destination, name, true, 0x80000000)) CopyTree(handle, target);
                }
                else
                {
                    using (FileStream input = new FileStream(handle, FileAccess.Read))
                    using (SafeFileHandle target = NewProtectedObject(destination, name, true, 0x40000000, false))
                    using (FileStream output = new FileStream(target, FileAccess.Write))
                        input.CopyTo(output);
                }
            }
        }
    }

    public static string ReplaceMachineData(string directory)
    {
        string full = Path.GetFullPath(directory);
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corewright");
        if (!String.Equals(full, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected machine data directory.");
        string parent = Path.GetDirectoryName(full);
        using (PinnedPath ancestors = new PinnedPath(parent, true, 0))
        {
            string fresh = full + "-fresh-" + Guid.NewGuid().ToString("N");
            string backup = full + "-preserved-" + Guid.NewGuid().ToString("N");
            // Request DELETE on this handle so the object can be renamed without
            // reopening its pathname. Other handles cannot rename/write it.
            using (SafeFileHandle replacement = NewDirectory(ancestors.Leaf, Path.GetFileName(fresh), true, 0x10000 | 0x80000000))
            {
                uint attributes = GetFileAttributes(full);
                if (attributes == UInt32.MaxValue)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 2 && error != 3) throw new Win32Exception(error);
                    Rename(replacement, full); // Fails if anyone created the name in the meantime.
                    return null;
                }
                using (SafeFileHandle original = OpenRelative(ancestors.Leaf, Path.GetFileName(full), 0x10000 | 0x80000000, false, false, IntPtr.Zero))
                {
                    if ((Information(original).Attributes & 0x10) == 0) throw new IOException("Machine data must be a directory.");
                    CopyTree(original, replacement);
                    // Old WRITE_DAC handles can change only the preserved objects;
                    // no old object or security descriptor becomes trusted machine data.
                    Rename(original, backup);
                    MachinePathReady = false;
                    try
                    {
                        Rename(replacement, full);
                        MachinePathReady = true;
                    }
                    catch
                    {
                        // A low-privilege creator may win the name in the gap.
                        // Never overwrite it or start SYSTEM against that object.
                        try { Rename(original, full); MachinePathReady = true; }
                        catch { }
                        throw;
                    }
                }
                return backup;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern uint GetFileAttributes(string path);

    static bool Trusted(string sid)
    {
        return sid == "S-1-5-18" || sid == "S-1-5-32-544" ||
            sid == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    }

    static void RequireTrusted(SafeFileHandle handle)
    {
        IntPtr owner, group, dacl, sacl, descriptor;
        uint error = GetSecurityInfo(handle, 1, 5, out owner, out group, out dacl, out sacl, out descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            byte[] bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            RawSecurityDescriptor security = new RawSecurityDescriptor(bytes, 0);
            if (security.Owner == null || !Trusted(security.Owner.Value) || security.DiscretionaryAcl == null)
                throw new IOException("Staging ancestor must be owned and controlled by administrators.");
            foreach (GenericAce entry in security.DiscretionaryAcl)
            {
                QualifiedAce ace = entry as QualifiedAce;
                if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
                // Unknown applicable ACEs fail closed. Ignoring deny ACEs can only over-reject.
                if (ace == null) throw new IOException("Unsupported ancestor ACL.");
                if (ace.AceQualifier != AceQualifier.AccessAllowed || Trusted(ace.SecurityIdentifier.Value)) continue;
                // Creating a new child is safe (CREATE_NEW); replacing the ancestor,
                // deleting its children, setting reparse data or rewriting its ACL is not.
                const int unsafeRights = 0x500D0040; // GENERIC_ALL/WRITE, DELETE/WRITE_DAC/OWNER, DELETE_CHILD
                if ((ace.AccessMask & unsafeRights) != 0)
                    throw new IOException("An unprivileged principal can redirect a staging ancestor.");
            }
        }
        finally { LocalFree(descriptor); }
    }

    public sealed class PinnedPath : IDisposable
    {
        readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
        public SafeFileHandle Leaf { get { return handles[handles.Count - 1]; } }
        public PinnedPath(string path, bool trusted, uint leafAccess)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (root.Length != 3 || root[1] != ':' || full.IndexOf(':', 2) >= 0)
                    throw new IOException("Only local drive paths are supported.");
                string current = root;
                string[] parts = full.Substring(root.Length).Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = -1; i < parts.Length; i++)
                {
                    if (i >= 0) current = Path.Combine(current, parts[i]);
                    bool leaf = i == parts.Length - 1;
                    SafeFileHandle handle = i < 0
                        ? CreateFile(current, ReadAttributes | (trusted ? ReadControl : 0) | (leaf ? leafAccess : 0),
                            1, IntPtr.Zero, 3, OpenReparsePoint | BackupSemantics, IntPtr.Zero)
                        : OpenRelative(Leaf, parts[i], (trusted ? ReadControl : 0) | (leaf ? leafAccess : 0), false, false, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
                    handles.Add(handle);
                    Info info;
                    if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception();
                    if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) == 0 && info.Links != 1))
                        throw new IOException("Reparse points and multiply-linked files are not supported.");
                    if (!leaf && (info.Attributes & 0x10) == 0) throw new IOException("Invalid path ancestor.");
                    if (trusted) RequireTrusted(handle);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            handles.Clear();
        }
    }

    public static void CreateProtectedDirectory(string path, bool usersRead)
    {
        using (PinnedPath parent = new PinnedPath(Path.GetDirectoryName(path), true, 0))
        using (SafeFileHandle created = NewDirectory(parent.Leaf, Path.GetFileName(path), usersRead, 0)) { }
    }

    public static SafeFileHandle CreateProtectedFile(string path)
    {
        using (PinnedPath parent = new PinnedPath(Path.GetDirectoryName(path), true, 0))
            return NewProtectedObject(parent.Leaf, Path.GetFileName(path), false, 0x40000000, false);
    }

    static SafeFileHandle NewDirectory(SafeFileHandle parent, string name, bool usersRead, uint access)
    {
        return NewProtectedObject(parent, name, usersRead, access, true);
    }

    static SafeFileHandle NewProtectedObject(SafeFileHandle parent, string name, bool usersRead, uint access, bool directory)
    {
        IntPtr descriptor;
        string sddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + (usersRead ? "(A;OICI;0x1200a9;;;BU)" : "");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, IntPtr.Zero)) throw new Win32Exception();
        try
        {
            // No adoption, no post-creation ACL window: ERROR_ALREADY_EXISTS is fatal.
            return OpenRelative(parent, name, access, true, directory, descriptor);
        }
        finally { LocalFree(descriptor); }
    }
}
