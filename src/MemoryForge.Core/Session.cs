using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryForge.Core;

public sealed record WriteRecord(string Id, string SessionId, int ProcessId, string Address, string Before, string After, DateTimeOffset Time, string State);

public sealed class AuditLog : IDisposable
{
    private readonly FileStream stream;
    private readonly object gate = new();
    public AuditLog(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)!;
        var parentExisted = Directory.Exists(parent);
        Directory.CreateDirectory(parent);
        if (!OperatingSystem.IsWindows() && !parentExisted) File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public void Append(WriteRecord entry)
    {
        lock (gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(true);
        }
    }
    public void Dispose() { lock (gate) stream.Dispose(); }
}

public sealed class MemorySession : IDisposable
{
    private readonly IProcessMemory memory;
    private readonly AuditLog audit;
    private readonly object gate = new();
    private readonly Dictionary<string, WriteRecord> writes = new();
    private bool disposed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int ProcessId => memory.ProcessId;
    public bool Writable => memory.CanWrite;
    public ConcurrentDictionary<string, ScanJob> Scans { get; } = new();

    public MemorySession(IProcessMemory memory, AuditLog audit) { this.memory = memory; this.audit = audit; }
    private void Check() { ObjectDisposedException.ThrowIf(disposed, this); memory.EnsureAlive(); }

    public IReadOnlyList<MemoryRegion> Regions() { lock (gate) { Check(); return memory.Regions(); } }
    public byte[] Read(ulong address, int length) { lock (gate) { Check(); return memory.Read(address, length); } }
    public byte[] ReadExact(ulong address, int length)
    {
        var data = Read(address, length);
        if (data.Length != length) throw new IOException($"Partial read: requested {length}, read {data.Length}.");
        return data;
    }

    public WriteRecord Write(ulong address, byte[] data, byte[] expected)
    {
        if (data.Length != expected.Length || data.Length is < 1 or > 4096) throw new ArgumentException("Write and expected bytes must have the same length, 1..4096.");
        lock (gate)
        {
            Check();
            if (!Writable) throw new InvalidOperationException("Session is read-only. Attach with writable=true.");
            if (writes.Count >= 1000) throw new InvalidOperationException("Session write journal is full. Detach and attach a new session.");
            var before = memory.Read(address, data.Length);
            if (!before.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"Precondition failed: current bytes are {Convert.ToHexString(before)}. Nothing was written.");
            var record = new WriteRecord(Guid.NewGuid().ToString("N"), Id, ProcessId, AddressCodec.Format(address), Convert.ToHexString(before), Convert.ToHexString(data), DateTimeOffset.UtcNow, "prepared");
            audit.Append(record); // Durable original bytes before touching the target.
            writes.Add(record.Id, record);
            try
            {
                memory.Write(address, data);
                var after = memory.Read(address, data.Length);
                record = record with { State = after.AsSpan().SequenceEqual(data) ? "verified" : "verification_failed" };
            }
            catch
            {
                writes[record.Id] = record with { State = "write_failed_or_partial" };
                audit.Append(writes[record.Id]);
                throw;
            }
            writes[record.Id] = record;
            audit.Append(record);
            return record;
        }
    }

    public WriteRecord Undo(string writeId)
    {
        lock (gate)
        {
            Check();
            if (!writes.TryGetValue(writeId, out var record)) throw new KeyNotFoundException("Write ID does not belong to this session.");
            if (record.State != "verified") throw new InvalidOperationException("Only verified writes can be undone automatically; inspect current bytes first.");
            var undo = Write(AddressCodec.Parse(record.Address), Convert.FromHexString(record.Before), Convert.FromHexString(record.After));
            writes[writeId] = record with { State = "undone" };
            audit.Append(writes[writeId]);
            return undo;
        }
    }
    public WriteRecord[] History() { lock (gate) return writes.Values.ToArray(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var job in Scans.Values) job.Cancel();
            memory.Dispose();
        }
    }
}

public sealed class SessionStore(AuditLog audit) : IDisposable
{
    private readonly Dictionary<string, MemorySession> sessions = new();
    private readonly object gate = new();
    public MemorySession Attach(int pid, bool writable)
    {
        lock (gate)
        {
            if (sessions.Count >= 4) throw new InvalidOperationException("Maximum 4 sessions. Detach an unused session.");
            var session = new MemorySession(ProcessMemory.Open(pid, writable), audit);
            sessions.Add(session.Id, session);
            return session;
        }
    }
    public MemorySession Get(string id) { lock (gate) return sessions.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException("Session not found. Attach first."); }
    public void Detach(string id)
    {
        lock (gate)
        {
            if (!sessions.Remove(id, out var session)) throw new KeyNotFoundException("Session not found.");
            session.Dispose();
        }
    }
    public object[] List() { lock (gate) return sessions.Values.Select(s => (object)new { sessionId = s.Id, processId = s.ProcessId, writable = s.Writable }).ToArray(); }
    public void Dispose() { lock (gate) { foreach (var session in sessions.Values) session.Dispose(); sessions.Clear(); } }
}
