using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// The ADC0804, the 8-bit successive-approximation converter made to sit on an 8080's or Z80's bus, in a DIP-20.
/// <para>
/// /WR rising with /CS low starts a conversion, sending /INTR high; about 70 clocks later the result is latched and
/// /INTR falls. /RD low with /CS low puts it on DB0-DB7 (three-state otherwise) and sends /INTR high again. The result
/// is 256 · (VIN+ − VIN−) / VREF, where VREF is twice the voltage on VREF/2 — which, left open, sits at half the
/// supply through the chip's own divider, so the span is VCC. The clock is <see cref="ClockFrequency"/>, which on the
/// chip comes from a resistor and capacitor on CLK R and CLK IN (f ≈ 1 / 1.1RC, 640 kHz for 10 kΩ and 150 pF).
/// </para>
/// </summary>
public sealed partial class Adc0804 : DigitalIc, IBreakpointSource
{
    private readonly Terminal[] _db = new Terminal[8];
    private bool _lastWr = true, _lastRd = true;
    private double _doneAt = double.NaN;
    private int _result;
    private bool _intr = true;

    public Adc0804() : base(20)
    {
        PropagationDelay = 100e-9;
        var pins = new Terminal[21];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 20));
        ChipSelect = Pin(1, "CS", TerminalType.Input);
        Read = Pin(2, "RD", TerminalType.Input);
        Write = Pin(3, "WR", TerminalType.Input);
        Pin(4, "CLK IN", TerminalType.Passive);
        InterruptOut = Pin(5, "INTR", TerminalType.Output);
        InputPlus = Pin(6, "VIN+", TerminalType.Passive);
        InputMinus = Pin(7, "VIN-", TerminalType.Passive);
        AnalogGround = Pin(8, "AGND", TerminalType.Passive);
        HalfReference = Pin(9, "VREF/2", TerminalType.Passive);
        Gnd = Pin(10, "DGND", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _db[7 - i] = Pin(11 + i, $"DB{7 - i}", TerminalType.Output);
        Pin(19, "CLK R", TerminalType.Passive);
        Vcc = Pin(20, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, Read, Write], [InterruptOut, .. _db]);
    }

    public Terminal ChipSelect { get; }
    public Terminal Read { get; }
    public Terminal Write { get; }
    public Terminal InterruptOut { get; }
    public Terminal InputPlus { get; }
    public Terminal InputMinus { get; }
    public Terminal AnalogGround { get; }
    public Terminal HalfReference { get; }

    /// <summary>DB0-DB7, DB0 first.</summary>
    public IReadOnlyList<Terminal> Data => _db;

    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 640e3;

    public int Result => _result;

    public override string PartNumber => "ADC0804";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name is "CS" or "RD" or "WR" or "INTR" ? "/" + pin.Name : pin.Name;

    public override string ValueLabel => $"ADC0804 {_result}";

    private double ConversionTime => 70 / Math.Max(ClockFrequency, 1);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var (vcc, agnd, half) = (system.Node(Vcc), system.Node(AnalogGround), system.Node(HalfReference));

        // VREF/2's divider, and the inputs' leakage.
        system.StampResistor(vcc, half, 2.4e3);
        system.StampResistor(half, agnd, 2.4e3);
        system.StampResistor(system.Node(InputPlus), agnd, 10e6);
        system.StampResistor(system.Node(InputMinus), agnd, 10e6);
        system.StampResistor(agnd, system.Node(Gnd), 1e-3);
    }

    public double? NextBreakpointAfter(double time) => _doneAt > time + 1e-12 ? _doneAt : null;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var t = context.Time;
        var cs = !context.ReadInput(ChipSelect, Levels).IsHigh();
        var wr = context.ReadInput(Write, Levels).IsHigh();
        var rd = context.ReadInput(Read, Levels).IsHigh();
        if (cs && wr && !_lastWr)
        {
            _intr = true;
            _doneAt = t + ConversionTime;
        }

        if (!double.IsNaN(_doneAt) && t >= _doneAt - 1e-12)
        {
            var vref = 2 * (context.NodeVoltage(HalfReference) - context.NodeVoltage(AnalogGround));
            var vin = context.NodeVoltage(InputPlus) - context.NodeVoltage(InputMinus);
            _result = (int)Math.Clamp(Math.Round(256 * vin / Math.Max(vref, 1e-3)), 0, 255);
            _intr = false;
            _doneAt = double.NaN;
            NotifyValueChanged();
        }

        if (cs && !rd && _lastRd) _intr = true;
        (_lastWr, _lastRd) = (wr, rd);
        var reading = cs && !rd;
        var delay = DelayFor(context);
        Request(context, 0, _intr ? LogicState.High : LogicState.Low, delay);
        for (var i = 0; i < 8; i++)
            Request(context, 1 + i,
                !reading ? LogicState.HighImpedance : ((_result >> i) & 1) != 0 ? LogicState.High : LogicState.Low, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_lastWr, _lastRd, _doneAt, _result, _intr) = (true, true, double.NaN, 0, true);
    }
}

