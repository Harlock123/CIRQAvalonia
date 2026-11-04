using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 3-to-8 line decoder / demultiplexer with active-low outputs.
/// <para>
/// Pinout: 1=A0, 2=A1, 3=A2, 4=E1, 5=E2, 6=E3, 7=Y7, 8=GND,
/// 9=Y6, 10=Y5, 11=Y4, 12=Y3, 13=Y2, 14=Y1, 15=Y0, 16=VCC.
/// </para>
/// <para>
/// All three enables must agree before anything is decoded: E1 and E2 are active low and E3 is
/// active high. That asymmetry is what lets several of these be cascaded into a larger decoder
/// without any glue logic.
/// </para>
/// </summary>
public sealed class Ic74138 : DigitalIc
{
    private readonly Terminal[] _outputs = new Terminal[8];

    public Ic74138() : base(16)
    {
        PropagationDelay = 21e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        SelectA = Pin(1, "A0", TerminalType.Input);
        SelectB = Pin(2, "A1", TerminalType.Input);
        SelectC = Pin(3, "A2", TerminalType.Input);
        Enable1 = Pin(4, "E1", TerminalType.Input);
        Enable2 = Pin(5, "E2", TerminalType.Input);
        Enable3 = Pin(6, "E3", TerminalType.Input);

        _outputs[7] = Pin(7, "Y7", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        _outputs[6] = Pin(9, "Y6", TerminalType.Output);
        _outputs[5] = Pin(10, "Y5", TerminalType.Output);
        _outputs[4] = Pin(11, "Y4", TerminalType.Output);
        _outputs[3] = Pin(12, "Y3", TerminalType.Output);
        _outputs[2] = Pin(13, "Y2", TerminalType.Output);
        _outputs[1] = Pin(14, "Y1", TerminalType.Output);
        _outputs[0] = Pin(15, "Y0", TerminalType.Output);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([SelectA, SelectB, SelectC, Enable1, Enable2, Enable3], _outputs);
    }

    public Terminal SelectA { get; }
    public Terminal SelectB { get; }
    public Terminal SelectC { get; }

    /// <summary>Active-low enable.</summary>
    public Terminal Enable1 { get; }

    /// <summary>Active-low enable.</summary>
    public Terminal Enable2 { get; }

    /// <summary>Active-high enable.</summary>
    public Terminal Enable3 { get; }

    public override string PartNumber => "74138";

    /// <summary>The eight active-low outputs, Y0 through Y7.</summary>
    public IReadOnlyList<Terminal> Outputs => _outputs;

    /// <summary>The address currently selected, or -1 when the part is disabled.</summary>
    public int SelectedOutput { get; private set; } = -1;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var enabled =
            context.ReadInput(Enable1, Levels).IsLow() &&
            context.ReadInput(Enable2, Levels).IsLow() &&
            context.ReadInput(Enable3, Levels).IsHigh();

        var address = 0;
        if (context.ReadInput(SelectA, Levels).IsHigh()) address |= 1;
        if (context.ReadInput(SelectB, Levels).IsHigh()) address |= 2;
        if (context.ReadInput(SelectC, Levels).IsHigh()) address |= 4;

        SelectedOutput = enabled ? address : -1;

        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
            context.Schedule(this, i, enabled && i == address ? LogicState.Low : LogicState.High, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        SelectedOutput = -1;
    }
}
