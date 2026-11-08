using System.ComponentModel;

namespace MemoryForge.Core;

public sealed record ScanOptions(ValueKind Kind, string? Value = null, string? Pattern = null,
    ulong Start = 0, ulong End = 0x7FFFFFFFFFFFFFFF, int Alignment = 0, bool WritableOnly = true,
    bool BigEndian = false, int MaxCandidates = 100_000, long MaxBytes = 1_073_741_824);
public sealed record ScanCandidate(ulong Address, byte[] Bytes);
public sealed record ScanStatus(string ScanId, string State, int Count, long BytesRead, long ReadFailures,
    bool Incomplete, string? Message, int Revision);

public sealed class ScanJob
{
    private static readonly SemaphoreSlim Workers = new(2);
    private readonly object gate = new();
    private readonly MemorySession session;
    private readonly ScanOptions options;
    private readonly byte[]? target;
    private readonly BytePattern? pattern;
    private readonly int width;
    private readonly int alignment;
    private readonly int candidateLimit;
    private CancellationTokenSource cancellation = new();
    private ScanCandidate[] candidates = [];
    private string state = "created";
    private string? message;
    private bool incomplete;
    private long bytesRead, readFailures;
    private int revision;
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public ScanJob(MemorySession session, ScanOptions options)
    {
        this.session = session;
        this.options = options;
        if (options.Start >= options.End) throw new ArgumentException("Start must be below exclusive end address.");
        if (options.MaxCandidates is < 1 or > 100_000 || options.MaxBytes is < 1 or > 8_589_934_592)
            throw new ArgumentException("Limits: 1..100000 candidates, 1..8589934592 bytes.");
        if (options.Pattern is not null && options.Value is not null) throw new ArgumentException("Use either a pattern or a typed value.");
        pattern = options.Pattern is null ? null : new BytePattern(options.Pattern);
        target = options.Value is null ? null : ValueCodec.Encode(options.Kind, options.Value, options.BigEndian);
        width = pattern?.Length ?? target?.Length ?? ValueCodec.Width(options.Kind);
        if (width > 256) throw new ArgumentException("Scan values are limited to 256 bytes. Use read_memory for larger data.");
        alignment = options.Alignment == 0 ? (pattern is not null || options.Kind is ValueKind.Bytes or ValueKind.Utf8 or ValueKind.Utf16 ? 1 : width) : options.Alignment;
        if (alignment is < 1 or > 4096) throw new ArgumentException("Alignment must be 1..4096 (or 0 for automatic).");
        candidateLimit = Math.Min(options.MaxCandidates, 16_777_216 / width);
    }

    public ScanStatus Status()
    {
        lock (gate) return new(Id, state, candidates.Length, Interlocked.Read(ref bytesRead), Interlocked.Read(ref readFailures), incomplete, message, revision);
    }

    public object Results(int offset = 0, int limit = 100)
    {
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentException("Offset must be nonnegative; limit must be 1..500.");
        lock (gate)
        {
            return new
            {
                status = Status(),
                offset,
                values = candidates.Skip(offset).Take(limit).Select(c => new
                {
                    address = AddressCodec.Format(c.Address),
                    hex = Convert.ToHexString(c.Bytes),
                    value = ValueCodec.Decode(pattern is null ? options.Kind : ValueKind.Bytes, c.Bytes, options.BigEndian)
                }).ToArray()
            };
        }
    }

    public void Start() => Launch(null, null);
    public void Refine(ScanComparison comparison, string? value = null)
    {
        var requiresValue = comparison is ScanComparison.Exact or ScanComparison.NotEqual or ScanComparison.GreaterThan or ScanComparison.LessThan or ScanComparison.IncreasedBy or ScanComparison.DecreasedBy;
        if (requiresValue && value is null) throw new ArgumentException("This comparison requires a value.");
        var bytes = value is null ? null : ValueCodec.Encode(pattern is null ? options.Kind : ValueKind.Bytes, value, options.BigEndian);
        if (bytes is not null && bytes.Length != width) throw new ArgumentException("Refinement value must have the original scan width.");
        if ((pattern is not null || options.Kind is ValueKind.Bytes or ValueKind.Utf8 or ValueKind.Utf16) && comparison is not (ScanComparison.Exact or ScanComparison.NotEqual or ScanComparison.Changed or ScanComparison.Unchanged))
            throw new ArgumentException("Ordered comparisons require numeric scans.");
        Launch(comparison, bytes);
    }

