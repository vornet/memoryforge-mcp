using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryForge.Core;
using MemoryForge.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using ModelContextProtocol.Protocol;

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
if (args.Contains("--version")) { Console.WriteLine($"MemoryForge {version}"); return; }
if (args.Contains("--help"))
{
    Console.WriteLine("MemoryForge MCP: local stdio process-memory tools\nUsage: memoryforge [--audit-log PATH] | --doctor | --version\nConfigure this executable as an MCP stdio server in your agent.\nNo listener, game-specific AI, browser, or elevated service is started.");
    return;
}
if (args.Contains("--doctor"))
{
    var pointer = Marshal.AllocHGlobal(8);
    try
    {
        Marshal.WriteInt64(pointer, 123456789);
        using var memory = ProcessMemory.Open(Environment.ProcessId, true);
        var before = memory.Read((ulong)pointer, 8);
        memory.Write((ulong)pointer, BitConverter.GetBytes(987654321L));
        var ok = BitConverter.ToInt64(memory.Read((ulong)pointer, 8)) == 987654321;
        memory.Write((ulong)pointer, before);
        Console.WriteLine(JsonSerializer.Serialize(new { version, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), nativeReadWrite = ok, regions = memory.Regions().Count }));
        Environment.ExitCode = ok ? 0 : 1;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    finally { Marshal.FreeHGlobal(pointer); }
    return;
}
var auditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryForge", $"audit-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--audit-log" && i + 1 < args.Length) auditPath = args[++i];
    else { Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}. Use --help."); Environment.ExitCode = 2; return; }
}
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(_ => new AuditLog(auditPath));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "memoryforge", Version = version };
    options.ServerInstructions = MemoryTools.AgentGuide;
}).WithStdioServerTransport().WithTools<MemoryTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, token) =>
    {
        try { return await next(context, token); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Win32Exception or UnauthorizedAccessException or KeyNotFoundException or FormatException or OverflowException or JsonException)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
    }));
await builder.Build().RunAsync();
