using System.Runtime.InteropServices;
using System.Text;

namespace MemoryForge.Core;

internal sealed class MacMemory : ProcessMemory
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private uint task;
    public MacMemory(int pid, bool writable) : base(pid, writable)
    {
        var result = task_for_pid(mach_task_self(), pid, out task);
        if (result != 0)
        {
            task = 0;
            base.Dispose();
            throw new UnauthorizedAccessException($"task_for_pid failed ({result}). macOS requires debugger authorization and an attachable target; hardened/protected apps may deny access.");
        }
    }

    public override IReadOnlyList<MemoryRegion> Regions()
    {
        EnsureAlive();
        var regions = new List<MemoryRegion>();
        ulong address = 0;
        while (true)
        {
            uint count = 9;
            var result = mach_vm_region(task, ref address, out var size, 9, out var info, ref count, out var objectName);
            if (objectName != 0) _ = mach_port_deallocate(mach_task_self(), objectName);
            if (result == 1) break; // KERN_INVALID_ADDRESS: end of map.
            Check(result, "enumerate regions");
            if (size == 0 || address > ulong.MaxValue - size) break;
            var pathBuffer = new byte[4096];
            var pathLength = proc_regionfilename(ProcessId, address, pathBuffer, (uint)pathBuffer.Length);
            var path = pathLength > 0 ? Encoding.UTF8.GetString(pathBuffer, 0, pathLength).TrimEnd('\0') : null;
            regions.Add(new(address, size, (info.Protection & 1) != 0, (info.Protection & 2) != 0, (info.Protection & 4) != 0, path));
            address += size;
        }
        return regions;
    }

    public override byte[] Read(ulong address, int length)
    {
        Validate(address, length);
        var data = new byte[length];
        Check(mach_vm_read_overwrite(task, address, (ulong)length, data, out var count), "read memory");
        return data.AsSpan(0, checked((int)count)).ToArray();
    }

    public override void Write(ulong address, byte[] data)
    {
        Validate(address, data.Length, true);
        Check(mach_vm_write(task, address, data, (uint)data.Length), "write memory");
    }

    protected override void Release()
    {
        if (task != 0) _ = mach_port_deallocate(mach_task_self(), task);
        task = 0;
    }
    private static void Check(int result, string operation)
    {
        if (result != 0) throw new IOException($"Mach {operation} failed: {result}.");
    }
    // XNU vm_region_basic_info_64 is packed to four bytes (36 bytes / 9 ints).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RegionInfo
    {
        public int Protection, MaximumProtection, Inheritance, Shared, Reserved;
        public ulong Offset;
        public int Behavior;
        public ushort WiredCount;
    }
    [DllImport(Lib)] private static extern uint mach_task_self();
    [DllImport(Lib)] private static extern int task_for_pid(uint self, int pid, out uint task);
    [DllImport(Lib)] private static extern int mach_port_deallocate(uint self, uint port);
    [DllImport(Lib)] private static extern int mach_vm_region(uint task, ref ulong address, out ulong size, int flavor, out RegionInfo info, ref uint count, out uint objectName);
    [DllImport(Lib)] private static extern int mach_vm_read_overwrite(uint task, ulong address, ulong size, [Out] byte[] data, out ulong count);
    [DllImport(Lib)] private static extern int mach_vm_write(uint task, ulong address, byte[] data, uint count);
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_regionfilename(int pid, ulong address, [Out] byte[] buffer, uint size);
}
