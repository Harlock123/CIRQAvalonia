using System.Globalization;

namespace Cirq.Components.Digital;

/// <summary>
/// The bytes of a ROM or RAM image, read from the files assemblers and EPROM programmers write: a
/// raw binary, or Intel HEX. <see cref="ParallelMemory.Load(MemoryImage)"/> puts one into a part.
/// <para>
/// <b>Binary</b> is the bytes and nothing else, so it says nothing about where they go. The first
/// byte goes to the chip's address <c>offset</c> (zero unless asked otherwise), which is what an
/// EPROM programmer does with it — and why an assembler for a ROM at $8000 writes a 32K file
/// starting with the byte for $8000.
/// </para>
/// <para>
/// <b>Intel HEX</b> carries its addresses with it. Every record type an 8-bit image needs is read:
/// data (00), end of file (01), extended segment address (02) and extended linear address (04);
/// the two start-address records (03, 05) say where a program begins rather than what is in the
/// memory, and are accepted and ignored. Every checksum is checked, and anything wrong is reported
/// with the line it is on, because the commonest fault in a hex file is one line of it.
/// </para>
/// </summary>
/// <param name="Blocks">Runs of bytes and the address each starts at, in the file's order.</param>
/// <param name="Format">"binary" or "Intel HEX".</param>
public sealed record MemoryImage(IReadOnlyList<(int Address, byte[] Bytes)> Blocks, string Format)
{
    /// <summary>The largest image read: a 64K address space, as an 8-bit processor sees.</summary>
    public const int MaximumBytes = 1 << 20;

    /// <summary>How many bytes are in it.</summary>
    public int ByteCount => Blocks.Sum(b => b.Bytes.Length);

    /// <summary>Reads a file by its extension: <c>.hex</c>, <c>.ihx</c> and <c>.ihex</c> as Intel HEX, anything else as binary.</summary>
    /// <param name="path">The file.</param>
    /// <param name="offset">Where a binary file's first byte goes. Ignored for HEX, which has its own addresses.</param>
    public static MemoryImage Read(string path, int offset = 0)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"There is no file {path}.", path);
        if (info.Length > MaximumBytes * 4L)
            throw new InvalidDataException($"{info.Name} is {info.Length / 1024} KB — far bigger than any memory here holds.");

        return IsHex(path)
            ? FromIntelHex(File.ReadAllText(path))
            : FromBinary(File.ReadAllBytes(path), offset);
    }

    /// <summary>True for the extensions read as Intel HEX.</summary>
    public static bool IsHex(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".hex" or ".ihx" or ".ihex";

    /// <summary>A binary image: the bytes, the first of them at <paramref name="offset"/>.</summary>
    public static MemoryImage FromBinary(byte[] bytes, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "The load offset cannot be negative.");
        if (bytes.Length == 0) throw new InvalidDataException("The file is empty.");
        if (bytes.Length > MaximumBytes) throw new InvalidDataException($"The file is {bytes.Length} bytes, more than any memory here holds.");

        return new MemoryImage([(offset, bytes)], "binary");
    }

    /// <summary>Parses Intel HEX text. See the class remarks for what is accepted.</summary>
    public static MemoryImage FromIntelHex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<(int, byte[])> blocks = [];
        var upper = 0;
        var sawEnd = false;
        var total = 0;
        var lineNumber = 0;

        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (sawEnd)
                throw new InvalidDataException($"Line {lineNumber}: there is more after the end-of-file record.");

            if (line[0] != ':')
                throw new InvalidDataException($"Line {lineNumber}: a record starts with ':', and this starts with '{line[0]}'.");

            if ((line.Length - 1) % 2 != 0 || line.Length < 11)
                throw new InvalidDataException($"Line {lineNumber}: too short or an odd number of digits for a record.");

            var bytes = new byte[(line.Length - 1) / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(line.AsSpan(1 + (i * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    throw new InvalidDataException($"Line {lineNumber}: '{line.Substring(1 + (i * 2), 2)}' is not a hexadecimal byte.");
            }

            var count = bytes[0];
            if (bytes.Length != count + 5)
                throw new InvalidDataException($"Line {lineNumber}: the record says {count} data bytes but has {bytes.Length - 5}.");

            var sum = 0;
            foreach (var b in bytes) sum += b;
            if ((sum & 0xFF) != 0)
            {
                var expected = (0x100 - ((sum - bytes[^1]) & 0xFF)) & 0xFF;
                throw new InvalidDataException($"Line {lineNumber}: the checksum is {bytes[^1]:X2}, and the record adds up to {expected:X2}.");
            }

            var address = (bytes[1] << 8) | bytes[2];
            var type = bytes[3];
            var payload = bytes.AsSpan(4, count);

            switch (type)
            {
                case 0x00:
                    total += count;
                    if (total > MaximumBytes) throw new InvalidDataException($"Line {lineNumber}: the image is more than any memory here holds.");
                    if (count > 0) blocks.Add((upper + address, payload.ToArray()));
                    break;

                case 0x01:
                    sawEnd = true;
                    break;

                case 0x02:
                    if (count != 2) throw new InvalidDataException($"Line {lineNumber}: an extended segment address has two bytes.");
                    upper = ((payload[0] << 8) | payload[1]) << 4;
                    break;

                case 0x04:
                    if (count != 2) throw new InvalidDataException($"Line {lineNumber}: an extended linear address has two bytes.");
                    upper = ((payload[0] << 8) | payload[1]) << 16;
                    break;

                case 0x03 or 0x05:
                    // Where execution starts, which a memory has no use for.
                    break;

                default:
                    throw new InvalidDataException($"Line {lineNumber}: record type {type:X2} is not an Intel HEX record.");
            }
        }

        if (blocks.Count == 0) throw new InvalidDataException("The file has no data records in it.");

        return new MemoryImage(blocks, "Intel HEX");
    }

    /// <summary>
    /// Writes bytes as Intel HEX, sixteen to a record, for a test or for saving an image back out.
    /// </summary>
    public static string ToIntelHex(int address, byte[] bytes)
    {
        var builder = new System.Text.StringBuilder();
        var upper = -1;

        for (var at = 0; at < bytes.Length; at += 16)
        {
            var absolute = address + at;
            if (absolute >> 16 != upper)
            {
                upper = absolute >> 16;
                Record(builder, 0, 0x04, [(byte)(upper >> 8), (byte)upper]);
            }

            Record(builder, absolute & 0xFFFF, 0x00, bytes.AsSpan(at, Math.Min(16, bytes.Length - at)));
        }

        Record(builder, 0, 0x01, []);
        return builder.ToString();
    }

    private static void Record(System.Text.StringBuilder builder, int address, byte type, ReadOnlySpan<byte> data)
    {
        var sum = data.Length + (address >> 8) + (address & 0xFF) + type;
        builder.Append(CultureInfo.InvariantCulture, $":{data.Length:X2}{address:X4}{type:X2}");
        foreach (var b in data)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{b:X2}");
            sum += b;
        }

        builder.Append(CultureInfo.InvariantCulture, $"{(0x100 - (sum & 0xFF)) & 0xFF:X2}\n");
    }
}
