using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The SID — the 6581 Sound Interface Device and its successor the 8580 — on its DIP-28 pinout:
/// three voices clocked from Φ2, a multimode filter on the two capacitors wired to its CAP pins, a
/// master volume, two paddle inputs, and an analog AUDIO OUT that drives the circuit.
/// <para>
/// <b>The bus.</b> Selected while /CS is low; A0-A4 pick one of 29 registers ($00-$1C). A write takes
/// the data bus as Φ2 falls; a read drives it while Φ2 is high. The voices are in <see cref="SidCore"/>,
/// which runs once per Φ2 cycle: at a 1 MHz clock that is a million updates of the output a second —
/// the chip's own rate, with no decimation.
/// </para>
/// <para>
/// <b>The output is a voltage source in the solve.</b> AUDIO OUT is stamped as a source behind
/// <see cref="OutputResistance"/>, at a DC level of half VDD (6 V on the 6581's 12 V supply, 4.5 V
/// on the 8580's 9 V) plus <see cref="OutputScale"/> volts per full-scale voice at full volume. The
/// voices and EXT IN are mixed each cycle into what goes round the filter and what goes through it;
/// the first moves only the source's value, so a voice changing never refactors the matrix. On the
/// silicon the output is a source follower that needs an external resistor to ground; here it can
/// both source and sink, so the resistor does no harm and is not required.
/// </para>
/// <para>
/// <b>The filter is analog, on the real capacitors.</b> It is a two-integrator-loop state-variable
/// filter, as the SID's is: each integrator is an ideal amplifier holding its CAPxA pin at the
/// reference with the capacitor from CAPxA to CAPxB in its feedback, fed by a transconductance set by
/// the cutoff register. The high-pass is the summing node (input, minus the low-pass, minus the band-pass
/// over Q), CAP1B is the band-pass and CAP2B the low-pass, and the mode bits add any of the three
/// into the output — so a notch is low-pass plus high-pass, as on the chip. The capacitors are parts
/// on the canvas, and they set the frequency the way they do on the board: the transconductance is
/// scaled so that the datasheet's capacitor (<see cref="NominalCapacitance"/>: 470 pF on the 6581 as
/// the C64 fits it, 22 nF on the 8580) gives the cutoff curve in <see cref="SidCore.CutoffFrequency"/>,
/// and a different capacitor moves the whole range in inverse proportion. The 6581's curve is an
/// approximation of a chip-to-chip variable one, and its distortion — the filter's non-linearity,
/// and the way it saturates — is not modelled; the 8580's near-linear filter is closer to life.
/// The 6581's mixer has a DC offset that moves with the volume (<see cref="MixerOffset"/>), which is
/// the trick C64 software plays samples through by writing the volume register; the 8580 has very
/// little, and plays them very quietly.
/// </para>
/// <para>
/// <b>Paddles.</b> POTX and POTY are measured as the chip measures them, against the real resistor
/// and capacitor on the pin: every 512 cycles the chip holds the pin low for 256 to discharge the
/// capacitor, then lets it charge through the paddle's resistance and counts the cycles until it
/// crosses a threshold, up to 255. The threshold here (0.42 of VCC) is chosen so the C64's paddle —
/// 470 kΩ to +5 V, with 1,000 pF to ground at the pin — reads 255 at full resistance; with nothing on
/// the pin it reads 255.
/// </para>
/// <para>
/// <b>Speed, honestly.</b> The chip is clocked, and its output updated, every Φ2 cycle, and the solver
/// takes a time point at each clock edge anyway — so the audio is solved at the cycle rate and is
/// as smooth as the chip's own. But the whole computer is being solved, so it runs at the circuit's
/// speed, 5 to 20 ms of simulated time a second, not in real time: a second of music is a minute
/// or more of solving. Listen with the speaker's recording, at full speed, afterwards.
/// </para>
/// <para>
/// Pinout (DIP-28): 1=CAP1A, 2=CAP1B, 3=CAP2A, 4=CAP2B, 5=/RES, 6=Φ2, 7=R/W, 8=/CS, 9-13=A0-A4,
/// 14=GND, 15-22=D0-D7, 23=POTY, 24=POTX, 25=VCC, 26=EXT IN, 27=AUDIO OUT, 28=VDD.
/// </para>
/// </summary>
public abstract partial class SidChip : DigitalIc, IRunState
{
    /// <summary>The fraction of VCC a paddle pin has to charge to before the count stops.</summary>
    public const double PotThreshold = 0.42;

    /// <summary>The leakage across each integrator, keeping it defined with no capacitor fitted.</summary>
    private const double IntegratorLeak = 1e9;

    /// <summary>The paddle pins' discharge transistor, in ohms.</summary>
    public const double PotDischargeResistance = 10.0;

