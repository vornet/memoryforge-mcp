using System.Runtime.InteropServices;
using System.Text.Json;

// This intentionally attachable test fixture is never shipped in the installer.
if (OperatingSystem.IsLinux()) _ = Native.prctl(0x59616d61, -1, 0, 0, 0); // PR_SET_PTRACER_ANY, permits sibling MCP test server.
var buffer = Marshal.AllocHGlobal(600_000);
try
{
    Marshal.Copy(new byte[600_000], 0, buffer, 600_000);
    Marshal.WriteInt32(buffer, 128, 1234); // Target character.
    Marshal.WriteInt32(buffer, 256, 1234); // Other character with same balance.
    Marshal.WriteInt32(buffer, 384, 1234); // Stale display cache.
    Marshal.WriteInt64(buffer, 16, (long)buffer + 128); // Pointer to target.
    Marshal.Copy(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 0, buffer + 262_142, 4);
    Console.WriteLine(JsonSerializer.Serialize(new { processId = Environment.ProcessId, start = $"0x{(ulong)buffer:X}", end = $"0x{(ulong)buffer + 600_000:X}", target = $"0x{(ulong)buffer + 128:X}", other = $"0x{(ulong)buffer + 256:X}", pointer = $"0x{(ulong)buffer + 16:X}" }));
    while (Console.ReadLine() is { } command)
    {
        if (command == "quit") break;
        if (command == "spend") Marshal.WriteInt32(buffer, 128, Marshal.ReadInt32(buffer, 128) - 7);
        Console.WriteLine(JsonSerializer.Serialize(new { target = Marshal.ReadInt32(buffer, 128), other = Marshal.ReadInt32(buffer, 256), cache = Marshal.ReadInt32(buffer, 384) }));
    }
}
finally { Marshal.FreeHGlobal(buffer); }

internal static class Native
{
    [DllImport("libc")] internal static extern int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);
}
