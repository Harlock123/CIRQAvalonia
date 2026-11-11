using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;

namespace Cirq.Components.Buses;

/// <summary>
/// The Nokia 5110 LCD module: an 84 × 48 PCD8544 on SPI, its header RST, CE, DC, DIN, CLK, VCC, BL and GND.
/// <para>
/// While CE is low each byte is clocked in on CLK's rising edges, most significant bit first, and DC, read with the
/// eighth bit, says whether it was a command (low) or display data (high). The commands: function set (0x20, with PD for
/// power-down, V for vertical addressing and H for the extended set); in the basic set display control (0x08 blank, 0x09
/// all on, 0x0C normal, 0x0D inverse), set Y (0x40-0x45, the bank of eight rows) and set X (0x80-0xD3, the column); in
/// the extended set contrast (0x80 | Vop), temperature and bias, taken and noted. A data byte goes down a column of the
/// current bank, bit 0 at the top, and X (or Y, addressing vertically) steps on. RST low clears it and powers it down.
/// </para>
/// </summary>
public sealed partial class Pcd8544 : DigitalIc
{
    public const int Width = 84, Height = 48;
    private readonly byte[] _ram = new byte[Width * Height / 8];
    private int _shift, _bits, _x, _y;
    private bool _lastClock;
    private bool _extended, _vertical;

    public Pcd8544() : base(8)
    {
        PropagationDelay = 0;
        Levels = LogicLevels.Cmos33V;
        Reset = new Terminal("rst", "RST", TerminalType.Input, new Point(-70, -60));
        ChipEnable = new Terminal("ce", "CE", TerminalType.Input, new Point(-50, -60));
        DataCommand = new Terminal("dc", "DC", TerminalType.Input, new Point(-30, -60));
        DataIn = new Terminal("din", "DIN", TerminalType.Input, new Point(-10, -60));
        Clock = new Terminal("clk", "CLK", TerminalType.Input, new Point(10, -60));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(30, -60));
        Backlight = new Terminal("bl", "BL", TerminalType.Passive, new Point(50, -60));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(70, -60));
        Terminals = [Reset, ChipEnable, DataCommand, DataIn, Clock, Vcc, Backlight, Gnd];
        ConfigurePins([Reset, ChipEnable, DataCommand, DataIn, Clock], []);
        PowerDown();
    }

    public Terminal Reset { get; }
    public Terminal ChipEnable { get; }
    public Terminal DataCommand { get; }
    public Terminal DataIn { get; }
    public Terminal Clock { get; }
    public Terminal Backlight { get; }

    public bool PoweredDown { get; private set; }

    /// <summary>0 blank, 1 all on, 2 normal, 3 inverse: display control's D and E.</summary>
    public int DisplayMode { get; private set; }

    public int Contrast { get; private set; }

    public IReadOnlyList<byte> Ram => _ram;

    /// <summary>Whether a pixel is dark, as the panel shows it.</summary>
    public bool Pixel(int x, int y)
    {
        if (PoweredDown || DisplayMode == 0) return false;
        if (DisplayMode == 1) return true;
        var on = (_ram[((y / 8) * Width) + x] & (1 << (y % 8))) != 0;
        return DisplayMode == 3 ? !on : on;
    }

    public override string PartNumber => "PCD8544";

    public override string ComponentType => "Nokia 5110 LCD";

    public override string ValueLabel => PoweredDown ? "Nokia 5110 (off)" : "Nokia 5110 84×48";

    public override bool LabelsPins => true;

    private void PowerDown()
    {
        Array.Clear(_ram);
        (_x, _y, _extended, _vertical, PoweredDown, DisplayMode, Contrast) = (0, 0, false, false, true, 0, 0);
    }

    private void Command(int b)
    {
        if ((b & 0xF8) == 0x20)
        {
            PoweredDown = (b & 4) != 0;
            _vertical = (b & 2) != 0;
            _extended = (b & 1) != 0;
        }
        else if (_extended)
        {
            if ((b & 0x80) != 0) Contrast = b & 0x7F;
        }
        else if ((b & 0x80) != 0) _x = Math.Min(b & 0x7F, Width - 1);
        else if ((b & 0xF8) == 0x40) _y = Math.Min(b & 7, 5);
        else if ((b & 0xFA) == 0x08) DisplayMode = ((b >> 1) & 2) | (b & 1);
    }

    private void Data(int b)
    {
        _ram[(_y * Width) + _x] = (byte)b;
        if (_vertical)
        {
            if (++_y > 5) { _y = 0; _x = (_x + 1) % Width; }
        }
        else if (++_x >= Width) { _x = 0; _y = (_y + 1) % 6; }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        if (context.ReadInput(Reset, Levels).IsLow())
        {
            PowerDown();
            (_bits, _shift) = (0, 0);
            return;
        }

        var clock = context.ReadInput(Clock, Levels).IsHigh();
        if (context.ReadInput(ChipEnable, Levels).IsHigh()) _bits = 0;
        else if (clock && !_lastClock)
        {
            _shift = ((_shift << 1) | (context.ReadInput(DataIn, Levels).IsHigh() ? 1 : 0)) & 0xFF;
            if (++_bits == 8)
            {
                if (context.ReadInput(DataCommand, Levels).IsHigh()) Data(_shift);
                else Command(_shift);
                _bits = 0;
                NotifyValueChanged();
            }
        }

        _lastClock = clock;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        PowerDown();
        (_bits, _shift, _lastClock) = (0, 0, false);
    }
}
