namespace Cirq.Core.Audio;

/// <summary>
/// A minimal WAV reader and writer, written out here rather than taken from a package.
/// <para>
/// The reason is the same one that produced the hand-written BMP encoder in the exporter: a
/// dependency that exists to write forty-four bytes of header is a dependency that has to be
/// version-pinned, shipped for six runtime identifiers and kept working, in exchange for code
/// anybody can read in one sitting. RIFF/WAVE has not changed since 1991.
/// </para>
/// <para>
/// Writing is one format — 16-bit signed PCM, mono — because that is what everything plays.
/// Reading is deliberately more generous, because the files come from whatever the person had to
/// hand: 8, 16, 24 and 32-bit PCM and 32-bit float are all accepted, and anything with more than
/// one channel is mixed down, since a circuit has one node and not two.
/// </para>
/// </summary>
public static class WaveFile
{
    /// <summary>What a sample is scaled to. Samples outside it are clipped, as they would be.</summary>
    public const double FullScale = 1.0;

    /// <summary>
    /// Writes mono 16-bit PCM. Samples are in [-1, 1] and anything beyond is clipped rather than
    /// wrapped — wrapping turns a slightly loud recording into white noise.
    /// </summary>
    public static void Write(string path, IReadOnlyList<double> samples, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var dataBytes = samples.Count * 2;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                           // PCM header length
        writer.Write((short)1);                     // format: integer PCM
        writer.Write((short)1);                     // channels
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);               // bytes per second
        writer.Write((short)2);                     // bytes per frame
        writer.Write((short)16);                    // bits per sample

        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue));
    }

    /// <summary>
    /// Reads any of the common uncompressed layouts, mixed down to mono and scaled to [-1, 1].
    /// </summary>
    public static (double[] Samples, int SampleRate) Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        if (!Tag(reader).SequenceEqual("RIFF"u8.ToArray())) throw new InvalidDataException("not a RIFF file");

        reader.ReadInt32();

        if (!Tag(reader).SequenceEqual("WAVE"u8.ToArray())) throw new InvalidDataException("not a WAVE file");

        short format = 0, channels = 0, bits = 0;
        var sampleRate = 0;
        byte[]? data = null;

        // Walk the chunks rather than assuming fmt is followed by data: plenty of writers put a
        // LIST or a fact chunk in between, and a reader that assumes the layout reads noise.
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Tag(reader);
            var length = reader.ReadInt32();

            if (length < 0 || stream.Position + length > stream.Length)
                length = (int)(stream.Length - stream.Position);

            if (id.SequenceEqual("fmt "u8.ToArray()))
            {
                format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();                 // bytes per second, recomputable
                reader.ReadInt16();                 // bytes per frame, likewise
                bits = reader.ReadInt16();

                if (length > 16) stream.Position += length - 16;
            }
            else if (id.SequenceEqual("data"u8.ToArray()))
            {
                data = reader.ReadBytes(length);
            }
            else
            {
                stream.Position += length;
            }

            // Chunks are word-aligned, and an odd length is followed by a pad byte.
            if ((length & 1) == 1 && stream.Position < stream.Length) stream.Position++;
        }

        if (data is null || channels < 1 || sampleRate < 1)
            throw new InvalidDataException("the file has no format or no audio in it");

        // 1 is integer PCM and 3 is IEEE float. 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real
        // format lives in a GUID; for the layouts here the bit depth is enough to tell them apart.
        if (format is not (1 or 3 or -2))
            throw new InvalidDataException($"compressed WAV files are not supported (format {format})");

        var isFloat = format == 3 || (format == -2 && bits == 32 && LooksLikeFloat(data, bits));

        return (Mix(data, channels, bits, isFloat), sampleRate);
    }

    private static byte[] Tag(BinaryReader reader) => reader.ReadBytes(4);

    /// <summary>
    /// Thirty-two-bit extensible files are usually float, but not always. Integer samples that
    /// size use the whole range; float ones stay inside ±1, which in IEEE-754 puts a hard ceiling
    /// on the exponent byte. That is enough to tell them apart without parsing the GUID.
    /// </summary>
    private static bool LooksLikeFloat(byte[] data, int bits)
    {
        if (bits != 32) return false;

        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            var value = BitConverter.ToSingle(data, i);
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 4.0) return false;
        }

        return true;
    }

    private static double[] Mix(byte[] data, int channels, int bits, bool isFloat)
    {
        var bytesPerSample = Math.Max(bits / 8, 1);
        var frameBytes = bytesPerSample * channels;
        var frames = frameBytes == 0 ? 0 : data.Length / frameBytes;

        var samples = new double[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var total = 0.0;

            for (var channel = 0; channel < channels; channel++)
                total += One(data, (frame * frameBytes) + (channel * bytesPerSample), bits, isFloat);

            samples[frame] = total / channels;
        }

        return samples;
    }

    private static double One(byte[] data, int offset, int bits, bool isFloat) => bits switch
    {
        // Eight-bit WAV is the odd one out: unsigned, with silence at 128 rather than at zero.
        8 => (data[offset] - 128) / 128.0,
        16 => BitConverter.ToInt16(data, offset) / 32768.0,
        24 => ((data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16))) / 8388608.0,
        32 => isFloat
            ? BitConverter.ToSingle(data, offset)
            : BitConverter.ToInt32(data, offset) / 2147483648.0,
        _ => throw new InvalidDataException($"{bits}-bit samples are not supported"),
    };
}
