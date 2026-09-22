using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The decode window's own logic: what it guesses, what it assigns, what it reports.</summary>
public class BusDecodeViewModelTests
{
    private static MainWindowViewModel Load(string name, double seconds, double? interval = null)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();

        Examples.All.Single(e => e.Name == name).Build(vm);

        if (interval is { } i) vm.Simulation.Settings.ProbeSampleInterval = i;

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(seconds);

        return vm;
    }

    /// <summary>
    /// Every shipped example names its probes after the signals, so opening the window on one
    /// should land on the right protocol with the right channels already assigned.
    /// </summary>
    [Theory]
    [InlineData("I2C EEPROM", BusProtocol.I2c)]
    [InlineData("SPI ADC", BusProtocol.Spi)]
    [InlineData("Serial Link", BusProtocol.Uart)]
    [InlineData("1-Wire Thermometer", BusProtocol.OneWire)]
    [InlineData("CAN Arbitration", BusProtocol.Can)]
    public void ItGuessesTheProtocolFromWhatTheProbesAreCalled(string example, BusProtocol expected)
    {
        var vm = Load(example, 1e-3);

        var model = new BusDecodeViewModel(vm.Circuit);

        Assert.Equal(expected, model.Protocol);
        Assert.NotNull(model.ChannelA);
    }

    [Fact]
    public void OpeningItOnTheEepromExampleDecodesTheTransactionWithNoSettingUp()
    {
        var vm = Load("I2C EEPROM", 3e-3);

        var model = new BusDecodeViewModel(vm.Circuit);
        model.Run();

        Assert.True(model.HasRows);
        Assert.Contains("0x50 W", model.Transcript);
        Assert.Contains("STOP", model.Transcript);

        // SDA and SCL, each on its own channel, picked from the names.
        Assert.Equal("SDA", model.ChannelA!.Label);
        Assert.Equal("SCL", model.ChannelB!.Label);
        Assert.Equal("SDA", model.LabelA);
        Assert.Equal("SCL", model.LabelB);
    }

    [Fact]
    public void TheSpiExampleGetsAllFourChannelLabelsAndBothDataLines()
    {
        var vm = Load("SPI ADC", 200e-6);

        var model = new BusDecodeViewModel(vm.Circuit);
        model.Run();

        Assert.True(model.UsesB);
        Assert.True(model.UsesC);
        Assert.True(model.UsesD);
        Assert.True(model.NeedsMode);
        Assert.False(model.NeedsBitRate);

        Assert.Equal("CLK", model.ChannelA!.Label);
        Assert.Equal("MOSI", model.ChannelB!.Label);
        Assert.Equal("MISO", model.ChannelC!.Label);

        // Both directions in one byte, which is what the slash in the text means.
        Assert.Contains("/", model.Transcript);
    }

    [Fact]
    public void SwitchingProtocolReassignsTheChannelsAndDecodesAgain()
    {
        var vm = Load("Serial Link", 3e-3);

        var model = new BusDecodeViewModel(vm.Circuit);

        Assert.Equal(BusProtocol.Uart, model.Protocol);
        Assert.True(model.NeedsBitRate);
        Assert.Equal(9600, model.BitRate);

        model.Protocol = BusProtocol.Can;

        // A sensible default rate for the new protocol rather than the old one's.
        Assert.Equal(125e3, model.BitRate);
        Assert.False(model.NeedsMode);
    }

    /// <summary>
    /// The thing about a wrong bit rate that catches people: it does not give silence, and it
    /// does not always give an error either. It gives bytes — wrong ones, confidently, and at
    /// half the rate here without a single framing complaint.
    /// </summary>
    [Fact]
    public void TheWrongBaudRateGivesDifferentBytesRatherThanNothing()
    {
        var vm = Load("Serial Link", 3e-3);

        var right = new BusDecodeViewModel(vm.Circuit) { BitRate = 9600 };
        right.Run();

        var wrong = new BusDecodeViewModel(vm.Circuit) { BitRate = 4800 };
        wrong.Run();

        Assert.Contains("\"hi\"", right.Summary);
        Assert.DoesNotContain("\"hi\"", wrong.Summary);
        Assert.NotEqual(right.Transcript, wrong.Transcript);
    }

    /// <summary>
    /// The symptom of a wrong bit rate is plausible rubbish, and it is worth being exact about
    /// how unhelpful that is: on this message neither half the rate nor twice it raises a single
    /// framing error. Both just produce different, confident, wrong bytes.
    /// </summary>
    [Theory]
    [InlineData(4800)]
    [InlineData(19200)]
    public void AWrongBaudRateGivesWrongBytesWithoutNecessarilyComplaining(double rate)
    {
        var vm = Load("Serial Link", 3e-3);

        var right = new BusDecodeViewModel(vm.Circuit) { BitRate = 9600 };
        right.Run();

        var wrong = new BusDecodeViewModel(vm.Circuit) { BitRate = rate };
        wrong.Run();

        Assert.NotEmpty(wrong.Rows);
        Assert.NotEqual(right.Transcript, wrong.Transcript);
        Assert.DoesNotContain(wrong.Rows, r => r.IsError);
    }

    /// <summary>
    /// The example itself decodes at the timebase it ships with; this is the same capture taken
    /// deliberately too coarsely, which has to be refused rather than misread.
    /// </summary>
    [Fact]
    public void ACaptureTooCoarseToDecodeIsReportedInTheSummaryRatherThanDecoded()
    {
        var vm = Load("1-Wire Thermometer", 5e-3, interval: 20e-6);

        var model = new BusDecodeViewModel(vm.Circuit);
        model.Run();

        Assert.False(model.HasRows);
        Assert.Contains("sample", model.Summary);
    }

    [Fact]
    public void TheSameExampleCapturedFinelyDecodesProperly()
    {
        var vm = Load("1-Wire Thermometer", 5e-3);

        var model = new BusDecodeViewModel(vm.Circuit);
        model.Run();

        Assert.True(model.HasRows);
        Assert.Contains("RESET", model.Transcript);
        Assert.Contains("0xCC", model.Transcript);
    }

    [Fact]
    public void WithNoTracesAtAllItSaysWhatToDo()
    {
        var model = new BusDecodeViewModel(new Cirq.Core.Topology.Circuit());
        model.Run();

        Assert.False(model.HasRows);
        Assert.Contains("Assign", model.Summary);
    }
}
