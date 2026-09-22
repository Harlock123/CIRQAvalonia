using Cirq.Core.Probing;
using Cirq.Engine.Decoding;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The decoders run against the shipped bus examples, which is the only check that matters: a
/// decoder that works on a waveform built by the same person who wrote it has proved very little.
/// </summary>
public class BusDecodeTests
{
    private static MainWindowViewModel Load(string name, double seconds, double? sampleInterval = null)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();

        Examples.All.Single(e => e.Name == name).Build(vm);

        if (sampleInterval is { } interval) vm.Simulation.Settings.ProbeSampleInterval = interval;

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(seconds);

        return vm;
    }

    private static LogicTrace Trace(MainWindowViewModel vm, string label) =>
        LogicTrace.From(vm.Circuit.Probes.Single(p => p.Label == label).HistoryBuffer.ToArray());

    /// <summary>
    /// The EEPROM example writes an address pointer and reads three bytes back. Every part of that
    /// should be legible, including the deliberate NACK on the last byte of the read.
    /// </summary>
    [Fact]
    public void TheI2cEepromExampleDecodesIntoItsOwnTransaction()
    {
        var vm = Load("I2C EEPROM", 3e-3);

        var result = I2cDecoder.Decode(Trace(vm, "SDA"), Trace(vm, "SCL"));

        Assert.Equal(
            "START 0x50 W ACK 0x00 ACK 0x00 ACK STOP START 0x50 R ACK 0x48 ACK 0x49 ACK 0x21 NACK STOP",
            result.Transcript);

        // 0x48 0x49 0x21 is "HI!", which is what the example writes into the memory.
        var data = result.Spans.Where(s => s.Kind == SpanKind.Data).ToList();
        Assert.Equal("HI!", new string([.. data.TakeLast(3).Select(s => (char)Convert.ToInt32(s.Text, 16))]));

        // The restart is what a read after a write looks like: the master keeps the bus rather
        // than releasing it between the two.
        Assert.Equal(2, result.Spans.Count(s => s.Text is "START" or "RESTART"));
    }

    /// <summary>
    /// The thing full duplex means: the converter's answer arrives underneath the master's
    /// question rather than after it.
    /// </summary>
    [Fact]
    public void TheSpiAdcExampleShowsTheAnswerArrivingWhileTheQuestionIsStillBeingAsked()
    {
        var vm = Load("SPI ADC", 200e-6);

        var result = SpiDecoder.Decode(
            Trace(vm, "CLK"), Trace(vm, "MOSI"), Trace(vm, "MISO"), null);

        var bytes = result.Spans.Where(s => s.Kind == SpanKind.Data).ToList();

        Assert.Equal(3, bytes.Count);

        // The master sends 01 80 00, and the ten-bit answer comes back in the second and third.
        Assert.StartsWith("01/", bytes[0].Text);
        Assert.StartsWith("80/", bytes[1].Text);

        var high = Convert.ToInt32(bytes[1].Text.Split('/')[1], 16);
        var low = Convert.ToInt32(bytes[2].Text.Split('/')[1], 16);
        var code = ((high & 0x03) << 8) | low;

        // The knob sits at 0.6 of full scale, and ten bits of that is 614.
        Assert.InRange(code, 600, 628);
    }

    [Fact]
    public void TheSerialLinkExampleDecodesIntoTheTextItIsSending()
    {
        var vm = Load("Serial Link", 3e-3);

        var result = UartDecoder.Decode(Trace(vm, "Terminal TX"), 9600);

        Assert.Contains("\"hi\"", result.Summary);
        Assert.All(result.Spans, s => Assert.NotEqual(SpanKind.Error, s.Kind));
    }

    /// <summary>
    /// A DS18B20 conversation, which is where 1-Wire's commands become readable rather than being
    /// a pattern of pulse widths.
    /// </summary>
    /// <summary>
    /// At the timebase the example ships with — no settings to change first, because an example
    /// that needs one is an example that does not work.
    /// </summary>
    [Fact]
    public void TheOneWireExampleDecodesItsResetAndCommands()
    {
        var vm = Load("1-Wire Thermometer", 5e-3);

        var result = OneWireDecoder.Decode(Trace(vm, "DQ"));

        Assert.StartsWith("RESET PRESENCE", result.Transcript);

        var bytes = result.Spans.Where(s => s.Kind == SpanKind.Data).ToList();

        // SKIP ROM, then WRITE SCRATCHPAD and the three bytes it takes.
        Assert.Equal("0xCC", bytes[0].Text);
        Assert.Contains("SKIP ROM", bytes[0].Detail);
        Assert.Equal("0x4E", bytes[1].Text);
        Assert.Contains("WRITE SCRATCHPAD", bytes[1].Detail);
    }

    /// <summary>
    /// And the same capture sampled too coarsely is refused rather than misread. A write-one slot
    /// is six microseconds; sampled every twenty it is not there at all, and every bit after it
    /// has moved.
    /// </summary>
    [Fact]
    public void TheSameCaptureSampledTooCoarselyIsRefusedRatherThanMisread()
    {
        var vm = Load("1-Wire Thermometer", 5e-3, sampleInterval: 20e-6);

        var result = OneWireDecoder.Decode(Trace(vm, "DQ"));

        Assert.True(result.IsEmpty);
        Assert.Contains("sample", result.Summary);
    }

    /// <summary>
    /// The CAN example demonstrates arbitration with two clocks rather than sending real frames,
    /// so there is no frame on that bus — and a frame decoder must say so rather than invent one.
    /// </summary>
    [Fact]
    public void TheCanArbitrationExampleHasNoFrameOnItAndTheDecoderSaysSo()
    {
        var vm = Load("CAN Arbitration", 500e-6);

        var result = CanDecoder.Decode(Trace(vm, "What both nodes hear"), 125e3);

        Assert.True(result.IsEmpty);
        Assert.NotEmpty(result.Summary);
    }
}
