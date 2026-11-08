using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MemoryForge.Core;

public enum ValueKind { Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, Utf8, Utf16, Bytes }
public enum ScanComparison { Exact, NotEqual, Changed, Unchanged, Increased, Decreased, IncreasedBy, DecreasedBy, GreaterThan, LessThan }

public static class ValueCodec
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public static int Width(ValueKind kind) => kind switch
    {
        ValueKind.Int8 or ValueKind.UInt8 => 1,
        ValueKind.Int16 or ValueKind.UInt16 => 2,
        ValueKind.Int32 or ValueKind.UInt32 or ValueKind.Float32 => 4,
        ValueKind.Int64 or ValueKind.UInt64 or ValueKind.Float64 => 8,
        _ => throw new ArgumentException("Text and byte values require an explicit length or search value.")
    };

    public static byte[] Encode(ValueKind kind, string value, bool bigEndian = false)
    {
        var data = kind switch
        {
            ValueKind.Int8 => [unchecked((byte)sbyte.Parse(value, Culture))],
            ValueKind.UInt8 => new[] { byte.Parse(value, Culture) },
            ValueKind.Int16 => BitConverter.GetBytes(short.Parse(value, Culture)),
            ValueKind.UInt16 => BitConverter.GetBytes(ushort.Parse(value, Culture)),
            ValueKind.Int32 => BitConverter.GetBytes(int.Parse(value, Culture)),
            ValueKind.UInt32 => BitConverter.GetBytes(uint.Parse(value, Culture)),
            ValueKind.Int64 => BitConverter.GetBytes(long.Parse(value, Culture)),
            ValueKind.UInt64 => BitConverter.GetBytes(ulong.Parse(value, Culture)),
            ValueKind.Float32 => BitConverter.GetBytes(float.Parse(value, Culture)),
            ValueKind.Float64 => BitConverter.GetBytes(double.Parse(value, Culture)),
            ValueKind.Utf8 => Encoding.UTF8.GetBytes(value),
            ValueKind.Utf16 => (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetBytes(value),
            ValueKind.Bytes => Convert.FromHexString(string.Concat(value.Where(c => !char.IsWhiteSpace(c)))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (data.Length is < 1 or > 4096) throw new ArgumentException("Encoded value must be 1..4096 bytes.");
        if (bigEndian && kind is not (ValueKind.Bytes or ValueKind.Utf8 or ValueKind.Utf16)) Array.Reverse(data);
        return data;
    }

    public static string Decode(ValueKind kind, ReadOnlySpan<byte> data, bool bigEndian = false)
    {
        if (kind == ValueKind.Bytes) return Convert.ToHexString(data);
        if (kind == ValueKind.Utf8) return Encoding.UTF8.GetString(data);
        if (kind == ValueKind.Utf16) return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(data);
        if (data.Length != Width(kind)) throw new ArgumentException("Numeric byte count does not match type.");
        Span<byte> bytes = stackalloc byte[data.Length];
        data.CopyTo(bytes);
        if (bigEndian == BitConverter.IsLittleEndian) bytes.Reverse();
        return kind switch
        {
            ValueKind.Int8 => unchecked((sbyte)bytes[0]).ToString(Culture),
            ValueKind.UInt8 => bytes[0].ToString(Culture),
            ValueKind.Int16 => BitConverter.ToInt16(bytes).ToString(Culture),
            ValueKind.UInt16 => BitConverter.ToUInt16(bytes).ToString(Culture),
            ValueKind.Int32 => BitConverter.ToInt32(bytes).ToString(Culture),
            ValueKind.UInt32 => BitConverter.ToUInt32(bytes).ToString(Culture),
            ValueKind.Int64 => BitConverter.ToInt64(bytes).ToString(Culture),
            ValueKind.UInt64 => BitConverter.ToUInt64(bytes).ToString(Culture),
            ValueKind.Float32 => BitConverter.ToSingle(bytes).ToString("R", Culture),
            ValueKind.Float64 => BitConverter.ToDouble(bytes).ToString("R", Culture),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public static bool Matches(ValueKind kind, ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous,
        ScanComparison comparison, byte[]? target, bool bigEndian)
    {
        if (comparison == ScanComparison.Changed) return !current.SequenceEqual(previous);
        if (comparison == ScanComparison.Unchanged) return current.SequenceEqual(previous);
        if (comparison == ScanComparison.Exact) return current.SequenceEqual(target);
        if (comparison == ScanComparison.NotEqual) return !current.SequenceEqual(target);
        if (kind is ValueKind.Bytes or ValueKind.Utf8 or ValueKind.Utf16) throw new ArgumentException("Ordered comparisons require numeric types.");
        // Decimal preserves every Int64/UInt64 value, avoiding the precision loss of doubles.
        // Floating-point types use IEEE arithmetic; NaN never satisfies an ordered comparison.
        if (kind is ValueKind.Float32 or ValueKind.Float64)
        {
            var now = double.Parse(Decode(kind, current, bigEndian), Culture);
            var old = previous.IsEmpty ? 0 : double.Parse(Decode(kind, previous, bigEndian), Culture);
            var wanted = target is null ? 0 : double.Parse(Decode(kind, target, bigEndian), Culture);
            return Compare(now, old, wanted, comparison);
        }
        var n = decimal.Parse(Decode(kind, current, bigEndian), Culture);
        var p = previous.IsEmpty ? 0 : decimal.Parse(Decode(kind, previous, bigEndian), Culture);
        var t = target is null ? 0 : decimal.Parse(Decode(kind, target, bigEndian), Culture);
        return comparison switch
        {
            ScanComparison.Increased => n > p,
            ScanComparison.Decreased => n < p,
            ScanComparison.GreaterThan => n > t,
            ScanComparison.LessThan => n < t,
            ScanComparison.IncreasedBy => n - p == t,
            ScanComparison.DecreasedBy => p - n == t,
            _ => false
        };
    }

    private static bool Compare(double n, double p, double t, ScanComparison c) => c switch
    {
        ScanComparison.Increased => n > p,
        ScanComparison.Decreased => n < p,
        ScanComparison.GreaterThan => n > t,
        ScanComparison.LessThan => n < t,
        ScanComparison.IncreasedBy => n - p == t,
        ScanComparison.DecreasedBy => p - n == t,
        _ => false
    };

    public static ulong Pointer(byte[] data, bool bigEndian) => data.Length switch
    {
        4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data),
        8 => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadUInt64LittleEndian(data),
        _ => throw new ArgumentException("Pointer width must be 4 or 8 bytes.")
    };
}

public sealed class BytePattern
{
    private readonly byte[] values;
    private readonly byte[] masks;
    public int Length => values.Length;
    public BytePattern(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 256) throw new ArgumentException("Pattern must contain 1..256 space-separated bytes, with optional ? nibbles (48 8B ?? ?F).");
        values = new byte[tokens.Length];
        masks = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i] == "?" ? "??" : tokens[i];
            if (token.Length != 2) throw new FormatException("Each pattern token must have two hex digits or ? wildcards.");
            for (var n = 0; n < 2; n++)
                if (token[n] != '?')
                {
                    var nibble = byte.Parse(token[n].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    values[i] |= (byte)(nibble << (4 * (1 - n)));
                    masks[i] |= (byte)(0xF << (4 * (1 - n)));
                }
        }
    }
    public bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length) return false;
        for (var i = 0; i < Length; i++) if ((data[i] & masks[i]) != values[i]) return false;
        return true;
    }
}
