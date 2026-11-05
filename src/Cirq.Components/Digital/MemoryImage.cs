using System.Globalization;

namespace Cirq.Components.Digital;

/// <summary>
/// Which bytes of an image a byte-wide chip takes. A 68000's bus is sixteen bits wide: the byte at
/// an even address travels on D8-D15 (/UDS) and the one at the odd address after it on D0-D7
/// (/LDS), so its ROM is a pair of byte-wide chips, one holding the even bytes and one the odd.
/// </summary>
public enum ByteLane
{
    /// <summary>Every byte, as an 8-bit processor's memory takes them.</summary>
    Both,

    /// <summary>The even addresses — the upper byte, D8-D15, selected by /UDS.</summary>
    Even,

    /// <summary>The odd addresses — the lower byte, D0-D7, selected by /LDS.</summary>
    Odd,
}

/// <summary>
/// The bytes of a ROM or RAM image, read from the files assemblers and EPROM programmers write: a
/// raw binary, Intel HEX, or Motorola S-records. <see cref="ParallelMemory.Load(MemoryImage)"/>
/// puts one into a part, and <see cref="Lane"/> picks the half of a 16-bit image a chip holds.
/// <para>
/// <b>Binary</b> is the bytes and nothing else, so it says nothing about where they go. The first
/// byte goes to the chip's address <c>offset</c> (zero unless asked otherwise), which is what an
/// EPROM programmer does with it — and why an assembler for a ROM at $8000 writes a 32K file
/// starting with the byte for $8000.
/// </para>
/// <para>
/// <b>S-records</b> (S19, S28, S37 — the 68000's own format) carry theirs too: S1, S2 and S3 data
/// records with 16-, 24- and 32-bit addresses, the S0 header, the S5 and S6 counts and the S7, S8
/// and S9 start addresses, every checksum checked.
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
/// <param name="Format">"binary", "Intel HEX" or "S-record", and which lane if split.</param>
public sealed record MemoryImage(IReadOnlyList<(int Address, byte[] Bytes)> Blocks, string Format)
{
    /// <summary>The largest image read: a 64K address space, as an 8-bit processor sees.</summary>
    public const int MaximumBytes = 1 << 20;

    /// <summary>How many bytes are in it.</summary>
    public int ByteCount => Blocks.Sum(b => b.Bytes.Length);

