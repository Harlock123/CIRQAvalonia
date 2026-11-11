using Cirq.Core.Digital;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// The 2114 1K × 4 static RAM, two to a byte in a great many 1970s machines (the ZX80, the TRS-80 Model I, the Apple I):
/// A0-A9, four common data lines I/O1-I/O4, /CS and /WE, and no /OE — /CS low with /WE high reads, /CS and /WE low
/// writes. 450 ns. Pinout: 1 A6, 2 A5, 3 A4, 4 A3, 5 A0, 6 A1, 7 A2, 8 /CS, 9 GND, 10 /WE, 11-14 I/O4-I/O1, 15 A9,
/// 16 A8, 17 A7, 18 VCC.
/// </summary>
public sealed partial class Sram2114 : DigitalIc, IRunState
{
    private readonly Terminal[] _a = new Terminal[10];
    private readonly Terminal[] _io = new Terminal[4];
    private readonly byte[] _cells = new byte[1024];

    public Sram2114() : base(18)
    {
        PropagationDelay = 450e-9;
        var pins = new Terminal[19];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 18));
        (_a[6], _a[5], _a[4], _a[3], _a[0], _a[1], _a[2]) = (Pin(1, "A6", TerminalType.Input), Pin(2, "A5", TerminalType.Input),
            Pin(3, "A4", TerminalType.Input), Pin(4, "A3", TerminalType.Input), Pin(5, "A0", TerminalType.Input),
            Pin(6, "A1", TerminalType.Input), Pin(7, "A2", TerminalType.Input));
        ChipSelect = Pin(8, "CS", TerminalType.Input);
        Gnd = Pin(9, "GND", TerminalType.Ground);
        WriteEnable = Pin(10, "WE", TerminalType.Input);
        for (var i = 0; i < 4; i++) _io[3 - i] = Pin(11 + i, $"I/O{4 - i}", TerminalType.Bidirectional);
        (_a[9], _a[8], _a[7]) = (Pin(15, "A9", TerminalType.Input), Pin(16, "A8", TerminalType.Input), Pin(17, "A7", TerminalType.Input));
        Vcc = Pin(18, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, WriteEnable, .. _a, .. _io], [.. _io]);
    }

    public Terminal ChipSelect { get; }
    public Terminal WriteEnable { get; }

    public IReadOnlyList<Terminal> Address => _a;

    /// <summary>I/O1-I/O4, I/O1 the lowest bit.</summary>
    public IReadOnlyList<Terminal> Data => _io;

    public int Read(int address) => _cells[address & 0x3FF];

    public override string PartNumber => "2114";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name is "CS" or "WE" ? "/" + pin.Name : pin.Name;

    public override string ValueLabel => "1K × 4";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var address = 0;
        for (var i = 0; i < 10; i++)
            if (context.ReadInput(_a[i], Levels).IsHigh()) address |= 1 << i;
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        var writing = selected && context.ReadInput(WriteEnable, Levels).IsLow();
        if (writing)
        {
            var nibble = 0;
            for (var i = 0; i < 4; i++)
                if (context.ReadInput(_io[i], Levels).IsHigh()) nibble |= 1 << i;
            _cells[address] = (byte)nibble;
        }

        var reading = selected && !writing;
        for (var i = 0; i < 4; i++)
            Request(context, i, !reading ? LogicState.HighImpedance : ((_cells[address] >> i) & 1) != 0 ? LogicState.High : LogicState.Low,
                reading ? DelayFor(context) : 30e-9);
    }

    public object CaptureRunState() => _cells.Clone();

    public void AdoptRunState(object state)
    {
        if (state is byte[] cells && cells.Length == _cells.Length) Array.Copy(cells, _cells, cells.Length);
    }
}