/// <summary>
/// The DAC0808 8-bit multiplying DAC: a current out of IOUT (pin 4 sinks it) of IREF · (A1/2 + A2/4 + … + A8/256), A1
/// the most significant bit, where IREF is the current into VREF+ (pin 14), which the chip holds at VREF−'s voltage —
/// so a resistor from a reference to VREF+ sets it, 2 mA for 10 V through 5 kΩ. An op-amp's virtual ground on IOUT
/// turns it into a voltage: with 5 kΩ of feedback, 0 to 10 V less a step. The inputs are TTL, high above 1.4 V.
/// </summary>
public sealed partial class Dac0808 : CircuitComponent
{
    private readonly Terminal[] _a = new Terminal[8];

    public Dac0808()
    {
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        Pin(1, "NC", TerminalType.Passive);
        Gnd = Pin(2, "GND", TerminalType.Ground);
        Vee = Pin(3, "VEE", TerminalType.Power);
        Output = Pin(4, "IOUT", TerminalType.Passive);
        for (var i = 0; i < 8; i++) _a[i] = Pin(5 + i, $"A{i + 1}", TerminalType.Input);
        Vcc = Pin(13, "VCC", TerminalType.Power);
        ReferencePlus = Pin(14, "VREF+", TerminalType.Passive);
        ReferenceMinus = Pin(15, "VREF-", TerminalType.Passive);
        Pin(16, "COMP", TerminalType.Passive);
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal Gnd { get; }
    public Terminal Vee { get; }
    public Terminal Vcc { get; }
    public Terminal Output { get; }
    public Terminal ReferencePlus { get; }
    public Terminal ReferenceMinus { get; }

    /// <summary>A1-A8, A1 the most significant.</summary>
    public IReadOnlyList<Terminal> Inputs => _a;

    /// <summary>The code on A1-A8 at the last solve, A1 the top bit.</summary>
    public int Code { get; private set; }

    public override string ComponentType => "DAC0808";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"DAC0808 {Code}";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 1;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (gnd, vee, output, plus, minus) = (system.Node(Gnd), system.Node(Vee), system.Node(Output), system.Node(ReferencePlus), system.Node(ReferenceMinus));
        var code = 0;
        for (var i = 0; i < 8; i++)
        {
            var input = system.Node(_a[i]);
            system.StampResistor(input, gnd, 100e3);
            if (system.IterationVoltage(input) - system.IterationVoltage(gnd) > 1.4) code |= 0x80 >> i;
        }

        Code = code;

        // VREF+ held at VREF−'s voltage; the current into it (the branch) goes on to VEE.
        var branch = system.Branch(this, 0);
        system.Add(plus, branch, 1.0);
        system.Add(vee, branch, -1.0);
        system.Add(branch, plus, 1.0);
        system.Add(branch, minus, -1.0);
        system.StampResistor(minus, gnd, 100e6);

        // IOUT sinks code/256 of it.
        var k = code / 256.0;
        system.Add(output, branch, k);
        system.Add(vee, branch, -k);
        system.StampResistor(output, gnd, 100e6);
        system.StampCurrentSource(system.Node(Vcc), gnd, 2.3e-3);
    }
}
