namespace Htpc.Launcher;

/// <summary>
/// Just enough protobuf for the Android TV Remote protocol (varints and length-delimited fields),
/// written by hand rather than a generated library: a dozen small messages, checked against
/// golden bytes in TvLab.
/// </summary>
sealed class ProtoWriter
{
    readonly MemoryStream buffer = new();

    public ProtoWriter Varint(int field, long value) { Tag(field, 0); Raw((ulong)value); return this; }
    public ProtoWriter Bool(int field, bool value) => Varint(field, value ? 1 : 0);
    public ProtoWriter String(int field, string value) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));
    public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

    public ProtoWriter Bytes(int field, byte[] value)
    {
        Tag(field, 2);
        Raw((ulong)value.Length);
        buffer.Write(value);
        return this;
    }

    void Tag(int field, int wireType) => Raw((ulong)(field << 3 | wireType));

    void Raw(ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            buffer.WriteByte(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
    }

    public byte[] ToArray() => buffer.ToArray();

    /// <summary>The message with its varint length in front (how both Android TV channels frame them).</summary>
    public byte[] Framed()
    {
        var body = ToArray();
        var framed = new ProtoWriter();
        framed.Raw((ulong)body.Length);
        framed.buffer.Write(body);
        return framed.ToArray();
    }
}

/// <summary>A decoded message: its fields by number (varints as longs, length-delimited as bytes).</summary>
sealed class ProtoMessage
{
    readonly List<(int Field, long Value, byte[]? Bytes)> fields = new();

    /// <summary>Null for bytes that are not a well-formed message.</summary>
    public static ProtoMessage? Parse(ReadOnlySpan<byte> data)
    {
        var m = new ProtoMessage();
        var i = 0;
        while (i < data.Length)
        {
            if (!ReadVarint(data, ref i, out var tag)) return null;
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0:
                    if (!ReadVarint(data, ref i, out var v)) return null;
                    m.fields.Add((field, (long)v, null));
                    break;
                case 2:
                    if (!ReadVarint(data, ref i, out var len) || len > (ulong)(data.Length - i)) return null;
                    m.fields.Add((field, 0, data.Slice(i, (int)len).ToArray()));
                    i += (int)len;
                    break;
                case 5: if (data.Length - i < 4) return null; i += 4; break;
                case 1: if (data.Length - i < 8) return null; i += 8; break;
                default: return null;
            }
        }
        return m;
    }

    public static bool ReadVarint(ReadOnlySpan<byte> data, ref int i, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    public bool Has(int field) => fields.Any(f => f.Field == field);
    public long Varint(int field) => fields.FirstOrDefault(f => f.Field == field && f.Bytes is null).Value;
    public byte[]? Bytes(int field) => fields.FirstOrDefault(f => f.Field == field && f.Bytes is not null).Bytes;
    public ProtoMessage? Message(int field) => Bytes(field) is { } b ? Parse(b) : null;
    public string String(int field) => Bytes(field) is { } b ? System.Text.Encoding.UTF8.GetString(b) : "";

    /// <summary>Reads one framed message from a stream; null at the end, or past <paramref name="max"/> bytes.</summary>
    public static async Task<ProtoMessage?> ReadFramed(Stream stream, CancellationToken cancel, int max = 64 * 1024)
    {
        ulong length = 0;
        var one = new byte[1];
        for (var shift = 0; ; shift += 7)
        {
            if (shift >= 35 || await stream.ReadAsync(one, cancel) == 0) return null;
            length |= (ulong)(one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
        }
        if (length > (ulong)max) return null;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancel);
        return Parse(body);
    }
}
