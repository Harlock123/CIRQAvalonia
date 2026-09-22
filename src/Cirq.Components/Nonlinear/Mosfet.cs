using System.Threading;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Nonlinear;

public enum MosfetChannel
{
    NChannel,
    PChannel,
}

/// <summary>Level-1 (square law) MOSFET parameters.</summary>
public sealed record MosfetModel(
    string Name,
    MosfetChannel Channel,
    double ThresholdVoltage,
    double TransconductanceParameter,
    double ChannelLengthModulation,
    bool HasBodyDiode = true,
    double ThresholdDriftPerKelvin = -2e-3,
    double MobilityExponent = -2.1)
{
    /// <summary>
    /// Threshold voltage at a temperature. It falls as the part warms, at a couple of millivolts a
    /// degree — the same figure as a diode's forward drop and for related reasons.
    /// <para>
    /// It is also why paralleled MOSFETs share current better than paralleled bipolars do. A
    /// bipolar that gets hot conducts <i>more</i> and gets hotter; a MOSFET that gets hot has a
    /// lower threshold but a much worse mobility, and the mobility wins — so it conducts less and
    /// pushes current to its neighbours.
    /// </para>
    /// </summary>
    public double ThresholdAt(double kelvin) =>
        ThresholdVoltage + (ThresholdDriftPerKelvin * (kelvin - JunctionTemperature.NominalKelvin));

    /// <summary>
    /// Transconductance parameter at a temperature. Carrier mobility falls as a power of the
    /// temperature, and that is the reason a power MOSFET's on-resistance climbs towards double
    /// between room temperature and a hot heatsink — which is what every derating curve on every
    /// power datasheet is about.
    /// <para>
    /// The exponent is fitted to those derating curves rather than taken from the physics. Silicon
    /// mobility alone goes as about T to the minus three halves, which on its own gives a ratio
    /// near 1.5 where the datasheets say 1.8 — because a real part's on-resistance is not only its
    /// channel, and the metallisation and bond wires warm up too. Since the parameter's job here
    /// is to reproduce the derating, it is fitted to it.
    /// </para>
    /// </summary>
    public double TransconductanceAt(double kelvin) =>
        TransconductanceParameter *
        Math.Pow(Math.Max(kelvin, 1.0) / JunctionTemperature.NominalKelvin, MobilityExponent);

    /// <summary>Small-signal logic-level N-channel, a few hundred milliamps.</summary>
    public static readonly MosfetModel N2N7000 = new("2N7000", MosfetChannel.NChannel, 2.0, 0.05, 0.02);

    /// <summary>Logic-level power N-channel.</summary>
    public static readonly MosfetModel IrlZ44N = new("IRLZ44N", MosfetChannel.NChannel, 1.8, 3.0, 0.01);

    /// <summary>Standard-gate power N-channel.</summary>
    public static readonly MosfetModel Irf540 = new("IRF540", MosfetChannel.NChannel, 3.5, 1.5, 0.01);

    public static readonly MosfetModel Bs250 = new("BS250", MosfetChannel.PChannel, 2.0, 0.04, 0.02);

    public static readonly MosfetModel Irf9540 = new("IRF9540", MosfetChannel.PChannel, 3.5, 0.8, 0.01);

    private static readonly IReadOnlyList<MosfetModel> BuiltIn =
        [N2N7000, IrlZ44N, Irf540, Bs250, Irf9540];

    /// <summary>Models brought in from SPICE cards, which shadow a built-in of the same name.</summary>
    private static readonly List<MosfetModel> ImportedModels = [];

    /// <summary>
    /// Guards the imported list. It is static, and a save now reads it for every model a circuit
    /// names — so an import from one place while a circuit is being written somewhere else would
    /// otherwise read a list mid-edit, which a plain list answers by corrupting itself rather
    /// than by complaining.
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>Every model the application knows, built in or imported.</summary>
    public static IReadOnlyList<MosfetModel> Library
    {
        get
        {
            lock (Gate)
            {
                if (ImportedModels.Count == 0) return BuiltIn;

                // An import shadows a built-in of the same name rather than replacing it, which is
                // what lets the import be removed again and the built-in come back. Somebody
                // importing a card called 1N4148 — much the likeliest name there is — should not be
                // able to delete the one that shipped.
                List<MosfetModel> library = [.. BuiltIn.Select(
                    m => ImportedModels.FirstOrDefault(
                        i => string.Equals(i.Name, m.Name, StringComparison.OrdinalIgnoreCase)) ?? m)];

                library.AddRange(ImportedModels.Where(
                    i => !BuiltIn.Any(m => string.Equals(m.Name, i.Name, StringComparison.OrdinalIgnoreCase))));

                return library;
            }
        }
    }

    /// <summary>Adds a model, replacing any with the same name.</summary>
    public static void Register(MosfetModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (Gate)
        {
            ImportedModels.RemoveAll(m => string.Equals(m.Name, model.Name, StringComparison.OrdinalIgnoreCase));
            ImportedModels.Add(model);
        }
    }

    /// <summary>
    /// Removes an imported model by name. Built-in models are not removable — an import of the
    /// same name was shadowing one, and taking the import away brings it back.
    /// </summary>
    public static bool Unregister(string name)
    {
        lock (Gate)
        {
            return ImportedModels.RemoveAll(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public override string ToString() => Name;
}

/// <summary>
/// Enhancement-mode MOSFET using the level-1 square law:
/// <code>
///   cutoff      Vgs &lt; Vt          Id = 0
///   triode      Vds &lt; Vgs − Vt    Id = K·((Vgs−Vt)·Vds − Vds²/2)·(1 + λ·Vds)
///   saturation  otherwise          Id = (K/2)·(Vgs−Vt)²·(1 + λ·Vds)
/// </code>
/// <para>
/// The channel is symmetric, so when Vds goes negative the drain and source swap roles and the
/// device conducts backwards. Without that a MOSFET could not be used as a low-side switch in a
/// bridge, which is most of what people reach for one to do.
/// </para>
/// <para>
/// The intrinsic body diode from source to drain is modelled too, so an inductive load clamps the
/// way real silicon does rather than producing an unbounded spike. The gate draws no current.
/// </para>
/// </summary>
public partial class Mosfet : CircuitComponent, ICurrentReporting, INoiseSource
{
    /// <summary>Saturation current of the body diode.</summary>
    private const double BodyDiodeSaturationCurrent = 1e-14;

    private double _bodyDiodeVoltage;
    private bool _limitedThisIteration;

    public Mosfet(MosfetModel? model = null)
    {
        Model = model ?? MosfetModel.N2N7000;

        Drain = new Terminal("d", "D", TerminalType.Passive, new Point(20, -40));
        Gate = new Terminal("g", "G", TerminalType.Input, new Point(-40, 0));
        Source = new Terminal("s", "S", TerminalType.Passive, new Point(20, 40));

        Terminals = [Drain, Gate, Source];
    }

    public Terminal Drain { get; }
    public Terminal Gate { get; }
    public Terminal Source { get; }

    [ObservableProperty]
    public partial MosfetModel Model { get; set; }

    public override string ComponentType =>
        Model.Channel == MosfetChannel.NChannel ? "N-MOSFET" : "P-MOSFET";

    public override string DesignatorPrefix => "M";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    public bool IsNChannel => Model.Channel == MosfetChannel.NChannel;

    /// <summary>Drain current at the converged solution, in amps.</summary>
    public double DrainCurrent { get; private set; }

    /// <summary>
    /// The channel's transconductance at the last solved point, in siemens. What the noise
    /// analysis measures the channel's thermal noise against.
    /// </summary>
    public double Transconductance { get; private set; }

    /// <summary>
    /// Where this part's flicker noise crosses its channel noise, in hertz. Below it the 1/f term
    /// dominates and rises without limit; above it there is nothing but the channel.
    /// <para>
    /// A hundred kilohertz is typical for a power MOSFET and high enough that flicker is most of
    /// the noise across the whole audio band. Set it to zero for a part where it does not matter,
    /// or where you would rather see the channel noise on its own.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double FlickerCornerHz { get; set; } = 1e5;

    /// <summary>Gate-source voltage in the device's own polarity convention.</summary>
    public double Vgs { get; private set; }

    /// <summary>Drain-source voltage in the device's own polarity convention.</summary>
    public double Vds { get; private set; }

    public bool IsConducting => Math.Abs(DrainCurrent) > 1e-6;

    /// <summary>True when the channel is in the resistive (triode) region.</summary>
    public bool IsInTriode { get; private set; }

    private double Polarity => IsNChannel ? 1.0 : -1.0;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var d = system.Node(Drain);
        var g = system.Node(Gate);
        var s = system.Node(Source);

        var polarity = Polarity;
        _limitedThisIteration = false;

        CacheForTemperature(state);

        var vd = system.IterationVoltage(d);
        var vg = system.IterationVoltage(g);
        var vs = system.IterationVoltage(s);

        // Work in the channel's own convention, where the drain is always the more positive end.
        var vdsNominal = polarity * (vd - vs);
        var reversed = vdsNominal < 0;

        var drainNode = reversed ? s : d;
        var sourceNode = reversed ? d : s;

        var vgs = reversed ? polarity * (vg - vd) : polarity * (vg - vs);
        var vds = Math.Abs(vdsNominal);

        var (id, gm, gds) = Evaluate(vgs, vds);

        // Kept for the noise analysis, which needs the slope the channel settled at rather than
        // anything it can work out from the terminal voltages.
        Transconductance = gm;

        // The equivalent current is mirrored back into the device's real polarity; the
        // conductances are polarity independent.
        var idEq = polarity * (id - gm * vgs - gds * vds);

        // Current leaves the effective drain node into the channel and returns at the source.
        system.Add(drainNode, g, gm);
        system.Add(drainNode, sourceNode, -(gm + gds));
        system.Add(drainNode, drainNode, gds);
        system.AddRhs(drainNode, -idEq);

        system.Add(sourceNode, g, -gm);
        system.Add(sourceNode, sourceNode, gm + gds);
        system.Add(sourceNode, drainNode, -gds);
        system.AddRhs(sourceNode, idEq);

        if (Model.HasBodyDiode) StampBodyDiode(system, state, d, s, polarity);

        // A gate with nothing attached would otherwise leave an empty matrix row.
        system.StampConductance(g, s, 1e-12);
    }

    /// <summary>
    /// Returns the drain current and its two conductances for a channel in its own convention,
    /// where <paramref name="vds"/> is non-negative.
    /// </summary>
    /// <summary>
    /// The two parameters that move with temperature, worked out once per solve rather than once
    /// per Newton iteration — they depend only on the circuit's temperature, and the inner loop
    /// runs a hundred times for every time point.
    /// </summary>
    private void CacheForTemperature(SimulationState state)
    {
        _threshold = Model.ThresholdAt(state.TemperatureKelvin);
        _transconductance = Model.TransconductanceAt(state.TemperatureKelvin);
    }

    private double _threshold;
    private double _transconductance;

    private (double Current, double Gm, double Gds) Evaluate(double vgs, double vds)
    {
        var overdrive = vgs - _threshold;
        var k = Math.Max(_transconductance, 1e-12);
        var lambda = Math.Max(Model.ChannelLengthModulation, 0);

        if (overdrive <= 0)
        {
            IsInTriode = false;
            return (0, 0, 0);
        }

        var modulation = 1.0 + lambda * vds;

        if (vds < overdrive)
        {
            // Triode: the channel behaves as a voltage-controlled resistor.
            IsInTriode = true;
            var core = overdrive * vds - 0.5 * vds * vds;
            var id = k * core * modulation;
            var gm = k * vds * modulation;
            var gds = k * ((overdrive - vds) * modulation + core * lambda);
            return (id, gm, gds);
        }

        // Saturation: current set by the gate, with channel-length modulation giving it a slope.
        IsInTriode = false;
        var square = overdrive * overdrive;
        var saturated = 0.5 * k * square * modulation;
        return (saturated, k * overdrive * modulation, 0.5 * k * square * lambda);
    }

    /// <summary>
    /// The intrinsic diode from source to drain on an N-channel part, and the reverse on a
    /// P-channel one.
    /// </summary>
    private void StampBodyDiode(MnaSystem system, SimulationState state, int d, int s, double polarity)
    {
        // Anode is the source for an N-channel device.
        var anode = polarity > 0 ? s : d;
        var cathode = polarity > 0 ? d : s;

        var vt = state.ThermalVoltage;
        var raw = system.IterationVoltageAcross(anode, cathode);
        var limited = Junction.Limit(raw, _bodyDiodeVoltage, vt,
            Junction.CriticalVoltage(BodyDiodeSaturationCurrent, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        _bodyDiodeVoltage = limited;

        var (current, conductance) = Junction.Evaluate(limited, BodyDiodeSaturationCurrent, vt);
        system.StampNorton(anode, cathode, conductance + 1e-12, current - conductance * limited);
    }

    /// <summary>
    /// Pure by design: the flag is reset at the top of each stamping pass, not here. The engine
    /// combines these with <c>All</c>, which short-circuits, so a convergence check that mutated
    /// state would run an unpredictable number of times.
    /// </summary>
    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var polarity = Polarity;

        CacheForTemperature(state);

        var vd = system.NodeVoltage(Drain);
        var vg = system.NodeVoltage(Gate);
        var vs = system.NodeVoltage(Source);

        Vgs = polarity * (vg - vs);
        Vds = polarity * (vd - vs);

        var reversed = Vds < 0;
        var channelVgs = reversed ? polarity * (vg - vd) : Vgs;
        var (id, _, _) = Evaluate(channelVgs, Math.Abs(Vds));

        DrainCurrent = polarity * (reversed ? -id : id);
    }

    /// <summary>An insulated gate draws nothing, so the channel current is all there is.</summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, Gate)) return 0;
        return ReferenceEquals(terminal, Drain) ? DrainCurrent : -DrainCurrent;
    }

    public override void ResetState()
    {
        _bodyDiodeVoltage = 0;
        _limitedThisIteration = false;
        DrainCurrent = 0;
        Vgs = 0;
        Vds = 0;
        IsInTriode = false;
    }

    partial void OnModelChanged(MosfetModel value) => NotifyValueChanged();

    /// <summary>
    /// Channel thermal noise: <c>4kT·(2/3)·gm</c>.
    /// <para>
    /// The channel is a resistor the gate is squeezing, so what comes out of it is ordinary
    /// thermal noise — but of a resistance that is none of the terminal resistances, which is why
    /// it is written against the transconductance instead. The 2/3 is the long-channel value; a
    /// short-channel part is worse, sometimes several times worse, and this does not model that.
    /// </para>
    /// <para>
    /// Plus flicker, which below the corner is the larger of the two and often by a long way. A
    /// MOSFET is the worst common device for it — surface conduction is what makes it so — and it
    /// is why a MOSFET front end is a poor choice for anything DC-coupled or audio, where a
    /// bipolar of the same transconductance would be quieter by an order of magnitude at ten
    /// hertz. The corner is a process parameter rather than anything derivable, so it is a
    /// property you can set.
    /// </para>
    /// </summary>
    public IEnumerable<NoiseEmission> NoiseSources(MnaSystem system, SimulationState state, double hertz)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(state);

        var density = NoisePhysics.Flicker(
            NoisePhysics.Channel(Transconductance, state.TemperatureKelvin),
            FlickerCornerHz, hertz);

        if (density <= 0) yield break;

        yield return new NoiseEmission(
            $"{Name} channel", system.Node(Drain), system.Node(Source), density);
    }
}
