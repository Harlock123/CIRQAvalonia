using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cirq.Components.Digital;
using Cirq.Core.Topology;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// The tools for memory parts: loading a ROM or RAM image into one, and reading back what is in it.
/// </summary>
public sealed partial class CircuitTools
{
    /// <summary>The most bytes read_memory returns in one call.</summary>
    public const int MaxMemoryRead = 4096;

    [McpServerTool(Name = "load_memory", Destructive = true)]
    [Description("Loads a ROM or RAM image into a memory part (an EPROM, the 28C256 EEPROM, a 6116 or 62256 RAM, the 2K SRAM/ROM), in one undo step. Give exactly one of: hex (Intel HEX text, or plain hex bytes such as \"A9 01 8D 00 60\"), base64 (a raw binary), or path (a .bin, .rom or .hex file in the working folder). Intel HEX carries its own addresses; binary and plain bytes start at offset. Addresses beyond the chip wrap into it, as with its upper address lines unwired, so a 6502 image assembled at $8000 lands at the start of a 32K ROM. The image becomes the part's preset: it's saved in the circuit and every run starts from it.")]
    public Task<string> LoadMemory(
        [Description("The memory part's designator, e.g. \"U2\".")] string part,
        [Description("Intel HEX text (lines starting ':'), or bytes as hex separated by spaces. Optional.")] string? hex = null,
        [Description("A raw binary image, base64-encoded. Optional.")] string? base64 = null,
        [Description("A .bin, .rom, .hex or .ihx file, relative to the working folder. Optional.")] string? path = null,
        [Description("Where in the chip a binary's (or plain bytes') first byte goes, e.g. 0 or 1792. Default 0.")] int offset = 0,
        CancellationToken cancel = default)
    {
        return Edit($"Load memory {part}", s =>
        {
            var given = new[] { hex, base64, path }.Count(x => !string.IsNullOrWhiteSpace(x));
            if (given != 1) throw new ArgumentException("Give exactly one of hex, base64 or path.");
            if (offset < 0) throw new ArgumentException("offset can't be negative.");

            var memory = Memory(s.Circuit, part);
            MemoryImage image;
            string from;

            if (!string.IsNullOrWhiteSpace(path))
            {
                var full = _files.Resolve(path, "A memory image", ".bin", ".rom", ".hex", ".ihx", ".ihex");
                if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {path} in the working folder ({_files.Root}).");
                image = MemoryImage.Read(full, offset);
                from = Path.GetFileName(full);
            }
            else if (!string.IsNullOrWhiteSpace(base64))
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64.Trim()); }
                catch (FormatException) { throw new ArgumentException("base64 isn't valid base64 text."); }
                image = MemoryImage.FromBinary(bytes, offset);
                from = "base64";
            }
            else
            {
                var text = hex!.Trim();
                image = text.StartsWith(':') ? MemoryImage.FromIntelHex(text) : MemoryImage.FromBinary(PlainBytes(text), offset);
                from = "hex";
            }

            var note = memory.Load(image);
            return new JsonObject
            {
                ["part"] = memory.Name,
                ["chip"] = memory.PartNumber,
                ["size"] = memory.Size,
                ["from"] = from,
                ["format"] = image.Format,
                ["loaded"] = note,
            };
        }, cancel);
    }

    [McpServerTool(Name = "read_memory", ReadOnly = true, Idempotent = true)]
    [Description("Reads bytes out of a memory part as a hex dump, 16 to a row, by the chip's own addresses (0 up to its size). In the app this is the memory as the running circuit has it, writes included; otherwise what was loaded or typed in, or what the last run_transient left there.")]
    public Task<string> ReadMemory(
        [Description("The memory part's designator, e.g. \"U3\".")] string part,
        [Description("The first address in the chip, e.g. 0 or 32752. Default 0.")] int start = 0,
        [Description("How many bytes, from 1 to 4096. Default 256.")] int length = 256,
        CancellationToken cancel = default)
    {
        return Read("read_memory", s =>
        {
            if (length is < 1 or > MaxMemoryRead) throw new ArgumentException($"length must be from 1 to {MaxMemoryRead}.");
            if (start < 0) throw new ArgumentException("start can't be negative.");
            var memory = Memory(s.Circuit, part);
            if (start >= memory.Size) throw new ArgumentException($"{memory.Name} is a {memory.Size}-byte {memory.PartNumber}: start must be below {memory.Size}.");
            var end = Math.Min(start + length, memory.Size);
            var rows = new JsonArray();
            for (var row = start; row < end; row += 16)
            {
                var line = new StringBuilder(row.ToString("X4", CultureInfo.InvariantCulture)).Append(':');
                for (var a = row; a < Math.Min(row + 16, end); a++) line.Append(' ').Append(memory.ReadByte(a).ToString("X2", CultureInfo.InvariantCulture));
                rows.Add(line.ToString());
            }

            return new JsonObject
            {
                ["part"] = memory.Name,
                ["chip"] = memory.PartNumber,
                ["size"] = memory.Size,
                ["used_bytes"] = memory.UsedBytes,
                ["rows"] = rows,
            };
        }, cancel);
    }

    /// <summary>The memory part called <paramref name="name"/>, or an error saying which parts are memories.</summary>
    private static ParallelMemory Memory(Circuit circuit, string name)
    {
        var c = Pins.Part(circuit, name);
        if (c is ParallelMemory memory) return memory;
        var memories = circuit.Components.OfType<ParallelMemory>().Select(m => m.Name).ToList();
        throw new ArgumentException($"{c.Name} is a {c.ComponentType}, not a memory. {(memories.Count == 0 ? "The circuit has no memory parts." : $"The memories: {string.Join(", ", memories)}.")}");
    }

    /// <summary>Bytes written as hex with spaces, commas or nothing between them.</summary>
    private static byte[] PlainBytes(string text)
    {
        var digits = new string([.. text.Where(c => !char.IsWhiteSpace(c) && c != ',')]);
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) digits = digits[2..];
        if (digits.Length == 0 || digits.Length % 2 != 0 || !digits.All(Uri.IsHexDigit))
            throw new ArgumentException("hex must be Intel HEX text (starting ':') or whole bytes in hexadecimal, such as \"A9 01 8D 00 60\".");
        return Convert.FromHexString(digits);
    }
}
