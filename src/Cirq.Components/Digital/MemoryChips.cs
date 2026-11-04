using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// The JEDEC byte-wide pinouts the memories in the palette are built on. They are a family on
/// purpose: a 24-pin socket takes a 2716, a 2732 or a 6116, and a 28-pin one a 2764, a 27C256, a
/// 28C256 or a 62256, with only a pin or two changing meaning between them — which is how a board
/// was made to take a bigger chip later.
/// </summary>
internal static class MemoryPinout
{
    /// <summary>The pins every 24-pin part shares: A0-A8, the data lines, the supplies, /CE and /OE.</summary>
    private static (int, string)[] Common24 =>
    [
        (1, "A7"), (2, "A6"), (3, "A5"), (4, "A4"), (5, "A3"), (6, "A2"), (7, "A1"), (8, "A0"),
        (9, "D0"), (10, "D1"), (11, "D2"), (12, "GND"), (13, "D3"), (14, "D4"), (15, "D5"),
        (16, "D6"), (17, "D7"), (18, "CE"), (19, "A10"), (20, "OE"), (22, "A9"), (23, "A8"), (24, "VCC"),
    ];

    /// <summary>The pins every 28-pin part shares: A0-A12, the data lines, the supplies, /CE and /OE.</summary>
    private static (int, string)[] Common28 =>
    [
        (2, "A12"), (3, "A7"), (4, "A6"), (5, "A5"), (6, "A4"), (7, "A3"), (8, "A2"), (9, "A1"),
        (10, "A0"), (11, "D0"), (12, "D1"), (13, "D2"), (14, "GND"), (15, "D3"), (16, "D4"),
        (17, "D5"), (18, "D6"), (19, "D7"), (20, "CE"), (21, "A10"), (22, "OE"), (23, "A11"),
        (24, "A9"), (25, "A8"), (28, "VCC"),
    ];

    /// <summary>6116 2K × 8 static RAM: pin 21 is /WE.</summary>
    public static (int, string)[] Sram6116 => [.. Common24, (21, "WE")];

    /// <summary>2716 2K × 8 EPROM: pin 21 is VPP, and /CE doubles as /PGM while programming.</summary>
    public static (int, string)[] Eprom2716 => [.. Common24, (21, "VPP")];

    /// <summary>
    /// 2732 4K × 8 EPROM: pin 21 becomes A11, and VPP moves onto /OE (pin 20), which is why a 2732
    /// will not drop into a 2716's socket without a link changed.
    /// </summary>
    public static (int, string)[] Eprom2732 => [.. Common24, (21, "A11")];

    /// <summary>2764 8K × 8 EPROM: VPP on pin 1, pin 26 not connected, /PGM on pin 27.</summary>
    public static (int, string)[] Eprom2764 => [.. Common28, (1, "VPP"), (27, "PGM")];

    /// <summary>27C256 32K × 8 EPROM: VPP on pin 1, A13 and A14 on pins 26 and 27.</summary>
    public static (int, string)[] Eprom27C256 => [.. Common28, (1, "VPP"), (26, "A13"), (27, "A14")];

    /// <summary>62256 and 28C256, 32K × 8: A14 on pin 1, A13 on 26 and /WE on 27.</summary>
    public static (int, string)[] Wide32K => [.. Common28, (1, "A14"), (26, "A13"), (27, "WE")];
}

/// <summary>
/// 6116 2K × 8 static RAM. The same chip as the palette's SRAM 2Kx8, under its part number: 150 ns,
/// writes level-sensitive while /CE and /WE are both low.
/// </summary>
public sealed class Sram6116 : ParallelMemory
{
    public Sram6116() : base(24, 11, MemoryPinout.Sram6116) => PropagationDelay = 150e-9;

    /// <summary>Active low: low takes the bus into the addressed byte.</summary>
    public Terminal WriteEnable => WriteEnablePin!;

    public override string PartNumber => "6116";

    protected override double OutputDisableTime => 50e-9;
}

/// <summary>
/// 62256 32K × 8 static RAM — the RAM in most hobby 6502 and Z80 machines, Ben Eater's among them.
/// 70 ns, writes level-sensitive while /CE and /WE are both low.
/// </summary>
public sealed class Sram62256 : ParallelMemory
{
    public Sram62256() : base(28, 15, MemoryPinout.Wide32K) => PropagationDelay = 70e-9;

