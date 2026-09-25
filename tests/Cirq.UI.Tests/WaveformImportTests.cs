using Cirq.Components.Sources;
using Cirq.Core.Audio;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Reading a file of numbers into a waveform source.
/// <para>
/// What matters here is that what comes out is the waveform that went in — the same length, the
/// same shape — because a thinning that quietly kept only the beginning, or that aliased a tone
/// into a different one, produces a file that loads without complaint and is not the recording.
/// </para>
/// </summary>
public class WaveformImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"cirq-import-{Guid.NewGuid():N}");

    public WaveformImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>Two columns are time and value, which is what every scope writes.</summary>
    [Fact]
    public void TwoColumnsAreTimeAndValue()
    {
        var path = Write("scope.csv", "0,0\n0.001,5\n0.002,0\n");

        var imported = WaveformImport.Read(path);

        Assert.Equal(3, imported.Count);
        Assert.Equal(0.002, imported.Seconds, 1e-9);

        var source = new WaveformSource { Points = imported.Points };

        Assert.Equal(5.0, source.ValueAt(1e-3), 1e-6);
        Assert.Equal(2.5, source.ValueAt(0.5e-3), 1e-6);
    }

    /// <summary>
    /// A header is skipped by failing to parse rather than by being recognised, which handles every
    /// spelling of one without a list of them.
    /// </summary>
    [Fact]
    public void AHeaderRowIsSkipped()
    {
        var path = Write("headed.csv", "Time (s),Channel 1 (V)\n0,0\n0.001,5\n");

        var imported = WaveformImport.Read(path);

        Assert.Equal(2, imported.Count);
    }

    /// <summary>One column is values at a stated interval, and the note says what was assumed.</summary>
    [Fact]
    public void OneColumnIsValuesAtAStatedInterval()
    {
        var path = Write("column.txt", "0\n1\n2\n3\n");

        var imported = WaveformImport.Read(path);

        Assert.Equal(4, imported.Count);
        Assert.Contains("1 ms apart", imported.Note, StringComparison.Ordinal);
        Assert.Contains("Time Scale", imported.Note, StringComparison.Ordinal);
    }

    /// <summary>A file with nothing usable in it says so rather than producing an empty source.</summary>
    [Fact]
    public void AFileWithNoNumbersIsRefused()
    {
        var path = Write("prose.csv", "this file contains no numbers at all\n");

        var thrown = Assert.Throws<InvalidDataException>(() => WaveformImport.Read(path));

        Assert.Contains("two points", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A long file keeps its whole length rather than its beginning. Truncating would give the
    /// first fraction of a recording at full resolution, where what anybody wants is the whole of
    /// it at lower resolution — the difference between one syllable and a quieter sentence.
    /// </summary>
    [Fact]
    public void ALongFileIsResampledRatherThanTruncated()
    {
        var samples = Enumerable.Range(0, 100_000)
            .Select(i => Math.Sin(2 * Math.PI * i / 100_000.0))
            .ToArray();

        var path = Path.Combine(_directory, "long.wav");

        WaveFile.Write(path, samples, 100_000);

        var imported = WaveformImport.Read(path);

        Assert.True(imported.Count <= WaveformImport.MaximumPoints,
            $"{imported.Count} points is more than the limit");

        Assert.Equal(1.0, imported.Seconds, 0.01);
        Assert.Contains("resampled", imported.Note, StringComparison.Ordinal);

        // And it is still one cycle of a sine, not the first hundredth of one.
        var source = new WaveformSource { Points = imported.Points };

        Assert.Equal(1.0, source.ValueAt(0.25), 0.02);
        Assert.Equal(-1.0, source.ValueAt(0.75), 0.02);
    }

    /// <summary>
    /// Resampling averages across each stride rather than picking one sample from it. Taking every
    /// nth sample aliases — a tone above the new Nyquist rate comes back as a different, lower one
    /// that looks entirely plausible — and the whole point of importing a capture is that it is the
    /// capture.
    /// </summary>
    [Fact]
    public void ResamplingAveragesRatherThanAliasing()
    {
        // A tone far above what 8192 points across this second can represent. Averaged, it very
        // nearly cancels and comes back as something small; picked, it comes back as a confident
        // waveform at some invented low frequency.
        var samples = Enumerable.Range(0, 100_000)
            .Select(i => Math.Sin(2 * Math.PI * 20_000.0 * i / 100_000.0))
            .ToArray();

        var path = Path.Combine(_directory, "fast.wav");

        WaveFile.Write(path, samples, 100_000);

        var source = new WaveformSource { Points = WaveformImport.Read(path).Points };

        var peak = Enumerable.Range(0, 400)
            .Select(i => Math.Abs(source.ValueAt(i / 400.0)))
            .Max();

        Assert.True(peak < 0.25,
            $"a tone this far above the resampled rate should mostly cancel, not come back at {peak:0.###}");
    }

    /// <summary>A WAV's samples are a fraction of full scale, so something has to say what that is worth.</summary>
    [Fact]
    public void FullScaleIsWorthWhateverIsAskedFor()
    {
        double[] samples = [0.0, 1.0, 0.0, -1.0];

        var path = Path.Combine(_directory, "scale.wav");

        WaveFile.Write(path, samples, 4);

        var source = new WaveformSource { Points = WaveformImport.Read(path, volts: 3.3).Points };

        Assert.Equal(3.3, source.ValueAt(0.25), 0.01);
        Assert.Equal(-3.3, source.ValueAt(0.75), 0.01);
    }

    [Theory]
    [InlineData("a.csv", true)]
    [InlineData("a.txt", true)]
    [InlineData("a.wav", true)]
    [InlineData("a.WAV", true)]
    [InlineData("a.cirq", false)]
    [InlineData("a.png", false)]
    public void ItKnowsWhatItCanRead(string name, bool expected) =>
        Assert.Equal(expected, WaveformImport.CanRead(name));
}