    public void Cancel() { lock (gate) cancellation.Cancel(); }
    private void Launch(ScanComparison? comparison, byte[]? value)
    {
        lock (gate)
        {
            if (state is "queued" or "running") throw new InvalidOperationException("Scan is busy; poll scan_status or cancel it.");
            if (comparison is not null && revision == 0) throw new InvalidOperationException("Initial scan has not completed successfully.");
            if (comparison is null && state != "created") throw new InvalidOperationException("Scan has already started.");
            cancellation.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            state = "queued";
            message = null;
            bytesRead = readFailures = 0;
            var previous = candidates;
            _ = Task.Run(async () =>
            {
                var acquired = false;
                try
                {
                    await Workers.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    lock (gate) state = "running";
                    var result = comparison is null ? First(token) : Next(previous, comparison.Value, value, token);
                    lock (gate)
                    {
                        candidates = result.Candidates;
                        incomplete |= result.Incomplete;
                        message = result.Message;
                        state = "completed";
                        revision++;
                    }
                }
                catch (OperationCanceledException) { lock (gate) { state = "cancelled"; message = "Cancelled; prior completed results retained."; } }
                catch (Exception ex) { lock (gate) { state = "failed"; message = ex.Message; } }
                finally { if (acquired) Workers.Release(); }
            });
        }
    }

    private (ScanCandidate[] Candidates, bool Incomplete, string? Message) First(CancellationToken token)
    {
        var found = new List<ScanCandidate>();
        var regions = session.Regions().Where(r => r.Readable && (!options.WritableOnly || r.Writable) && r.End > options.Start && r.Start < options.End);
        foreach (var region in regions)
        {
            var start = Math.Max(region.Start, options.Start);
            var end = Math.Min(region.End, options.End);
            var position = start;
            while (position < end)
            {
                token.ThrowIfCancellationRequested();
                var remaining = options.MaxBytes - Interlocked.Read(ref bytesRead);
                if (remaining < width) return (found.ToArray(), true, "Byte budget reached. Narrow the address range or increase maxBytes; results cover only part of the requested memory.");
                var length = (int)Math.Min(Math.Min(262_144UL, end - position), (ulong)remaining);
                if (length < width) break;
                byte[] data;
                try { data = session.Read(position, length); }
                catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref readFailures);
                    // Retry subsequent pages rather than discarding an entire 256 KiB chunk.
                    position += Math.Min((ulong)Environment.SystemPageSize, end - position);
                    Interlocked.Add(ref bytesRead, Math.Min(length, Environment.SystemPageSize));
                    continue;
                }
                Interlocked.Add(ref bytesRead, data.Length);
                var offset = (int)((ulong)alignment - position % (ulong)alignment) % alignment;
                for (; offset <= data.Length - width; offset += alignment)
                {
                    if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
                    var bytes = data.AsSpan(offset, width);
                    if (pattern?.Matches(bytes) ?? (target is null || bytes.SequenceEqual(target)))
                    {
                        found.Add(new(position + (ulong)offset, bytes.ToArray()));
                        if (found.Count >= candidateLimit) return (found.ToArray(), true, "Candidate/storage limit reached. Narrow the range or refine these partial results; unscanned matches are not included.");
                    }
                }
                if (data.Length < length) Interlocked.Increment(ref readFailures);
                // Retain width-1 bytes across chunk boundaries; do not duplicate candidate starts.
                var advance = data.Length >= width ? data.Length - width + 1 : Math.Min(length, Environment.SystemPageSize);
                position += (ulong)Math.Max(advance, 1);
                if (end - position < (ulong)width) break;
            }
        }
        var failures = Interlocked.Read(ref readFailures);
        return (found.ToArray(), failures > 0, failures > 0 ? "Some reads failed or were partial; those bytes were not fully searched." : null);
    }

    private (ScanCandidate[] Candidates, bool Incomplete, string? Message) Next(ScanCandidate[] previous, ScanComparison comparison, byte[]? value, CancellationToken token)
    {
        var found = new List<ScanCandidate>();
        // Batch nearby candidates to avoid one OS call per value in unknown-value scans.
        for (var i = 0; i < previous.Length;)
        {
            token.ThrowIfCancellationRequested();
            var first = i;
            var start = previous[i].Address;
            while (i + 1 < previous.Length && previous[i + 1].Address - start < 65_536UL - (ulong)width) i++;
            var end = previous[i].Address + (ulong)width;
            byte[]? block = null;
            try { block = session.Read(start, checked((int)(end - start))); }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException) { }
            for (var k = first; k <= i; k++)
            {
                token.ThrowIfCancellationRequested();
                var candidate = previous[k];
                byte[] current;
                var offset = checked((int)(candidate.Address - start));
                if (block is not null && offset + width <= block.Length) current = block.AsSpan(offset, width).ToArray();
                else
                {
                    try { current = session.ReadExact(candidate.Address, width); }
                    catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException) { Interlocked.Increment(ref readFailures); continue; }
                }
                Interlocked.Add(ref bytesRead, width);
                if (ValueCodec.Matches(pattern is null ? options.Kind : ValueKind.Bytes, current, candidate.Bytes, comparison, value, options.BigEndian))
                    found.Add(new(candidate.Address, current));
            }
            i++;
        }
        return (found.ToArray(), readFailures > 0, readFailures > 0 ? "Unreadable candidates were dropped." : null);
    }
}