    /// <summary>
    /// Reads a file by its extension: <c>.hex</c>, <c>.ihx</c> and <c>.ihex</c> as Intel HEX;
    /// <c>.s19</c>, <c>.s28</c>, <c>.s37</c>, <c>.srec</c>, <c>.mot</c> and <c>.sx</c> as S-records;
    /// anything else as binary.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="offset">Where a binary file's first byte goes. Ignored for HEX, which has its own addresses.</param>
    public static MemoryImage Read(string path, int offset = 0)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"There is no file {path}.", path);
        if (info.Length > MaximumBytes * 4L)
            throw new InvalidDataException($"{info.Name} is {info.Length / 1024} KB — far bigger than any memory here holds.");

        if (IsHex(path)) return FromIntelHex(File.ReadAllText(path));
        if (IsSRecord(path)) return FromSRecords(File.ReadAllText(path));
        return FromBinary(File.ReadAllBytes(path), offset);
    }

    /// <summary>True for the extensions read as Intel HEX.</summary>
    public static bool IsHex(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".hex" or ".ihx" or ".ihex";

    /// <summary>True for the extensions read as Motorola S-records.</summary>
    public static bool IsSRecord(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".s19" or ".s28" or ".s37" or ".srec" or ".mot" or ".sx";

    /// <summary>True for a file with its own addresses in it, which needs no load offset.</summary>
    public static bool HasAddresses(string path) => IsHex(path) || IsSRecord(path);

    /// <summary>
    /// The half of a 16-bit image one byte-wide chip of a pair holds: the even bytes or the odd,
    /// each at half its address — so byte $000400 of a 68000 program is byte $200 of the even ROM,
    /// and $000401 byte $200 of the odd one. <see cref="ByteLane.Both"/> gives the image back.
    /// </summary>
    public MemoryImage Lane(ByteLane lane)
    {
        if (lane == ByteLane.Both) return this;

        var wanted = lane == ByteLane.Odd ? 1 : 0;
        List<(int, byte[])> blocks = [];
        List<byte> run = [];
        var runStart = -1;
        var next = -1;

        foreach (var (start, bytes) in Blocks)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                var address = start + i;
                if ((address & 1) != wanted) continue;
                var half = address >> 1;
                if (half != next && run.Count > 0)
                {
                    blocks.Add((runStart, [.. run]));
                    run.Clear();
                }

                if (run.Count == 0) runStart = half;
                run.Add(bytes[i]);
                next = half + 1;
            }
        }

        if (run.Count > 0) blocks.Add((runStart, [.. run]));
        if (blocks.Count == 0)
            throw new InvalidDataException($"The image has no bytes at {(lane == ByteLane.Odd ? "odd" : "even")} addresses.");

        return new MemoryImage(blocks, $"{Format}, {(lane == ByteLane.Odd ? "odd" : "even")} bytes");
    }

    /// <summary>
    /// Parses Motorola S-records. See the class remarks for the record types read; a line that is
    /// not one is reported with its number.
    /// </summary>
    public static MemoryImage FromSRecords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<(int, byte[])> blocks = [];
        var total = 0;
        var lineNumber = 0;
        var sawEnd = false;

        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (sawEnd)
                throw new InvalidDataException($"Line {lineNumber}: there is more after the termination record.");

            if (line.Length < 4 || (line[0] != 'S' && line[0] != 's') || !char.IsDigit(line[1]))
                throw new InvalidDataException($"Line {lineNumber}: an S-record starts with S and a digit, and this starts with '{line[..Math.Min(2, line.Length)]}'.");

            var type = line[1] - '0';
            if ((line.Length - 2) % 2 != 0)
                throw new InvalidDataException($"Line {lineNumber}: an odd number of digits for a record.");

            var bytes = new byte[(line.Length - 2) / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(line.AsSpan(2 + (i * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    throw new InvalidDataException($"Line {lineNumber}: '{line.Substring(2 + (i * 2), 2)}' is not a hexadecimal byte.");
            }

            var count = bytes[0];
            if (bytes.Length != count + 1)
                throw new InvalidDataException($"Line {lineNumber}: the record says {count} bytes follow but {bytes.Length - 1} do.");

            var sum = 0;
            for (var i = 0; i < bytes.Length - 1; i++) sum += bytes[i];
            var expected = (byte)~sum;
            if (bytes[^1] != expected)
                throw new InvalidDataException($"Line {lineNumber}: the checksum is {bytes[^1]:X2}, and the record adds up to {expected:X2}.");

            var addressBytes = type switch
            {
                0 or 1 or 5 or 9 => 2,
                2 or 8 => 3,
                3 or 7 => 4,
                6 => 3,
                _ => throw new InvalidDataException($"Line {lineNumber}: S{type} is not a record type."),
            };

            if (count < addressBytes + 1)
                throw new InvalidDataException($"Line {lineNumber}: the record is too short for its address.");

            var address = 0;
            for (var i = 0; i < addressBytes; i++) address = (address << 8) | bytes[1 + i];
            var data = bytes.AsSpan(1 + addressBytes, count - addressBytes - 1);

            switch (type)
            {
                case 1 or 2 or 3:
                    total += data.Length;
                    if (total > MaximumBytes) throw new InvalidDataException($"Line {lineNumber}: the image is more than any memory here holds.");
                    if (data.Length > 0) blocks.Add((address, data.ToArray()));
                    break;

                case 7 or 8 or 9:
                    // Where execution starts, which a memory has no use for.
                    sawEnd = true;
                    break;

                default:
                    // S0, the header, and S5/S6, the record counts.
                    break;
            }
        }

        if (blocks.Count == 0) throw new InvalidDataException("The file has no data records in it.");

        return new MemoryImage(blocks, "S-record");
    }

    /// <summary>Writes bytes as S-records — S2 for a 24-bit address, 32 bytes to a record, and an S8 to end.</summary>
    public static string ToSRecords(int address, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var builder = new System.Text.StringBuilder();
        SRecord(builder, 0, 0, "CIRQ"u8);
        for (var at = 0; at < bytes.Length; at += 32)
            SRecord(builder, 2, address + at, bytes.AsSpan(at, Math.Min(32, bytes.Length - at)));
        SRecord(builder, 8, address, []);
        return builder.ToString();
    }

    private static void SRecord(System.Text.StringBuilder builder, int type, int address, ReadOnlySpan<byte> data)
    {
        var addressBytes = type is 2 or 8 ? 3 : 2;
        var count = addressBytes + data.Length + 1;
        var sum = count;
        builder.Append(CultureInfo.InvariantCulture, $"S{type}{count:X2}");
        for (var i = addressBytes - 1; i >= 0; i--)
        {
            var b = (address >> (i * 8)) & 0xFF;
            sum += b;
            builder.Append(CultureInfo.InvariantCulture, $"{b:X2}");
        }

        foreach (var b in data)
        {
            sum += b;
            builder.Append(CultureInfo.InvariantCulture, $"{b:X2}");
        }

        builder.Append(CultureInfo.InvariantCulture, $"{(byte)~sum:X2}\n");
    }

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
