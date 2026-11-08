using System.Diagnostics;
using System.Globalization;

namespace MemoryForge.Core;

public sealed record MemoryRegion(ulong Start, ulong Size, bool Readable, bool Writable, bool Executable, string? Path = null)
{
    public ulong End => checked(Start + Size);
    public string Address => AddressCodec.Format(Start);
    public string EndAddress => AddressCodec.Format(End);
}

public interface IProcessMemory : IDisposable
{
    int ProcessId { get; }
    bool CanWrite { get; }
    IReadOnlyList<MemoryRegion> Regions();
    // Partial reads are normal when a process changes its mappings concurrently.
    byte[] Read(ulong address, int length);
    void Write(ulong address, byte[] data);
    void EnsureAlive();
}

public static class AddressCodec
{
    public static ulong Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    }
    public static string Format(ulong address) => $"0x{address:X}";
    public static ulong Add(ulong address, long offset) => offset >= 0
        ? checked(address + (ulong)offset) : checked(address - (ulong)(-(offset + 1)) - 1);
}

public abstract class ProcessMemory : IProcessMemory
{
    private readonly Process process;
    private readonly DateTime started;
    protected bool Disposed;
    public int ProcessId => process.Id;
    public bool CanWrite { get; }

    protected ProcessMemory(int pid, bool writable)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        process = Process.GetProcessById(pid);
        started = process.StartTime.ToUniversalTime();
        CanWrite = writable;
    }

    public void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        process.Refresh();
        if (process.HasExited || process.StartTime.ToUniversalTime() != started)
            throw new InvalidOperationException("Target exited or its PID was reused. Attach again.");
    }

    protected void Validate(ulong address, int length, bool write = false)
    {
        EnsureAlive();
        if (length is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(length), "Transfer size must be 1..1048576 bytes.");
        _ = checked(address + (ulong)length);
        if (address > (ulong)nint.MaxValue) throw new ArgumentOutOfRangeException(nameof(address));
        if (write && !CanWrite) throw new InvalidOperationException("Session is read-only. Attach with writable=true to modify memory.");
    }

    public abstract IReadOnlyList<MemoryRegion> Regions();
    public abstract byte[] Read(ulong address, int length);
    public abstract void Write(ulong address, byte[] data);
    protected virtual void Release() { }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Release();
        process.Dispose();
        GC.SuppressFinalize(this);
    }

    public static IProcessMemory Open(int pid, bool writable = false)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Use a 64-bit MemoryForge binary.");
        if (OperatingSystem.IsWindows()) return new WindowsMemory(pid, writable);
        if (OperatingSystem.IsLinux()) return new LinuxMemory(pid, writable);
        if (OperatingSystem.IsMacOS()) return new MacMemory(pid, writable);
        throw new PlatformNotSupportedException("Supported hosts: Windows, Linux, macOS.");
    }
}
