namespace Htpc.Launcher;

/// <summary>
/// What goes through the pipe between the launcher and the elevated input helper (InputHelper,
/// ElevatedInput): frames of SendInput records. A frame is the length of its records in bytes
/// (int32, little endian), then the records, as Input made them. The helper takes keyboard and
/// mouse records only, a few at a time: it is an elevated process answering a standard one.
/// </summary>
static class InputFrame
{
    /// <summary>sizeof(INPUT) on x64 (Input checks its struct against it).</summary>
    public const int RecordSize = 40;

    /// <summary>Most records in one frame: a typed word is the longest the launcher sends.</summary>
    public const int MaxRecords = 256;

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

    public static byte[] Encode(ReadOnlySpan<byte> records)
    {
        var frame = new byte[4 + records.Length];
        BitConverter.TryWriteBytes(frame, records.Length);
        records.CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// <summary>Whether a frame's records are ones the helper sends: whole records, not too many, each keyboard or mouse (never hardware input).</summary>
    public static bool Valid(ReadOnlySpan<byte> records)
    {
        if (records.Length == 0 || records.Length % RecordSize != 0 || records.Length / RecordSize > MaxRecords) return false;
        for (var at = 0; at < records.Length; at += RecordSize)
        {
            var type = BitConverter.ToUInt32(records.Slice(at, 4));
            if (type != INPUT_MOUSE && type != INPUT_KEYBOARD) return false;
        }
        return true;
    }

    /// <summary>The next frame's records from the stream; null at its end, or on a frame that is not valid (the connection is then dropped).</summary>
    public static byte[]? Read(Stream stream)
    {
        var head = new byte[4];
        if (!ReadAll(stream, head)) return null;
        var length = BitConverter.ToInt32(head);
        if (length <= 0 || length > MaxRecords * RecordSize) return null;
        var records = new byte[length];
        return ReadAll(stream, records) && Valid(records) ? records : null;
    }

    static bool ReadAll(Stream stream, byte[] buffer)
    {
        for (var got = 0; got < buffer.Length;)
        {
            var n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}