/// <summary>
/// The 4116 16K × 1 dynamic RAM, eight to a byte, the memory of the TRS-80, the ZX Spectrum and the arcade boards of the
/// late 1970s. Its 14-bit address goes in twice on seven pins: the row (A0-A6) latched as /RAS falls, the column as
/// /CAS falls. With /WE low at that moment it writes DIN (an early write); otherwise it reads, DOUT driving the bit
/// while /CAS stays low and letting go when it rises. It's dynamic: each cell is a capacitor, and a row not accessed —
/// or refreshed, by /RAS alone with a row address (RAS-only refresh) — within <see cref="RetentionTime"/> (2 ms, the
/// datasheet's) loses its data, reading 0 after. It wants all three supplies: VDD +12 V, VCC +5 V and VBB −5 V, and
/// without them forgets everything. Pinout: 1 VBB, 2 DIN, 3 /WE, 4 /RAS, 5 A0, 6 A2, 7 A1, 8 VDD, 9 VCC, 10 A5, 11 A4,
/// 12 A3, 13 A6, 14 DOUT, 15 /CAS, 16 VSS.
/// </summary>
public sealed partial class Dram4116 : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[7];
    private readonly bool[] _cells = new bool[16384];
    private readonly double[] _refreshed = new double[128];
    private bool _lastRas = true, _lastCas = true;
    private int _row = -1, _column;
    private bool _reading, _bit;

    public Dram4116() : base(16)
    {
        PropagationDelay = 150e-9;
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        Vbb = Pin(1, "VBB", TerminalType.Passive);
        DataIn = Pin(2, "DIN", TerminalType.Input);
        WriteEnable = Pin(3, "WE", TerminalType.Input);
        RowStrobe = Pin(4, "RAS", TerminalType.Input);
        _a[0] = Pin(5, "A0", TerminalType.Input);
        _a[2] = Pin(6, "A2", TerminalType.Input);
        _a[1] = Pin(7, "A1", TerminalType.Input);
        Vdd = Pin(8, "VDD", TerminalType.Passive);
        Vcc = Pin(9, "VCC", TerminalType.Power);
        _a[5] = Pin(10, "A5", TerminalType.Input);
        _a[4] = Pin(11, "A4", TerminalType.Input);
        _a[3] = Pin(12, "A3", TerminalType.Input);
        _a[6] = Pin(13, "A6", TerminalType.Input);
        DataOut = Pin(14, "DOUT", TerminalType.Output);
        ColumnStrobe = Pin(15, "CAS", TerminalType.Input);
        Gnd = Pin(16, "VSS", TerminalType.Ground);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([DataIn, WriteEnable, RowStrobe, ColumnStrobe, .. _a], [DataOut]);
    }

    public Terminal Vbb { get; }
    public Terminal Vdd { get; }
    public Terminal DataIn { get; }
    public Terminal DataOut { get; }
    public Terminal WriteEnable { get; }
    public Terminal RowStrobe { get; }
    public Terminal ColumnStrobe { get; }

    public IReadOnlyList<Terminal> Address => _a;

    /// <summary>How long a row keeps its data unrefreshed, in seconds.</summary>
    [ObservableProperty]
    public partial double RetentionTime { get; set; } = 2e-3;

    public override string PartNumber => "4116";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name is "WE" or "RAS" or "CAS" ? "/" + pin.Name : pin.Name;

    public override string ValueLabel => "16K × 1 DRAM";

    /// <summary>A cell as it holds now, given when its row was last refreshed.</summary>
    public bool Cell(int address, double now) => _cells[address & 0x3FFF] && now - _refreshed[(address & 0x3FFF) >> 7] <= RetentionTime;

    private void Decay(int row, double now)
    {
        if (now - _refreshed[row] > RetentionTime)
            for (var c = 0; c < 128; c++) _cells[(row << 7) | c] = false;
        _refreshed[row] = now;
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var t = context.Time;
        var gnd = context.NodeVoltage(Gnd);
        if (context.NodeVoltage(Vdd) - gnd < 10.8 || context.NodeVoltage(Vbb) - gnd > -4.5)
        {
            Array.Clear(_cells);
            Request(context, 0, LogicState.HighImpedance, 0);
            return;
        }

        var address = 0;
        for (var i = 0; i < 7; i++)
            if (context.ReadInput(_a[i], Levels).IsHigh()) address |= 1 << i;
        var ras = context.ReadInput(RowStrobe, Levels).IsHigh();
        var cas = context.ReadInput(ColumnStrobe, Levels).IsHigh();

        if (!ras && _lastRas)
        {
            _row = address;
            Decay(_row, t);                       // opening a row refreshes it
        }

        if (ras) _row = -1;
        if (!cas && _lastCas && _row >= 0)
        {
            _column = address;
            var cell = (_row << 7) | _column;
            if (context.ReadInput(WriteEnable, Levels).IsLow())
            {
                _cells[cell] = context.ReadInput(DataIn, Levels).IsHigh();
                _reading = false;
            }
            else (_reading, _bit) = (true, _cells[cell]);
        }

        if (cas) _reading = false;
        (_lastRas, _lastCas) = (ras, cas);
        Request(context, 0, !_reading ? LogicState.HighImpedance : _bit ? LogicState.High : LogicState.Low,
            _reading ? DelayFor(context) : 20e-9);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_cells);
        Array.Clear(_refreshed);
        (_lastRas, _lastCas, _row, _reading) = (true, true, -1, false);
    }
}
