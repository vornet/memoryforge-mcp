using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MemoryForge.Core;

internal sealed class LinuxMemory(int pid, bool writable) : ProcessMemory(pid, writable)
{
    public override IReadOnlyList<MemoryRegion> Regions()
    {
        EnsureAlive();
        return File.ReadLines($"/proc/{ProcessId}/maps").Select(ParseMap).ToArray();
    }

    internal static MemoryRegion ParseMap(string line)
    {
        var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        var range = fields[0].Split('-');
        var start = ulong.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var end = ulong.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(start, end - start, fields[1][0] == 'r', fields[1][1] == 'w', fields[1][2] == 'x', fields.Length == 6 ? fields[5].Trim() : null);
    }

    public override byte[] Read(ulong address, int length)
    {
        Validate(address, length);
        var buffer = new byte[length];
        var count = Transfer(address, buffer, false);
        return buffer.AsSpan(0, count).ToArray();
    }

    public override void Write(ulong address, byte[] data)
    {
        Validate(address, data.Length, true);
        if (Transfer(address, data, true) != data.Length)
            throw new IOException("Partial memory write; re-read target before retrying.");
    }

    private int Transfer(ulong address, byte[] data, bool write)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var local = new IOVector { Base = pin.AddrOfPinnedObject(), Length = (nuint)data.Length };
            var remote = new IOVector { Base = (nint)address, Length = (nuint)data.Length };
            var count = write ? process_vm_writev(ProcessId, ref local, 1, ref remote, 1, 0) : process_vm_readv(ProcessId, ref local, 1, ref remote, 1, 0);
            if (count < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Memory access denied or unavailable. Check target ownership, Yama ptrace_scope, and CAP_SYS_PTRACE.");
            return checked((int)count);
        }
        finally { pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IOVector { public nint Base; public nuint Length; }
    [DllImport("libc", SetLastError = true)] private static extern nint process_vm_readv(int pid, ref IOVector local, nuint localCount, ref IOVector remote, nuint remoteCount, nuint flags);
    [DllImport("libc", SetLastError = true)] private static extern nint process_vm_writev(int pid, ref IOVector local, nuint localCount, ref IOVector remote, nuint remoteCount, nuint flags);
}
