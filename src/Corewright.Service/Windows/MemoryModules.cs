using System.Runtime.InteropServices;
using System.Text;

namespace Corewright.Service.Windows;

/// <param name="ConfiguredMts">The speed the module actually runs at (e.g. with EXPO/XMP off it is lower than rated).</param>
public sealed record MemoryModuleDto(string Slot, long SizeMb, string Type, int RatedMts, int ConfiguredMts, string Manufacturer, string Part);

/// <summary>
/// Installed memory modules from the firmware's SMBIOS table (type 17, Memory Device), read once on first request
/// with GetSystemFirmwareTable('RSMB'). No driver, no WMI; works without administrator rights.
/// </summary>
internal static unsafe partial class MemoryModules
{
    private static MemoryModuleDto[]? _cache;

    public static MemoryModuleDto[] Read() => _cache ??= ReadOnce();

    private static MemoryModuleDto[] ReadOnce()
    {
        const uint Rsmb = 0x52534D42; // 'RSMB'
        try
        {
            uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
            if (size == 0 || size > 1 << 20) return [];
            var buffer = new byte[size];
            fixed (byte* b = buffer)
            {
                if (GetSystemFirmwareTable(Rsmb, 0, b, size) != size) return [];
                // RawSMBIOSData: 4 bytes of version info, then the table length and the table.
                uint length = *(uint*)(b + 4);
                if (8 + (long)length > size) return [];
                return Parse(b + 8, (int)length);
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return []; }
    }

    private static MemoryModuleDto[] Parse(byte* table, int length)
    {
        var list = new List<MemoryModuleDto>();
        int off = 0;
        while (off + 4 <= length)
        {
            byte type = table[off];
            int formatted = table[off + 1];
            if (formatted < 4 || off + formatted > length) break;
            // The strings follow the formatted area and end with a double zero.
            int strings = off + formatted;
            int end = strings;
            while (end + 1 < length && !(table[end] == 0 && table[end + 1] == 0)) end++;
            if (type == 127) break; // end of table
            if (type == 17 && formatted >= 0x15)
            {
                byte* s = table + off;
                ushort rawSize = *(ushort*)(s + 0x0C);
                long mb = rawSize switch
                {
                    0 or 0xFFFF => 0,
                    0x7FFF when formatted >= 0x20 => *(uint*)(s + 0x1C) & 0x7FFFFFFF,
                    _ => (rawSize & 0x8000) != 0 ? (rawSize & 0x7FFF) / 1024 : rawSize,
                };
                if (mb > 0)
                {
                    string Str(int fieldOffset) => fieldOffset < formatted ? StringAt(table, strings, end, s[fieldOffset]) : "";
                    string slot = Str(0x10);
                    string bank = Str(0x11);
                    int rated = formatted >= 0x17 ? *(ushort*)(s + 0x15) : 0;
                    int configured = formatted >= 0x22 ? *(ushort*)(s + 0x20) : 0;
                    list.Add(new MemoryModuleDto(
                        slot.Length > 0 ? slot : bank,
                        mb,
                        MemoryType(s[0x12]),
                        rated,
                        configured,
                        Str(0x17),
                        Str(0x1A)));
                }
            }
            off = end + 2;
        }
        return [.. list];
    }

    private static string StringAt(byte* table, int start, int end, byte index)
    {
        if (index == 0) return "";
        int pos = start;
        for (int i = 1; pos < end; i++)
        {
            int len = 0;
            while (pos + len < end && table[pos + len] != 0) len++;
            if (i == index) return Encoding.ASCII.GetString(table + pos, len).Trim();
            pos += len + 1;
        }
        return "";
    }

    private static string MemoryType(byte t) => t switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => "",
    };

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint provider, uint id, byte* buffer, uint size);
}