    /// <summary>Active low: low takes the bus into the addressed byte.</summary>
    public Terminal WriteEnable => WriteEnablePin!;

    public override string PartNumber => "62256";

    protected override double OutputDisableTime => 25e-9;
}

/// <summary>
/// An ultraviolet-erasable PROM: a ROM as far as a circuit is concerned. Its contents come from
/// <see cref="ParallelMemory.Contents"/> or a loaded file — programming one with VPP and /PGM is not
/// simulated, so those pins only need wiring the way the datasheet asks for reading.
/// </summary>
public abstract class Eprom(int pinCount, int addressBits, (int, string)[] pinout)
    : ParallelMemory(pinCount, addressBits, pinout)
{
    public override bool AcceptsWrites => false;

    /// <summary>The t<sub>DF</sub> of an NMOS EPROM: the outputs float this long after /OE or /CE goes high.</summary>
    protected override double OutputDisableTime => 100e-9;
}

/// <summary>2716 2K × 8 EPROM, 450 ns. Tie VPP (pin 21) to VCC to read.</summary>
public sealed class Eprom2716 : Eprom
{
    public Eprom2716() : base(24, 11, MemoryPinout.Eprom2716) => PropagationDelay = 450e-9;

    public override string PartNumber => "2716";
}

/// <summary>2732 4K × 8 EPROM, 450 ns. Pin 20 is /OE (and VPP when programming), pin 21 A11.</summary>
public sealed class Eprom2732 : Eprom
{
    public Eprom2732() : base(24, 12, MemoryPinout.Eprom2732) => PropagationDelay = 450e-9;

    public override string PartNumber => "2732";
}

/// <summary>2764 8K × 8 EPROM, 250 ns. Tie VPP (pin 1) to VCC and /PGM (pin 27) high to read.</summary>
public sealed class Eprom2764 : Eprom
{
    public Eprom2764() : base(28, 13, MemoryPinout.Eprom2764) => PropagationDelay = 250e-9;

    public override string PartNumber => "2764";
}

/// <summary>27C256 32K × 8 CMOS EPROM, 150 ns. Tie VPP (pin 1) to VCC to read.</summary>
public sealed class Eprom27C256 : Eprom
{
    public Eprom27C256() : base(28, 15, MemoryPinout.Eprom27C256) => PropagationDelay = 150e-9;

    public override string PartNumber => "27C256";

    protected override double OutputDisableTime => 50e-9;
}

/// <summary>
/// 28C256 32K × 8 parallel EEPROM: a ROM that a circuit can write, slowly. The ROM in Ben Eater's
/// 6502 computer.
/// <para>
/// Reading is a 150 ns ROM's. Writing is not a RAM's, and that is the thing to know about it. A
/// write pulse on /WE (with /CE low and /OE high) only <i>loads</i> the byte — the address is taken
/// as /WE falls and the data as it rises — and the chip goes on accepting bytes into a 64-byte page
/// for as long as each follows the last within the byte-load window (150 µs). When the window
/// closes the page is written into the cells, which takes <see cref="WriteCycleTime"/>, and for all
/// of that time the chip answers every read with <b>data polling</b>: bit 7 the complement of the
/// last byte written and bit 6 toggling from one read to the next. Software waits for bit 7 to come
/// back true, or for bit 6 to stop toggling; anything written meanwhile is ignored. Then the cells
/// hold the new bytes and reads return them.
/// </para>
/// <para>
/// Modelled as described, with two simplifications: bytes for a different page in the same load are
/// ignored rather than corrupting the page, and the software data protection sequence is not
/// modelled, so the chip is always writable.
/// </para>
/// </summary>
public sealed partial class Eeprom28C256 : ParallelMemory, IBreakpointSource
{
    /// <summary>The byte-load window: a byte must follow the last within this, or the write starts.</summary>
    public const double ByteLoadWindow = 150e-6;

    private const int PageSize = 64;

