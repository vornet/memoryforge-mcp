using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryForge.Core;

namespace MemoryForge.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "memoryforge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AuditLog audit;
    public CoreTests() { audit = new AuditLog(Path.Combine(directory, "audit.jsonl")); }
    public void Dispose() { audit.Dispose(); Directory.Delete(directory, true); }

    [Theory]
    [InlineData(ValueKind.Int64, "9223372036854775807")]
    [InlineData(ValueKind.UInt64, "18446744073709551615")]
    [InlineData(ValueKind.Int32, "-2147483648")]
    [InlineData(ValueKind.Float32, "1.25")]
    [InlineData(ValueKind.Float64, "-0.125")]
    [InlineData(ValueKind.Utf16, "Gold 🪙")]
    public void ValuesRoundTripInBothByteOrders(ValueKind kind, string value)
    {
        foreach (var bigEndian in new[] { false, true })
            Assert.Equal(value, ValueCodec.Decode(kind, ValueCodec.Encode(kind, value, bigEndian), bigEndian));
    }

    [Fact]
    public void UInt64ComparisonDoesNotLosePrecision()
    {
        var previous = ValueCodec.Encode(ValueKind.UInt64, "18446744073709551614");
        var current = ValueCodec.Encode(ValueKind.UInt64, "18446744073709551615");
        Assert.True(ValueCodec.Matches(ValueKind.UInt64, current, previous, ScanComparison.IncreasedBy, ValueCodec.Encode(ValueKind.UInt64, "1"), false));
    }

    [Fact]
    public void PatternsSupportNibbleWildcardsAndRejectMalformedTokens()
    {
        Assert.True(new BytePattern("48 ?B ?? F?").Matches([0x48, 0x8b, 0x12, 0xfe]));
        Assert.False(new BytePattern("48 ?B ?? F?").Matches([0x48, 0x8a, 0x12, 0xfe]));
        Assert.Throws<FormatException>(() => new BytePattern("ABC"));
        Assert.Throws<FormatException>(() => new BytePattern("GG"));
    }

    [Fact]
    public void AddressesAreCheckedAndPreserveFullPrecision()
    {
        Assert.Equal(ulong.MaxValue, AddressCodec.Parse("0xFFFFFFFFFFFFFFFF"));
        Assert.Equal(0UL, AddressCodec.Add(7, -7));
        Assert.Throws<OverflowException>(() => AddressCodec.Add(0, -1));
        Assert.Throws<OverflowException>(() => AddressCodec.Add(ulong.MaxValue, 1));
        Assert.Throws<OverflowException>(() => AddressCodec.Add(0, long.MinValue));
    }

    [Fact]
    public void GuardedWriteAndConditionalUndoPersistOriginals()
    {
        using var session = new MemorySession(new FakeMemory(32), audit);
        var record = session.Write(FakeMemory.Base, [1, 2, 3, 4], [0, 0, 0, 0]);
        Assert.Equal("verified", record.State);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, session.ReadExact(FakeMemory.Base, 4));
        Assert.Throws<InvalidOperationException>(() => session.Write(FakeMemory.Base, [9, 9, 9, 9], [0, 0, 0, 0]));
        session.Undo(record.Id);
        Assert.Equal(new byte[4], session.ReadExact(FakeMemory.Base, 4));
        Assert.Throws<InvalidOperationException>(() => session.Undo(record.Id));
        using var stream = new FileStream(Path.Combine(directory, "audit.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var log = reader.ReadToEnd();
        Assert.Contains("prepared", log);
        Assert.Contains("00000000", log);
    }

    [Fact]
    public void UndoDoesNotOverwriteSubsequentGameChanges()
    {
        var memory = new FakeMemory(32);
        using var session = new MemorySession(memory, audit);
        var record = session.Write(FakeMemory.Base, [1], [0]);
        memory.Data[0] = 7;
        Assert.Throws<InvalidOperationException>(() => session.Undo(record.Id));
        Assert.Equal(7, memory.Data[0]);
    }

    [Fact]
    public void ReadOnlySessionRejectsWritesBeforeJournaling()
    {
        using var session = new MemorySession(new FakeMemory(32, false), audit);
        Assert.Throws<InvalidOperationException>(() => session.Write(FakeMemory.Base, [1], [0]));
        Assert.Empty(session.History());
    }

    [Fact]
    public async Task UnknownScanRefinesAgainstLastSuccessfulBaseline()
    {
        var memory = new FakeMemory(64);
        using var session = new MemorySession(memory, audit);
        var job = new ScanJob(session, new(ValueKind.Int32));
        job.Start(); await Completed(job);
        Assert.Equal(16, job.Status().Count);
        BitConverter.GetBytes(10).CopyTo(memory.Data, 12);
        BitConverter.GetBytes(10).CopyTo(memory.Data, 28);
        job.Refine(ScanComparison.IncreasedBy, "10"); await Completed(job);
        Assert.Equal(2, job.Status().Count);
        BitConverter.GetBytes(11).CopyTo(memory.Data, 12);
        job.Refine(ScanComparison.IncreasedBy, "1"); await Completed(job);
        Assert.Equal(1, job.Status().Count);
        Assert.Equal(3, job.Status().Revision);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(job.Results()));
        Assert.Equal(AddressCodec.Format(FakeMemory.Base + 12), result.RootElement.GetProperty("values")[0].GetProperty("address").GetString());
    }

    [Fact]
    public async Task PatternCrossingChunkBoundaryIsFoundExactlyOnce()
    {
        var memory = new FakeMemory(600_000);
        byte[] pattern = [0xDE, 0xAD, 0xBE, 0xEF];
        pattern.CopyTo(memory.Data, 262_142);
        using var session = new MemorySession(memory, audit);
        var job = new ScanJob(session, new(ValueKind.Bytes, Pattern: "DE AD ?? EF"));
        job.Start(); await Completed(job);
        Assert.Equal(1, job.Status().Count);
        Assert.False(job.Status().Incomplete);
    }

    [Fact]
    public async Task CandidateLimitReportsIncompleteInsteadOfPretendingFullCoverage()
    {
        using var session = new MemorySession(new FakeMemory(4096), audit);
        var job = new ScanJob(session, new(ValueKind.Int32, MaxCandidates: 3));
        job.Start(); await Completed(job);
        Assert.Equal(3, job.Status().Count);
        Assert.True(job.Status().Incomplete);
        job.Refine(ScanComparison.Unchanged); await Completed(job);
        Assert.True(job.Status().Incomplete);
    }

    [Fact]
    public async Task ByteBudgetAndUnalignedRangeAreRespected()
    {
        using var session = new MemorySession(new FakeMemory(4096), audit);
        var job = new ScanJob(session, new(ValueKind.Int32, Start: FakeMemory.Base + 1, End: FakeMemory.Base + 4096, MaxBytes: 32));
        job.Start(); await Completed(job);
        Assert.True(job.Status().Incomplete);
        Assert.InRange(job.Status().BytesRead, 1, 32);
        using var results = JsonDocument.Parse(JsonSerializer.Serialize(job.Results()));
        foreach (var value in results.RootElement.GetProperty("values").EnumerateArray())
            Assert.Equal(0UL, AddressCodec.Parse(value.GetProperty("address").GetString()!) % 4);
    }

    [Fact]
    public async Task CancelledRefinementRetainsPreviousRevision()
    {
        var memory = new FakeMemory(100_000);
        using var session = new MemorySession(memory, audit);
        var job = new ScanJob(session, new(ValueKind.Int32));
        job.Start(); await Completed(job);
        memory.Delay = 100;
        job.Refine(ScanComparison.Changed);
        job.Cancel();
        await Terminal(job);
        Assert.Equal("cancelled", job.Status().State);
        Assert.Equal(25_000, job.Status().Count);
        Assert.Equal(1, job.Status().Revision);
    }

    [Fact]
    public void NativeSelfReadWriteAndRegionEnumeration()
    {
        var pointer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(pointer, 12345);
            using var memory = ProcessMemory.Open(Environment.ProcessId, true);
            Assert.Equal(12345, BitConverter.ToInt64(memory.Read((ulong)pointer, 8)));
            memory.Write((ulong)pointer, BitConverter.GetBytes(67890L));
            Assert.Equal(67890, Marshal.ReadInt64(pointer));
            Assert.Contains(memory.Regions(), r => r.Start <= (ulong)pointer && r.End > (ulong)pointer && r.Readable && r.Writable);
            Assert.ThrowsAny<Exception>(() => memory.Read(0, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.Read((ulong)pointer, 0));
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static async Task Completed(ScanJob job)
    {
        await Terminal(job);
        Assert.True(job.Status().State == "completed", JsonSerializer.Serialize(job.Status()));
    }
    private static async Task Terminal(ScanJob job)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (job.Status().State is "queued" or "running") await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeMemory(int length, bool writable = true) : IProcessMemory
    {
        public const ulong Base = 0x1000;
        public byte[] Data { get; } = new byte[length];
        public int Delay { get; set; }
        public int ProcessId => 1;
        public bool CanWrite => writable;
        public IReadOnlyList<MemoryRegion> Regions() => [new(Base, (ulong)Data.Length, true, writable, false)];
        public byte[] Read(ulong address, int size) { if (Delay > 0) Thread.Sleep(Delay); return Data.AsSpan(checked((int)(address - Base)), size).ToArray(); }
        public void Write(ulong address, byte[] data) { if (!writable) throw new InvalidOperationException(); data.CopyTo(Data, checked((int)(address - Base))); }
        public void EnsureAlive() { }
        public void Dispose() { }
    }
}
