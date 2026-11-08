using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MemoryForge.Core;

internal sealed class WindowsMemory : ProcessMemory
{
    private readonly SafeProcessHandle handle;
    public WindowsMemory(int pid, bool writable) : base(pid, writable)
    {
        handle = OpenProcess(0x0400 | 0x0010u | (writable ? 0x0028u : 0), false, pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            base.Dispose();
            throw new Win32Exception(error, "OpenProcess failed; target access rights may require elevation.");
        }
    }

    public override IReadOnlyList<MemoryRegion> Regions()
    {
        EnsureAlive();
        var result = new List<MemoryRegion>();
        ulong address = 0;
        while (VirtualQueryEx(handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            var start = (ulong)info.BaseAddress;
            var size = (ulong)info.RegionSize;
            if (size == 0 || start > (ulong)nint.MaxValue - size) break;
            var protection = info.Protect & 0xff;
            if (info.State == 0x1000 && (info.Protect & 0x100) == 0)
                result.Add(new(start, size, protection is 2 or 4 or 8 or 0x20 or 0x40 or 0x80,
                    protection is 4 or 8 or 0x40 or 0x80, protection is 0x10 or 0x20 or 0x40 or 0x80));
            address = start + size;
        }
        return result;
    }

    public override byte[] Read(ulong address, int length)
    {
        Validate(address, length);
        var bytes = new byte[length];
        var ok = ReadProcessMemory(handle, (nint)address, bytes, (nuint)length, out var read);
        if (!ok && read == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot read {AddressCodec.Format(address)}.");
        return bytes.AsSpan(0, checked((int)read)).ToArray();
    }

    public override void Write(ulong address, byte[] data)
    {
        Validate(address, data.Length, true);
        if (!WriteProcessMemory(handle, (nint)address, data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Write failed or was partial; re-read the target before retrying.");
    }

    protected override void Release() => handle?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryBasicInformation information, nuint length);
    [DllImport("kernel32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
}
