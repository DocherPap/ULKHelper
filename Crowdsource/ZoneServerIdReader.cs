using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace ULKHelper.Crowdsource;

// Read-only Zone Init identity. Patterns/packing verified against the service author's
// InstanceFinder and OmenTools; see docs/CROWDSOURCE.md. No game hooks or writes.
internal sealed class ZoneServerIdReader(ISigScanner scanner)
{
    private long? managerRva;
    private int offset;
    private DateTimeOffset retryAt;
    public string Status { get; private set; } = "等待读取房间 ID。";
    private const string ManagerPattern = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? 48 83 C4";
    private const string LegacyManagerPattern = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 45 33 C0 48 8D 57 ?? 41 8B CE E8 ?? ?? ?? ?? 48 8D 8F";
    private const string OffsetPattern = "0F 11 83 ?? ?? ?? ?? 0F 10 4F ?? 0F 11 8B ?? ?? ?? ?? 0F 10 47 ?? 0F 11 83 ?? ?? ?? ?? 0F 10 4F ?? 0F 11 8B ?? ?? ?? ?? 0F 10 47 ?? 0F 11 83 ?? ?? ?? ?? 0F 10 4F ?? 0F 11 8B ?? ?? ?? ?? 0F 10 47 ?? 0F 11 83 ?? ?? ?? ?? 0F 10 4F";

    public uint Read(DateTimeOffset now)
    {
        try
        {
            if (managerRva == null)
            {
                if (now < retryAt) return 0;
                retryAt = now.AddSeconds(10);
                if ((!scanner.TryGetStaticAddressFromSig(ManagerPattern, out var manager)
                    && !scanner.TryGetStaticAddressFromSig(LegacyManagerPattern, out manager))
                    || !scanner.TryScanText(OffsetPattern, out var code))
                { Status = "房间 ID 签名未匹配，暂不联网查询。"; return 0; }
                var fieldOffset = Marshal.ReadInt32(code + 3);
                var rva = manager.ToInt64() - scanner.Module.BaseAddress.ToInt64();
                if (fieldOffset is < 0 or > 0x100000 || rva < 0 || rva + fieldOffset + 6 >= scanner.Module.ModuleMemorySize)
                { Status = "房间 ID 地址校验失败。"; return 0; }
                managerRva = rva; offset = fieldOffset; // Managed offsets only; no native pointer survives a frame.
            }
            Span<byte> bytes = stackalloc byte[6];
            var address = scanner.Module.BaseAddress + (nint)managerRva.Value + offset;
            if (!ReadBytes(address, bytes)) { Status = "房间 ID 读取失败。"; return 0; }
            var high = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            var low = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            var id = ((uint)high << 16) | low;
            Status = high == 0 ? "等待服务器下发房间 ID。" : $"房间 ID：{id}";
            return high == 0 ? 0 : id;
        }
        catch (Exception) { Status = "房间 ID 读取不可用，暂不联网查询。"; return 0; }
    }

    private static unsafe bool ReadBytes(nint address, Span<byte> bytes)
    {
        fixed (byte* target = bytes)
            return ReadProcessMemory(new nint(-1), address, target, (nuint)bytes.Length, out var count) && count == (nuint)bytes.Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint address, byte* destination, nuint size, out nuint read);
}
