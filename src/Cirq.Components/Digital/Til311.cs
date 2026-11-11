using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// The Texas Instruments TIL311, the hexadecimal display with its own latch and decoder in a 14-pin package: four data
/// inputs (A the lowest), a latch strobe (low: the display follows the inputs; high: it holds), and blanking (high:
/// dark), the 0-F shown on a 4 × 7 matrix of red dots. The board on every hobbyist's 1970s address bus. Its LED supply
/// (pin 1) takes about 60 mA when lit; the two decimal points are LED cathodes of their own, not modelled. Pinout: 1 LED
/// supply, 2 B, 3 A, 4 left DP, 5 strobe, 7 GND, 8 blanking, 10 right DP, 12 D, 13 C, 14 VCC.
/// </summary>
public sealed partial class Til311 : DigitalIc
{
    private readonly Terminal[] _data = new Terminal[4];
    private int _value;

    public Til311() : base(14)
    {
        var pins = new Terminal[15];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 14));
        LedSupply = Pin(1, "LED+", TerminalType.Passive);
        _data[1] = Pin(2, "B", TerminalType.Input);
        _data[0] = Pin(3, "A", TerminalType.Input);
        Pin(4, "LDP", TerminalType.Passive);
        Strobe = Pin(5, "STB", TerminalType.Input);
        Pin(6, "NC", TerminalType.Passive);
        Gnd = Pin(7, "GND", TerminalType.Ground);
        Blank = Pin(8, "BL", TerminalType.Input);
        Pin(9, "NC", TerminalType.Passive);
        Pin(10, "RDP", TerminalType.Passive);
        Pin(11, "NC", TerminalType.Passive);
        _data[3] = Pin(12, "D", TerminalType.Input);
        _data[2] = Pin(13, "C", TerminalType.Input);
        Vcc = Pin(14, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Strobe, Blank, .. _data], []);
    }

    public Terminal LedSupply { get; }
    public Terminal Strobe { get; }
    public Terminal Blank { get; }

    /// <summary>A-D, A the least significant.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>The digit shown, 0-15, or null while blanked.</summary>
    public int? Shown { get; private set; }

    public override string PartNumber => "TIL311";

    public override string ComponentType => "Hex Display";

    public override string DesignatorPrefix => "DS";

    public override string ValueLabel => Shown is { } v ? $"TIL311 {v:X}" : "TIL311";

    public override void StampMatrix(Cirq.Core.Simulation.MnaSystem system, Cirq.Core.Simulation.SimulationState state)
    {
        base.StampMatrix(system, state);
        system.StampResistor(system.Node(LedSupply), system.Node(Gnd), Shown is null ? 100e3 : 80);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        if (context.ReadInput(Strobe, Levels).IsLow())
        {
            var v = 0;
            for (var i = 0; i < 4; i++)
                if (context.ReadInput(_data[i], Levels).IsHigh()) v |= 1 << i;
            _value = v;
        }

        var shown = context.ReadInput(Blank, Levels).IsHigh() ? (int?)null : _value;
        if (shown != Shown)
        {
            Shown = shown;
            NotifyValueChanged();
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_value, Shown) = (0, null);
    }
}
