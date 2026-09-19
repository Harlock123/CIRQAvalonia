using Cirq.Core.Units;

namespace Cirq.Components.Tests;

public class SiPrefixTests
{
    [Theory]
    [InlineData("10k", 10_000)]
    [InlineData("2.2M", 2_200_000)]
    [InlineData("100R", 100)]
    [InlineData("4k7", 4_700)]
    [InlineData("4R7", 4.7)]
    [InlineData("1u", 1e-6)]
    [InlineData("100n", 100e-9)]
    [InlineData("10p", 10e-12)]
    [InlineData("1m", 1e-3)]
    [InlineData("1G", 1e9)]
    [InlineData("470", 470)]
    [InlineData("-2.5k", -2500)]
    [InlineData("1µ", 1e-6)]
    [InlineData("100nF", 100e-9)]
    [InlineData("10kOhm", 10_000)]
    [InlineData("1kHz", 1000)]
    [InlineData(" 3.3k ", 3300)]
    public void ParsesEngineeringNotation(string text, double expected)
    {
        Assert.True(SiPrefix.TryParse(text, out var value));
        Assert.Equal(expected, value, Math.Abs(expected) * 1e-9 + 1e-15);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("k")]
    public void RejectsMalformedInput(string text) => Assert.False(SiPrefix.TryParse(text, out _));

    [Theory]
    [InlineData(4700, "4.7k")]
    [InlineData(1e-6, "1µ")]
    [InlineData(2.2e6, "2.2M")]
    [InlineData(100, "100")]
    [InlineData(0.047, "47m")]
    public void FormatsUsingTheNearestPrefix(double value, string expected) =>
        Assert.Equal(expected, SiPrefix.Format(value));

    [Fact]
    public void FormatRoundTripsThroughParse()
    {
        double[] values = [1e-12, 4.7e-9, 100e-6, 0.033, 47, 6800, 1.5e6, 2.7e9];
        foreach (var v in values)
        {
            Assert.True(SiPrefix.TryParse(SiPrefix.Format(v), out var parsed));
            Assert.Equal(v, parsed, Math.Abs(v) * 1e-3);
        }
    }

    [Fact]
    public void MilliAndMegaAreCaseSensitive()
    {
        Assert.Equal(1e-3, SiPrefix.Parse("1m"));
        Assert.Equal(1e6, SiPrefix.Parse("1M"));
    }
}