    /// <summary>What EXT IN looks like from outside: a resistance to its internal bias.</summary>
    private const double ExternalInputResistance = 100e3;

    private readonly Terminal[] _address = new Terminal[5];
    private readonly Terminal[] _data = new Terminal[8];

    // Outputs: D0-D7. Branches after those: AUDIO OUT, then the two integrators.

    private LogicState _lastPhi2 = LogicState.Unknown;
    private double _lastFall = double.NaN;
    private int _potCycle;
    private int _potXCount, _potYCount;
    private bool _potXDone, _potYDone;
    private double _centre;
    private double _supply = 5.0;

    private protected SidChip(SidModel model) : base(28)
    {
        Model = model;
        Core = new SidCore(model);
        PropagationDelay = 40e-9;

        var pins = new Terminal[29];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 28));

        Cap1A = Pin(1, "CAP1A", TerminalType.Passive);
        Cap1B = Pin(2, "CAP1B", TerminalType.Passive);
        Cap2A = Pin(3, "CAP2A", TerminalType.Passive);
        Cap2B = Pin(4, "CAP2B", TerminalType.Passive);
        Reset = Pin(5, "RES", TerminalType.Input);
        Clock = Pin(6, "PHI2", TerminalType.Input);
        ReadWrite = Pin(7, "RW", TerminalType.Input);
        ChipSelect = Pin(8, "CS", TerminalType.Input);
        for (var i = 0; i < 5; i++) _address[i] = Pin(9 + i, $"A{i}", TerminalType.Input);
        Gnd = Pin(14, "GND", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _data[i] = Pin(15 + i, $"D{i}", TerminalType.Bidirectional);
        PotY = Pin(23, "POTY", TerminalType.Passive);
        PotX = Pin(24, "POTX", TerminalType.Passive);
        Vcc = Pin(25, "VCC", TerminalType.Power);
        ExternalIn = Pin(26, "EXTIN", TerminalType.Input);
        AudioOut = Pin(27, "AUDIO", TerminalType.Output);
        Vdd = Pin(28, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, ReadWrite, ChipSelect, Reset, .. _address, .. _data], [.. _data]);
    }

    public SidModel Model { get; }

    /// <summary>The voices, the envelopes and the registers.</summary>
    public SidCore Core { get; private set; }

    public IReadOnlyList<Terminal> Address => _address;

    public IReadOnlyList<Terminal> Data => _data;

    public Terminal Cap1A { get; }

    public Terminal Cap1B { get; }

    public Terminal Cap2A { get; }

    public Terminal Cap2B { get; }

    public Terminal Reset { get; }

    public Terminal Clock { get; }

    public Terminal ReadWrite { get; }

    /// <summary>/CS, active low.</summary>
    public Terminal ChipSelect { get; }

    public Terminal PotX { get; }

    public Terminal PotY { get; }

    /// <summary>EXT IN: an analog input mixed with the voices, through the filter or round it.</summary>
    public Terminal ExternalIn { get; }

    /// <summary>AUDIO OUT: the analog output.</summary>
    public Terminal AudioOut { get; }

    /// <summary>VDD: 12 V on the 6581, 9 V on the 8580. The output sits at half of it.</summary>
    public Terminal Vdd { get; }

    /// <summary>The filter capacitor the datasheet's cutoff range is for, in farads.</summary>
    public double NominalCapacitance => Model == SidModel.Mos8580 ? 22e-9 : 470e-12;

    /// <summary>Volts at AUDIO OUT for one full-scale voice at full volume.</summary>
    [ObservableProperty]
    public partial double OutputScale { get; set; } = 0.5;

    /// <summary>The resistance behind AUDIO OUT, in ohms.</summary>
    [ObservableProperty]
    public partial double OutputResistance { get; set; } = 470.0;

    /// <summary>
    /// The mixer's DC offset, in voices, multiplied by the volume: what makes writing the volume
    /// register a 4-bit sample player on the 6581.
    /// </summary>
    public double MixerOffset => Model == SidModel.Mos8580 ? 0.02 : 0.5;

    /// <summary>The clock it is being run at, measured from Φ2, in hertz; NaN before two cycles.</summary>
    public double ClockFrequency { get; private set; } = double.NaN;

    /// <summary>A voice's frequency in hertz at the clock it is running at (1 MHz until it has been measured).</summary>
    public double VoiceFrequency(int voice) =>
        Core.Voices[voice].Frequency * (double.IsNaN(ClockFrequency) ? 1e6 : ClockFrequency) / (1 << 24);

    /// <summary>The cutoff the register asks for, in hertz, with the datasheet capacitors.</summary>
    public double CutoffFrequency => SidCore.CutoffFrequency(Model, Core.Cutoff);

    /// <summary>What AUDIO OUT is being driven to, before any load, in volts.</summary>
    public double OutputLevel { get; private set; }

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RES" or "CS" => "/" + pin.Name,
        "RW" => "R/W",
        "EXTIN" => "EXT IN",
        "AUDIO" => "AUDIO OUT",
        _ => pin.Name,
    };

    public override string ValueLabel => string.Create(CultureInfo.InvariantCulture,
        $"vol {Core.Volume} gates {string.Concat(Core.Voices.Select(v => v.Gated ? '1' : '0'))}");

    /// <summary>The analog output and the two integrators, after the logic pins' branches.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + 3;

    // ---- the analog side ---------------------------------------------------------

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        var gnd = system.Node(Gnd);
        var vdd = system.Node(Vdd);
        var c1a = system.Node(Cap1A);
        var c1b = system.Node(Cap1B);
        var c2a = system.Node(Cap2A);
        var c2b = system.Node(Cap2B);
        var outBranch = system.Branch(this, OutputTerminals.Count);
        var amp1 = system.Branch(this, OutputTerminals.Count + 1);
        var amp2 = system.Branch(this, OutputTerminals.Count + 2);

        // The reference everything sits on is half VDD, stamped as half of VDD's node voltage rather
        // than as a number, so the bias point is right from the first solve and follows the supply.
        const double half = 0.5;

        var gm = 2 * Math.PI * CutoffFrequency * NominalCapacitance;
        var k = 1.0 / SidCore.QualityFactor(Model, Core.Resonance);
        var input = Core.FilterInput;

        // Two ideal integrators: each amplifier holds its A pin at the reference, its output on B.
        StampAmplifier(system, amp1, c1a, c1b, gnd, vdd);
        StampAmplifier(system, amp2, c2a, c2b, gnd, vdd);
        system.StampResistor(c1a, c1b, IntegratorLeak);
        system.StampResistor(c2a, c2b, IntegratorLeak);

        // Into the first, gm × the high-pass, input + k·BP' − LP' (BP' = CAP1B less the reference,
        // the band-pass inverted; LP' = CAP2B less it, the low-pass); into the second, gm × BP'. The
        // capacitors integrate them.
        Inject(system, c1a, gnd, [(c1b, gm * k), (c2b, -gm), (vdd, half * gm * (1 - k))], gm * input);
        Inject(system, c2a, gnd, [(c1b, gm), (vdd, -half * gm)], 0);

        // AUDIO OUT: the volume times what went round the filter and its DC offset, and the modes
        // chosen out of the filter: low-pass, band-pass (inverted back) and high-pass, the summing node.
        var g = OutputScale * Core.Volume / 15.0;
        double sL = Core.LowPass ? 1 : 0, sB = Core.BandPass ? 1 : 0, sH = Core.HighPass ? 1 : 0;
        var onBand = g * ((sH * k) - sB);
        var onLow = g * (sL - sH);
        var onReference = half * (1 - onBand - onLow);

        var output = system.Node(AudioOut);
        system.Add(output, outBranch, 1.0);
        system.Add(gnd, outBranch, -1.0);
        system.Add(outBranch, output, 1.0);
        system.Add(outBranch, outBranch, -Math.Max(OutputResistance, 1e-3));
        system.Add(outBranch, c1b, -onBand);
        system.Add(outBranch, c2b, -onLow);
        system.Add(outBranch, vdd, -onReference);
        system.Add(outBranch, gnd, -1.0 + onBand + onLow + onReference);
        system.AddRhs(outBranch, (g * (Core.Direct + MixerOffset)) + (g * sH * input));

        // The paddle pins, held low for the first half of the paddle cycle.
        if (_potCycle < 256)
        {
            system.StampResistor(system.Node(PotX), gnd, PotDischargeResistance);
            system.StampResistor(system.Node(PotY), gnd, PotDischargeResistance);
        }

        // EXT IN sits at the reference through its input resistance until something drives it.
        var ext = system.Node(ExternalIn);
        system.StampConductance(ext, gnd, 1.0 / ExternalInputResistance);
        Inject(system, ext, gnd, [(vdd, half / ExternalInputResistance)], 0);
    }

    /// <summary>An ideal amplifier: its output current into <paramref name="output"/> holds <paramref name="input"/> at half VDD.</summary>
    private static void StampAmplifier(MnaSystem system, int branch, int input, int output, int gnd, int vdd)
    {
        system.Add(output, branch, 1.0);
        system.Add(gnd, branch, -1.0);
        system.Add(branch, input, 1.0);
        system.Add(branch, vdd, -0.5);
        system.Add(branch, gnd, -0.5);
    }

    /// <summary>A current Σ a·(V(n) − V(gnd)) + constant into <paramref name="node"/>, drawn from <paramref name="gnd"/>.</summary>
    private static void Inject(MnaSystem system, int node, int gnd, (int Node, double Gain)[] terms, double constant)
    {
        foreach (var (n, a) in terms)
        {
            system.Add(node, n, -a);
            system.Add(node, gnd, a);
            system.Add(gnd, n, a);
            system.Add(gnd, gnd, -a);
        }

        system.AddRhs(node, constant);
        system.AddRhs(gnd, -constant);
    }

    // ---- the logic ------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var phi2 = context.ReadInput(Clock, Levels);
        var previous = _lastPhi2;
        _lastPhi2 = phi2;

        _supply = SupplyVoltage(context);
        _centre = Math.Max(context.NodeVoltage(Vdd) - context.NodeVoltage(Gnd), 0.0) / 2;

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            if (Core.Cycles > 0 || Core.LastWritten != 0) Core = new SidCore(Model);
            Drive(context, driveData: false, 0);
            return;
        }

        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var register = 0;
        for (var i = 0; i < 5; i++)
            if (context.ReadInput(_address[i], Levels).IsHigh()) register |= 1 << i;

        if (!context.IsInitializing && previous.IsHigh() && phi2.IsLow())
        {
            if (!double.IsNaN(_lastFall) && context.Time > _lastFall) ClockFrequency = 1.0 / (context.Time - _lastFall);
            _lastFall = context.Time;

            if (selected && !reading)
            {
                var value = 0;
                for (var i = 0; i < 8; i++)
                    if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;
                Core.Write(register, (byte)value);
            }

            Paddles(context);
            Core.ExternalInput = (context.NodeVoltage(ExternalIn) - context.NodeVoltage(Gnd) - _centre) / Math.Max(OutputScale, 1e-9);
            Core.Clock();
            OutputLevel = _centre + (OutputScale * Core.Volume / 15.0 * (Core.Direct + MixerOffset));
        }

        var driveData = selected && reading && phi2.IsHigh();
        Drive(context, driveData, driveData ? Core.Read(register) : (byte)0);
    }

    /// <summary>The paddle cycle: 256 cycles discharging the pins, 256 counting them charge back.</summary>
    private void Paddles(IDigitalContext context)
    {
        if (_potCycle >= 256)
        {
            var threshold = PotThreshold * _supply;
            if (!_potXDone && context.NodeVoltage(PotX) - context.NodeVoltage(Gnd) < threshold) _potXCount++;
            else _potXDone = true;
            if (!_potYDone && context.NodeVoltage(PotY) - context.NodeVoltage(Gnd) < threshold) _potYCount++;
            else _potYDone = true;
        }

        if (++_potCycle < 512) return;

        Core.PotX = (byte)Math.Min(_potXCount, 255);
        Core.PotY = (byte)Math.Min(_potYCount, 255);
        _potCycle = 0;
        _potXCount = _potYCount = 0;
        _potXDone = _potYDone = false;
    }

    private void Drive(IDigitalContext context, bool driveData, byte value)
    {
        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
            Request(context, i, driveData ? ((value >> i) & 1) != 0 ? LogicState.High : LogicState.Low : LogicState.HighImpedance, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Core = new SidCore(Model);
        _lastPhi2 = LogicState.Unknown;
        _lastFall = double.NaN;
        ClockFrequency = double.NaN;
        _potCycle = 0;
        _potXCount = _potYCount = 0;
        _potXDone = _potYDone = false;
        OutputLevel = 0;
    }

    private sealed record RunState(SidCore Core, double Clock, double Output);

    public object CaptureRunState() => new RunState(Core.Clone(), ClockFrequency, OutputLevel);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        Core = run.Core.Clone();
        ClockFrequency = run.Clock;
        OutputLevel = run.Output;
        NotifyValueChanged();
    }
}

/// <summary>
/// The MOS 6581 SID, the sound chip of the original Commodore 64: 12 V on VDD, 470 pF filter
/// capacitors as the C64 fits them, a filter whose cutoff curve is anything but straight, and a mixer
/// offset that makes the volume register a sample player. See <see cref="SidChip"/>.
/// </summary>
public sealed class Sid6581() : SidChip(SidModel.Mos6581)
{
    public override string PartNumber => "6581";
}

/// <summary>
/// The MOS 8580 SID of the C64C: 9 V on VDD, 22 nF filter capacitors, a filter close to linear, and
/// almost no mixer offset. See <see cref="SidChip"/>.
/// </summary>
public sealed class Sid8580() : SidChip(SidModel.Mos8580)
{
    public override string PartNumber => "8580";
}
