using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Decoding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>Which bus to read the traces as.</summary>
public enum BusProtocol
{
    I2c,
    Spi,
    Uart,
    OneWire,
    Can,
}

/// <summary>One decoded element, dressed for the list.</summary>
public sealed class DecodeRowViewModel(DecodedSpan span)
{
    public DecodedSpan Span { get; } = span;

    public string Time => SiPrefix.Format(Span.Start, "s");

    public string Kind => Span.Kind.ToString();

    public string Text => Span.Text;

    public string Detail => Span.Detail;

    public bool IsError => Span.Kind == SpanKind.Error;
}

/// <summary>
/// The bus decode window: read the recorded traces as a protocol rather than as wiggly lines.
/// <para>
/// This library has unusually deep bus coverage — I²C, SPI, UART, 1-Wire, CAN and RS-232, with a
/// worked example of each — and until now every one of those examples has had to be understood by
/// counting edges. The teaching in them is not in the shapes. It is in the addressing dance, in
/// the answer arriving underneath the question, in which node lost arbitration and when.
/// </para>
/// </summary>
public sealed partial class BusDecodeViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public BusDecodeViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var probe in circuit.Probes) Traces.Add(probe);

        // A first guess from the probe names, which on every shipped example are the signal names.
        Protocol = Guess();
        AssignByName();
    }

    /// <summary>The probes available to assign to the protocol's channels.</summary>
    public ObservableCollection<SignalProbe> Traces { get; } = [];

    public static IReadOnlyList<BusProtocol> ProtocolOptions { get; } = Enum.GetValues<BusProtocol>();

    [ObservableProperty]
    public partial BusProtocol Protocol { get; set; }

    /// <summary>First channel: SDA, SCLK, the line, or the bus.</summary>
    [ObservableProperty]
    public partial SignalProbe? ChannelA { get; set; }

    /// <summary>Second: SCL for I²C, MOSI for SPI. Unused by the single-wire protocols.</summary>
    [ObservableProperty]
    public partial SignalProbe? ChannelB { get; set; }

    /// <summary>Third: MISO. SPI only.</summary>
    [ObservableProperty]
    public partial SignalProbe? ChannelC { get; set; }

    /// <summary>Fourth: chip select. SPI only, and optional there.</summary>
    [ObservableProperty]
    public partial SignalProbe? ChannelD { get; set; }

    /// <summary>Bits per second, for the protocols that have no clock on the wire.</summary>
    [ObservableProperty]
    public partial double BitRate { get; set; } = 9600;

    /// <summary>SPI mode, 0 to 3.</summary>
    [ObservableProperty]
    public partial int SpiMode { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The whole decode on one line, which is how it is usually read.</summary>
    [ObservableProperty]
    public partial string Transcript { get; private set; } = string.Empty;

    /// <summary>What came back, one row per element.</summary>
    public ObservableCollection<DecodeRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>What the four channel pickers are called for the chosen protocol.</summary>
    public string LabelA => Protocol switch
    {
        BusProtocol.I2c => "SDA",
        BusProtocol.Spi => "SCLK",
        BusProtocol.Can => "Bus",
        _ => "Line",
    };

    public string LabelB => Protocol switch
    {
        BusProtocol.I2c => "SCL",
        BusProtocol.Spi => "MOSI",
        _ => string.Empty,
    };

    public string LabelC => Protocol == BusProtocol.Spi ? "MISO" : string.Empty;

    public string LabelD => Protocol == BusProtocol.Spi ? "CS" : string.Empty;

    public bool UsesB => LabelB.Length > 0;

    public bool UsesC => LabelC.Length > 0;

    public bool UsesD => LabelD.Length > 0;

    /// <summary>True for the protocols with no clock on the wire, which need to be told the rate.</summary>
    public bool NeedsBitRate => Protocol is BusProtocol.Uart or BusProtocol.Can;

    public bool NeedsMode => Protocol == BusProtocol.Spi;

    partial void OnProtocolChanged(BusProtocol value)
    {
        foreach (var name in new[]
                 {
                     nameof(LabelA), nameof(LabelB), nameof(LabelC), nameof(LabelD),
                     nameof(UsesB), nameof(UsesC), nameof(UsesD),
                     nameof(NeedsBitRate), nameof(NeedsMode),
                 })
        {
            OnPropertyChanged(name);
        }

        BitRate = value == BusProtocol.Can ? 125e3 : 9600;

        AssignByName();
        Run();
    }

    [RelayCommand]
    public void Run()
    {
        Rows.Clear();

        var result = Decode();

        foreach (var span in result.Spans) Rows.Add(new DecodeRowViewModel(span));

        Summary = result.Summary;
        Transcript = result.Transcript;

        OnPropertyChanged(nameof(HasRows));
    }

    private DecodeResult Decode()
    {
        LogicTrace? Of(SignalProbe? probe) =>
            probe is null ? null : LogicTrace.From(probe.HistoryBuffer.ToArray());

        var a = Of(ChannelA);

        if (a is null || a.IsEmpty)
            return DecodeResult.Nothing(Protocol.ToString(),
                $"Assign a recorded trace to {LabelA}, and run the circuit long enough to capture " +
                "something.");

        return Protocol switch
        {
            BusProtocol.I2c => Of(ChannelB) is { } scl
                ? I2cDecoder.Decode(a, scl)
                : DecodeResult.Nothing(I2cDecoder.Protocol, "Assign a trace to SCL as well."),

            BusProtocol.Spi => SpiDecoder.Decode(a, Of(ChannelB), Of(ChannelC), Of(ChannelD), SpiMode),

            BusProtocol.Uart => UartDecoder.Decode(a, BitRate),

            BusProtocol.OneWire => OneWireDecoder.Decode(a),

            _ => CanDecoder.Decode(a, BitRate),
        };
    }

    /// <summary>
    /// A first guess at the protocol from what the probes are called. Every shipped example names
    /// its probes after the signals, so this lands on the right answer for all of them — and where
    /// it does not, the pickers are right there.
    /// </summary>
    private BusProtocol Guess()
    {
        var names = Traces.Select(p => p.Label.ToUpperInvariant()).ToList();

        if (names.Any(n => n.Contains("SDA")) && names.Any(n => n.Contains("SCL")))
            return BusProtocol.I2c;

        if (names.Any(n => n.Contains("MOSI") || n.Contains("MISO"))) return BusProtocol.Spi;
        if (names.Any(n => n.Contains("CAN"))) return BusProtocol.Can;
        if (names.Any(n => n.Contains("DQ") || n.Contains("1-WIRE"))) return BusProtocol.OneWire;

        return BusProtocol.Uart;
    }

    /// <summary>Puts each probe on the channel its name suggests.</summary>
    private void AssignByName()
    {
        SignalProbe? Named(params string[] wanted) =>
            Traces.FirstOrDefault(p => wanted.Any(w =>
                p.Label.Contains(w, StringComparison.OrdinalIgnoreCase)));

        switch (Protocol)
        {
            case BusProtocol.I2c:
                ChannelA = Named("SDA") ?? Traces.FirstOrDefault();
                ChannelB = Named("SCL");
                break;

            case BusProtocol.Spi:
                ChannelA = Named("SCK", "CLK", "CLOCK") ?? Traces.FirstOrDefault();
                ChannelB = Named("MOSI", "DIN");
                ChannelC = Named("MISO", "DOUT");
                ChannelD = Named("CS", "SS", "SELECT");
                break;

            case BusProtocol.OneWire:
                ChannelA = Named("DQ") ?? Traces.FirstOrDefault();
                break;

            case BusProtocol.Can:
                ChannelA = Named("hear", "RXD", "CANH") ?? Traces.FirstOrDefault();
                break;

            default:
                ChannelA = Named("TX", "RX") ?? Traces.FirstOrDefault();
                break;
        }
    }
}
