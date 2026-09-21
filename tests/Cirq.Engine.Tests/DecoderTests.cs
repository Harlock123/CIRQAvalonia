using Cirq.Core.Primitives;
using Cirq.Engine.Decoding;

namespace Cirq.Engine.Tests;

/// <summary>
/// The protocol decoders, driven from waveforms built to a known transaction so the expected
/// answer is decided before the decoder sees anything.
/// </summary>
public class DecoderTests
{
    // ---- builders ----------------------------------------------------------

    private sealed class Wire(double step)
    {
        private readonly List<DataPoint> _points = [];

        public double Time { get; private set; }

        public void Hold(bool high, int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                _points.Add(new DataPoint(Time, high ? 3.3 : 0.0));
                Time += step;
            }
        }

        public void HoldFor(bool high, double seconds)
        {
            var until = Time + seconds;
            while (Time < until)
            {
                _points.Add(new DataPoint(Time, high ? 3.3 : 0.0));
                Time += step;
            }
        }

        public LogicTrace Trace() => LogicTrace.From(_points);
    }

    // ---- I2C ---------------------------------------------------------------

    private static (LogicTrace Sda, LogicTrace Scl) I2cCapture(
        int address, bool read, params int[] bytes)
    {
        List<DataPoint> sda = [], scl = [];
        var t = 0.0;
        const double q = 1e-6;

        void Step(bool d, bool c)
        {
            sda.Add(new DataPoint(t, d ? 3.3 : 0));
            scl.Add(new DataPoint(t, c ? 3.3 : 0));
            t += q;
        }

        void Bit(bool v)
        {
            Step(v, false);
            Step(v, true);
            Step(v, true);
            Step(v, false);
        }

        void Byte(int b) { for (var i = 7; i >= 0; i--) Bit(((b >> i) & 1) != 0); }

        Step(true, true); Step(true, true);          // idle
        Step(false, true); Step(false, true);        // START
        Step(false, false);

        Byte((address << 1) | (read ? 1 : 0));
        Bit(false);                                   // ACK

        foreach (var b in bytes) { Byte(b); Bit(false); }

        Step(false, false); Step(false, true);        // STOP setup
        Step(true, true); Step(true, true);

        return (LogicTrace.From(sda), LogicTrace.From(scl));
    }

    [Fact]
    public void I2cReadsBackTheAddressAndBytesItWasGiven()
    {
        var (sda, scl) = I2cCapture(0x50, read: false, 0x2A, 0xFF);

        var result = I2cDecoder.Decode(sda, scl);

        Assert.Equal("START 0x50 W ACK 0x2A ACK 0xFF ACK STOP", result.Transcript);
        // Three bytes were on the wire: the address and the two data bytes.
        Assert.Contains("3 byte", result.Summary);
    }

    [Fact]
    public void TheReadWriteBitComesOutOfTheBottomOfTheAddressByte()
    {
        var (sda, scl) = I2cCapture(0x68, read: true, 0x01);

        var result = I2cDecoder.Decode(sda, scl);

        // 0x68 shifted up with the read bit set is 0xD1 on the wire, and the address is still 0x68.
        Assert.Contains("0x68 R", result.Transcript);
        Assert.Contains("0xD1", result.Spans.First(s => s.Kind == SpanKind.Address).Detail);
    }

    /// <summary>
    /// The clock rise that sets up a STOP is not a ninth data bit, and counting it produces a
    /// phantom one-bit byte before every stop condition.
    /// </summary>
    [Fact]
    public void TheStopSetupEdgeIsNotCountedAsAData_Bit()
    {
        var (sda, scl) = I2cCapture(0x50, read: false, 0x00);

        var result = I2cDecoder.Decode(sda, scl);

        Assert.DoesNotContain(result.Spans, s => s.Kind == SpanKind.Error);
        Assert.EndsWith("ACK STOP", result.Transcript);
    }

    [Fact]
    public void AnUnansweredAddressComesBackAsANack()
    {
        List<DataPoint> sda = [], scl = [];
        var t = 0.0;
        const double q = 1e-6;

        void Step(bool d, bool c)
        {
            sda.Add(new DataPoint(t, d ? 3.3 : 0));
            scl.Add(new DataPoint(t, c ? 3.3 : 0));
            t += q;
        }

        void Bit(bool v) { Step(v, false); Step(v, true); Step(v, true); Step(v, false); }

        Step(true, true); Step(true, true);
        Step(false, true); Step(false, true);
        Step(false, false);

        for (var i = 7; i >= 0; i--) Bit(((0x30 >> i) & 1) != 0);
        Bit(true);   // nobody pulled SDA down

        Step(false, false); Step(false, true); Step(true, true); Step(true, true);

        var result = I2cDecoder.Decode(LogicTrace.From(sda), LogicTrace.From(scl));

        Assert.Contains("NACK", result.Transcript);
        Assert.Contains("not acknowledged", result.Summary);
    }

    // ---- SPI ---------------------------------------------------------------

    [Fact]
    public void SpiDecodesBothDirectionsOfTheSameEightClocks()
    {
        List<DataPoint> clk = [], mosi = [], miso = [], cs = [];
        var t = 0.0;
        const double q = 1e-6;

        void Step(bool c, bool o, bool i, bool select)
        {
            clk.Add(new DataPoint(t, c ? 3.3 : 0));
            mosi.Add(new DataPoint(t, o ? 3.3 : 0));
            miso.Add(new DataPoint(t, i ? 3.3 : 0));
            cs.Add(new DataPoint(t, select ? 0 : 3.3));   // active low
            t += q;
        }

        Step(false, false, false, false);   // idle, deselected

        void Byte(int out_, int in_)
        {
            for (var b = 7; b >= 0; b--)
            {
                var o = ((out_ >> b) & 1) != 0;
                var i = ((in_ >> b) & 1) != 0;

                Step(false, o, i, true);
                Step(true, o, i, true);
                Step(true, o, i, true);
                Step(false, o, i, true);
            }
        }

        Step(false, false, false, true);    // select
        Byte(0x9C, 0x3B);
        Step(false, false, false, false);   // deselect

        var result = SpiDecoder.Decode(
            LogicTrace.From(clk), LogicTrace.From(mosi), LogicTrace.From(miso), LogicTrace.From(cs));

        Assert.Contains("9C/3B", result.Transcript);
        Assert.Contains("CS↓", result.Transcript);
        Assert.Contains("CS↑", result.Transcript);
    }

    [Fact]
    public void WithOnlyOneDataLineTheByteIsReportedPlainly()
    {
        var clock = new Wire(1e-7);
        var data = new Wire(1e-7);

        clock.Hold(false, 20);
        data.Hold(false, 20);

        for (var b = 7; b >= 0; b--)
        {
            var bit = ((0x5A >> b) & 1) != 0;

            data.Hold(bit, 40);
            clock.Hold(false, 10);
            clock.Hold(true, 20);
            clock.Hold(false, 10);
        }

        var result = SpiDecoder.Decode(clock.Trace(), data.Trace(), null, null);

        Assert.Contains("0x5A", result.Transcript);
    }

    // ---- UART --------------------------------------------------------------

    private static LogicTrace UartCapture(double baud, params byte[] bytes)
    {
        var wire = new Wire(1.0 / (baud * 40));
        var bitTime = 1.0 / baud;

        wire.HoldFor(true, bitTime * 2);

        foreach (var b in bytes)
        {
            wire.HoldFor(false, bitTime);                      // start

            for (var i = 0; i < 8; i++)                        // LSB first
                wire.HoldFor(((b >> i) & 1) != 0, bitTime);

            wire.HoldFor(true, bitTime);                       // stop
        }

        wire.HoldFor(true, bitTime * 3);

        return wire.Trace();
    }

    [Fact]
    public void UartReadsTheCharactersItWasSent()
    {
        var result = UartDecoder.Decode(UartCapture(9600, (byte)'H', (byte)'i', (byte)'!'), 9600);

        Assert.Equal(3, result.Spans.Count);
        Assert.Contains("\"Hi!\"", result.Summary);
    }

    /// <summary>
    /// Back-to-back characters are where a resynchronising decoder goes wrong: the next start bit
    /// falls the instant the previous stop bit ends.
    /// </summary>
    [Fact]
    public void CharactersWithNoGapBetweenThemAreAllDecoded()
    {
        var result = UartDecoder.Decode(UartCapture(115200, 0x00, 0xFF, 0x55, 0xAA), 115200);

        Assert.Equal(4, result.Spans.Count);
        Assert.Equal(["0x00", "0xFF", "0x55", "0xAA"],
            result.Spans.Select(s => s.Text.Split(' ')[0]).ToArray());
    }

    [Fact]
    public void TheWrongBaudRateGivesFramingErrorsRatherThanSilence()
    {
        // Sent at 9600, read at 19200: the sampling drifts out of the character.
        var result = UartDecoder.Decode(UartCapture(9600, (byte)'A', (byte)'B', (byte)'C'), 19200);

        Assert.Contains(result.Spans, s => s.Kind == SpanKind.Error);
    }

    // ---- 1-Wire ------------------------------------------------------------

    [Fact]
    public void OneWireReadsTheResetPresenceAndCommandItWasGiven()
    {
        var wire = new Wire(1e-6);

        wire.HoldFor(true, 100e-6);
        wire.HoldFor(false, 480e-6);      // reset
        wire.HoldFor(true, 30e-6);
        wire.HoldFor(false, 120e-6);      // presence
        wire.HoldFor(true, 30e-6);

        // SKIP ROM, least significant bit first. Short low is a one, long low is a zero.
        foreach (var bit in Bits(0xCC))
        {
            wire.HoldFor(false, bit ? 6e-6 : 60e-6);
            wire.HoldFor(true, bit ? 60e-6 : 10e-6);
        }

        wire.HoldFor(true, 100e-6);

        var result = OneWireDecoder.Decode(wire.Trace());

        Assert.Equal("RESET PRESENCE 0xCC", result.Transcript);
        Assert.Contains("SKIP ROM", result.Spans.Last().Detail);
    }

    private static IEnumerable<bool> Bits(int value)
    {
        for (var i = 0; i < 8; i++) yield return ((value >> i) & 1) != 0;
    }

    // ---- CAN ---------------------------------------------------------------

    /// <summary>
    /// Builds a standard CAN frame on the wire, dominant low, with the stuffing a transmitter
    /// would insert. No example in the library sends one, so the only honest way to check the
    /// decoder is to construct a frame whose contents are decided in advance.
    /// </summary>
    private static LogicTrace CanFrame(int identifier, double bitRate, params byte[] data)
    {
        // Logical bits: one is recessive and leaves the line high, zero is dominant and pulls it
        // down. Building the frame in logical bits and letting the wire invert nothing is the
        // only way to keep the polarity straight.
        List<bool> bits = [];
        var run = 0;
        var last = true;

        void Add(bool bit)
        {
            bits.Add(bit);

            if (bit == last) run++;
            else { run = 1; last = bit; }

            // Five of a kind and the transmitter inserts an opposite bit for the receivers' sake.
            if (run != 5) return;

            bits.Add(!bit);
            last = !bit;
            run = 1;
        }

        void Field(int value, int width)
        {
            for (var i = width - 1; i >= 0; i--) Add(((value >> i) & 1) != 0);
        }

        Add(false);                 // start of frame: dominant
        Field(identifier, 11);
        Add(false);                 // RTR dominant: an ordinary data frame
        Add(false);                 // IDE dominant: a standard eleven-bit identifier
        Add(false);                 // reserved
        Field(data.Length, 4);      // DLC

        foreach (var b in data) Field(b, 8);

        Field(0x1234, 15);          // CRC; its contents do not matter to the decode

        var wire = new Wire(1.0 / (bitRate * 20));
        var bitTime = 1.0 / bitRate;

        wire.HoldFor(true, bitTime * 4);
        foreach (var bit in bits) wire.HoldFor(bit, bitTime);

        // CRC delimiter, the acknowledge slot somebody else pulled dominant, its delimiter, and
        // then the bus idle. None of this is stuffed.
        wire.HoldFor(true, bitTime);
        wire.HoldFor(false, bitTime);
        wire.HoldFor(true, bitTime * 15);

        return wire.Trace();
    }

    [Fact]
    public void CanReadsTheIdentifierAndDataOutOfAStuffedFrame()
    {
        const double rate = 125e3;

        var result = CanDecoder.Decode(CanFrame(0x123, rate, 0xDE, 0xAD), rate);

        Assert.Contains("ID 0x123", result.Transcript);
        Assert.Contains("DLC 2", result.Transcript);
        Assert.Contains("0xDE", result.Transcript);
        Assert.Contains("0xAD", result.Transcript);
    }

    /// <summary>
    /// A low identifier is a high priority, and that ordering is the whole of CAN arbitration.
    /// </summary>
    [Fact]
    public void TheIdentifierComesOutWhateverItsBitPattern()
    {
        const double rate = 250e3;

        foreach (var id in new[] { 0x001, 0x0FF, 0x555, 0x7A3 })
        {
            var result = CanDecoder.Decode(CanFrame(id, rate, 0x01), rate);

            Assert.Contains($"ID 0x{id:X3}", result.Transcript);
        }
    }

    [Fact]
    public void StuffedBitsAreRemovedAndCounted()
    {
        const double rate = 125e3;

        // An identifier and payload full of runs, so there is plenty to stuff.
        var result = CanDecoder.Decode(CanFrame(0x000, rate, 0x00, 0xFF), rate);

        Assert.Contains("ID 0x000", result.Transcript);
        Assert.Contains("stuffed bit", result.Summary);
    }

    [Fact]
    public void TheWrongBitRateIsRefusedRatherThanDecodedIntoNonsense()
    {
        var result = CanDecoder.Decode(CanFrame(0x123, 125e3, 0x01), 500e3);

        // Refused, whether because the pulses do not fit the rate or because the run lengths
        // are impossible under bit stuffing. Either is the right answer; guessing is not.
        Assert.True(result.IsEmpty);
        Assert.NotEmpty(result.Summary);
    }

    [Fact]
    public void ATraceWithNoFrameOnItIsRefused()
    {
        var wire = new Wire(1e-6);
        wire.HoldFor(true, 200e-6);
        wire.HoldFor(false, 200e-6);

        var result = CanDecoder.Decode(wire.Trace(), 125e3);

        Assert.True(result.IsEmpty);
    }

    // ---- sampling ----------------------------------------------------------

    [Fact]
    public void ACaptureTooCoarseToDecodeIsRefusedRatherThanGuessedAt()
    {
        // 1-Wire's write-one slot is six microseconds. Sampled every twenty, it is not there.
        List<DataPoint> samples = [];
        var t = 0.0;

        for (var i = 0; i < 200; i++)
        {
            samples.Add(new DataPoint(t, i % 3 == 0 ? 0.0 : 3.3));
            t += 20e-6;
        }

        var trace = new LogicTrace(samples, 1.65, 0.3);

        Assert.True(trace.IsUndersampled);
        Assert.NotNull(trace.SamplingWarning);

        var result = OneWireDecoder.Decode(trace);

        Assert.True(result.IsEmpty);
        Assert.Contains("pulses are being missed", result.Summary);
    }

    [Fact]
    public void AWellSampledCaptureIsNotRefused()
    {
        var wire = new Wire(1e-7);
        for (var i = 0; i < 20; i++) { wire.HoldFor(false, 6e-6); wire.HoldFor(true, 60e-6); }

        Assert.False(wire.Trace().IsUndersampled);
        Assert.Null(wire.Trace().SamplingWarning);
    }
}