    private readonly Dictionary<int, byte> _page = [];
    private int _pageBase = -1;
    private bool _strobe;
    private int _loadAddress;
    private int _loadData;
    private double _windowEnds = double.NaN;
    private double _cycleEnds = double.NaN;
    private int _lastWritten;
    private bool _toggle;
    private bool _wasReading;

    public Eeprom28C256() : base(28, 15, MemoryPinout.Wide32K) => PropagationDelay = 150e-9;

    /// <summary>Active low: a pulse loads a byte (address on the falling edge, data on the rising).</summary>
    public Terminal WriteEnable => WriteEnablePin!;

    public override string PartNumber => "28C256";

    protected override double OutputDisableTime => 50e-9;

    /// <summary>
    /// How long the internal write takes once a page has been loaded, in seconds. The datasheet's
    /// maximum is 10 ms; real parts are often quicker, which is why software polls rather than waits.
    /// </summary>
    [ObservableProperty]
    public partial double WriteCycleTime { get; set; } = 10e-3;

    /// <summary>True while a page is being written into the cells and the chip is answering with data polling.</summary>
    public bool IsBusy => !double.IsNaN(_cycleEnds);

    /// <summary>How many write cycles it has completed since the run began.</summary>
    public int WritesCompleted { get; private set; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var now = context.Time;

        // The page goes into the cells once the load window has closed, and comes out of them
        // a write cycle later.
        if (!double.IsNaN(_windowEnds) && now >= _windowEnds - 1e-15 && !_strobe)
        {
            _windowEnds = double.NaN;
            _cycleEnds = now + Math.Max(WriteCycleTime, 0);
        }

        if (!double.IsNaN(_cycleEnds) && now >= _cycleEnds - 1e-15)
        {
            foreach (var (at, written) in _page) Store(at, written);

            _page.Clear();
            _pageBase = -1;
            _cycleEnds = double.NaN;
            WritesCompleted++;
        }

        var selected = context.ReadInput(ChipEnable, Levels).IsLow();
        var outputsOn = context.ReadInput(OutputEnable, Levels).IsLow();
        var strobe = selected && !outputsOn && context.ReadInput(WriteEnable, Levels).IsLow();
        var address = ReadAddress(context);

        if (strobe)
        {
            // Address on the falling edge of the strobe; the data is followed until it ends.
            if (!_strobe) _loadAddress = address;
            _loadData = ReadDataBus(context);
            _strobe = true;

            Note(_loadAddress, _loadData, false, true);
            Release(context);
            _wasReading = false;
            return;
        }

        if (_strobe)
        {
            _strobe = false;
            Accept(_loadAddress, _loadData, now);
        }

        var reading = selected && outputsOn;

        if (!reading)
        {
            Note(selected ? address : -1, LastData, false, false);
            Release(context);
            _wasReading = false;
            return;
        }

        int value;
        if (IsBusy || !double.IsNaN(_windowEnds))
        {
            // Data polling: bit 7 inverted until the write is done, bit 6 toggling on every read.
            if (!_wasReading) _toggle = !_toggle;
            value = ((~_lastWritten) & 0x80) | (_toggle ? 0x40 : 0) | (_lastWritten & 0x3F);
        }
        else
        {
            value = Stored(address);
        }

        _wasReading = true;
        Note(address, value, true, false);
        Drive(context, value);
        NotifyValueChanged();
    }

    /// <summary>A byte loaded by the end of a write pulse: into the page, unless the chip is busy writing.</summary>
    private void Accept(int address, int value, double now)
    {
        if (IsBusy) return;

        var page = address & ~(PageSize - 1);
        if (_pageBase < 0) _pageBase = page;
        if (page != _pageBase) return;

        _page[address] = (byte)value;
        _lastWritten = value;
        _windowEnds = now + ByteLoadWindow;
    }

    public double? NextBreakpointAfter(double time)
    {
        if (!double.IsNaN(_windowEnds) && _windowEnds > time) return _windowEnds;
        if (!double.IsNaN(_cycleEnds) && _cycleEnds > time) return _cycleEnds;
        return null;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _page.Clear();
        _pageBase = -1;
        _strobe = false;
        _windowEnds = double.NaN;
        _cycleEnds = double.NaN;
        _lastWritten = 0;
        _toggle = false;
        _wasReading = false;
        WritesCompleted = 0;
    }
}
