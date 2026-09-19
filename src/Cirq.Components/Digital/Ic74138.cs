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

/// <summary>
/// 8-to-1 multiplexer with complementary outputs.
/// <para>
/// Pinout: 1=D3, 2=D2, 3=D1, 4=D0, 5=Y, 6=W, 7=E, 8=GND,
/// 9=A0, 10=A1, 11=A2, 12=D7, 13=D6, 14=D5, 15=D4, 16=VCC.
/// </para>
/// <para>
/// With the strobe released the part forces Y low and W high regardless of the address, which is
/// how several of these are wired together onto a shared bus.
/// </para>
/// </summary>
public sealed class Ic74151 : DigitalIc
{
    private readonly Terminal[] _data = new Terminal[8];

    public Ic74151() : base(16)
    {
        PropagationDelay = 22e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _data[3] = Pin(1, "D3", TerminalType.Input);
        _data[2] = Pin(2, "D2", TerminalType.Input);
        _data[1] = Pin(3, "D1", TerminalType.Input);
        _data[0] = Pin(4, "D0", TerminalType.Input);
        Output = Pin(5, "Y", TerminalType.Output);
        InvertedOutput = Pin(6, "W", TerminalType.Output);
        Strobe = Pin(7, "E", TerminalType.Input);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        SelectA = Pin(9, "A0", TerminalType.Input);
        SelectB = Pin(10, "A1", TerminalType.Input);
        SelectC = Pin(11, "A2", TerminalType.Input);
        _data[7] = Pin(12, "D7", TerminalType.Input);
        _data[6] = Pin(13, "D6", TerminalType.Input);
        _data[5] = Pin(14, "D5", TerminalType.Input);
        _data[4] = Pin(15, "D4", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _data, SelectA, SelectB, SelectC, Strobe], [Output, InvertedOutput]);
    }

    /// <summary>The eight data inputs, D0 through D7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    public Terminal SelectA { get; }
    public Terminal SelectB { get; }
    public Terminal SelectC { get; }

    /// <summary>Active-low strobe. High forces the outputs to their inactive state.</summary>
    public Terminal Strobe { get; }

    public Terminal Output { get; }

    public Terminal InvertedOutput { get; }

    public override string PartNumber => "74151";

    /// <summary>The data input currently routed to the output, or -1 while strobed off.</summary>
    public int SelectedInput { get; private set; } = -1;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (!context.ReadInput(Strobe, Levels).IsLow())
        {
            SelectedInput = -1;
            context.Schedule(this, 0, LogicState.Low, delay);
            context.Schedule(this, 1, LogicState.High, delay);
            return;
        }

        var address = 0;
        if (context.ReadInput(SelectA, Levels).IsHigh()) address |= 1;
        if (context.ReadInput(SelectB, Levels).IsHigh()) address |= 2;
        if (context.ReadInput(SelectC, Levels).IsHigh()) address |= 4;

        SelectedInput = address;

        var selected = context.ReadInput(_data[address], Levels);
        context.Schedule(this, 0, selected, delay);
        context.Schedule(this, 1, selected.Invert(), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        SelectedInput = -1;
    }
}
