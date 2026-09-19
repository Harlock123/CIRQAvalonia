using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>A thermocouple alloy pair and what it produces per degree.</summary>
/// <param name="Name">The letter it is known by, and what it is made of.</param>
/// <param name="MicrovoltsPerDegree">Seebeck coefficient near room temperature.</param>
/// <param name="MaximumTemperature">Useful upper limit, in °C.</param>
public sealed record ThermocoupleType(string Name, double MicrovoltsPerDegree, double MaximumTemperature)
{
    public static readonly ThermocoupleType K = new("K (chromel/alumel)", 41.0, 1260);

    public static readonly ThermocoupleType J = new("J (iron/constantan)", 55.0, 760);

    public static readonly ThermocoupleType T = new("T (copper/constantan)", 43.0, 370);

    public static readonly ThermocoupleType E = new("E (chromel/constantan)", 68.0, 870);

    public static readonly IReadOnlyList<ThermocoupleType> Library = [K, J, T, E];

    public override string ToString() => Name;
}

/// <summary>
/// A thermocouple: two dissimilar metals joined, producing a few tens of microvolts per degree.
/// <para>
/// It is the canonical thing an instrumentation amplifier exists to read. Forty microvolts per
/// degree means a hot furnace and a warm hand differ by a couple of millivolts, which is why the
/// INA126 beside it in the palette has the gain it has.
/// </para>
/// <para>
/// The catch, and it is the whole subject: <b>a thermocouple measures a difference, not a
/// temperature.</b> The junction you care about produces an EMF, and so does every other junction
/// in the loop — including where the wires meet the copper of your circuit. What comes out is the
/// difference between the two ends, so without knowing how warm the cold end is you do not know
/// how hot the hot one is. That is what <i>cold junction compensation</i> means, and why the cold
/// junction temperature is a property here rather than an assumption.
/// </para>
/// <para>
/// The output is taken as linear in temperature, which real tables are not — a K type varies by
/// a few percent across its range. Near room temperature the error is small; near the top of the
/// range it is not, and a real instrument uses a polynomial.
/// </para>
/// </summary>
public partial class Thermocouple : TwoTerminalComponent, IInteractiveComponent
{
    public Thermocouple(ThermocoupleType? type = null) : base("+", "-")
    {
        Type = type ?? ThermocoupleType.K;
    }

    [ObservableProperty]
    public partial ThermocoupleType Type { get; set; }

    /// <summary>Temperature of the measuring junction, in °C.</summary>
    [ObservableProperty]
    public partial double Temperature { get; set; } = 25.0;

    /// <summary>
    /// Temperature where the wires meet the circuit, in °C. Everything the thermocouple says is
    /// relative to this, which is why it is here and not assumed.
    /// </summary>
    [ObservableProperty]
    public partial double ColdJunctionTemperature { get; set; } = 25.0;

    /// <summary>Temperature the junction goes to when you heat it by double-clicking.</summary>
    [ObservableProperty]
    public partial double HeatedTemperature { get; set; } = 300.0;

    /// <summary>Resistance of the wire, which matters because the signal is microvolts.</summary>
    [ObservableProperty]
    public partial double LeadResistance { get; set; } = 5.0;

    public override string ComponentType => "Thermocouple";

    public override string DesignatorPrefix => "TC";

    public override string ValueLabel => $"{Temperature:0.#} °C · {SiPrefix.Format(Emf, "V")}";

    public string InteractionHint =>
        Temperature > ColdJunctionTemperature + 1.0 ? "Let it cool" : "Heat the junction";

    public void Interact() =>
        Temperature = Temperature > ColdJunctionTemperature + 1.0
            ? ColdJunctionTemperature
            : HeatedTemperature;

    public override int VoltageSourceCount => 1;

    /// <summary>What the junction pair actually produces, in volts.</summary>
    public double Emf =>
        (Temperature - ColdJunctionTemperature) * Type.MicrovoltsPerDegree * 1e-6;

    /// <summary>
    /// What an instrument would read back if it assumed the cold end were at zero, which is the
    /// mistake cold junction compensation exists to prevent.
    /// </summary>
    public double UncompensatedTemperature =>
        Emf / (Type.MicrovoltsPerDegree * 1e-6);

    public IReadOnlyList<string> Violations => Temperature > Type.MaximumTemperature
        ? [$"{Temperature:0} °C is past the {Type.MaximumTemperature:0} °C limit for a " +
           $"{Type.Name} thermocouple — the alloys change above it and the reading drifts"]
        : [];

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampTheveninSource(
            system.Branch(this), system.Node(A), system.Node(B),
            Emf, Math.Max(LeadResistance, 1e-6));

    partial void OnTemperatureChanged(double value) => NotifyValueChanged();

    partial void OnTypeChanged(ThermocoupleType value) => NotifyValueChanged();
}

/// <summary>
/// A load cell: four strain gauges in a Wheatstone bridge, which is how nearly everything that
/// weighs things works.
/// <para>
/// Two of the gauges stretch under load and two compress, so the bridge goes out of balance by a
/// fraction of a percent. The bridge is stamped here as the four resistors it actually is, which
/// means the things that matter behave properly: it <b>needs exciting</b> — no voltage across the
/// bridge, no output, however much you load it — and the output is proportional to the excitation
/// rather than absolute, which is why load cells are rated in millivolts per volt.
/// </para>
/// <para>
/// Two millivolts per volt at full scale, with ten volts of excitation, is twenty millivolts for
/// the whole range of the thing. That is the other half of why the INA126 is in the palette.
/// </para>
/// </summary>
public partial class LoadCell : CircuitComponent, ICurrentReporting
{
    public LoadCell()
    {
        ExcitationPositive = new Terminal("e+", "E+", TerminalType.Passive, new Point(-40, -30));
        ExcitationNegative = new Terminal("e-", "E-", TerminalType.Passive, new Point(-40, 30));
        SignalPositive = new Terminal("s+", "S+", TerminalType.Output, new Point(40, -30));
        SignalNegative = new Terminal("s-", "S-", TerminalType.Output, new Point(40, 30));

        Terminals = [ExcitationPositive, SignalPositive, ExcitationNegative, SignalNegative];
    }

    public Terminal ExcitationPositive { get; }
    public Terminal ExcitationNegative { get; }
    public Terminal SignalPositive { get; }
    public Terminal SignalNegative { get; }

    /// <summary>Resistance of each arm of the bridge, in ohms. 350 is the usual figure.</summary>
    [ObservableProperty]
    public partial double BridgeResistance { get; set; } = 350.0;

    /// <summary>Output at full load, in millivolts per volt of excitation.</summary>
    [ObservableProperty]
    public partial double SensitivityMillivoltsPerVolt { get; set; } = 2.0;

    /// <summary>Load at which it gives its rated output, in kilograms.</summary>
    [ObservableProperty]
    public partial double CapacityKilograms { get; set; } = 5.0;

    /// <summary>What is sitting on it, in kilograms.</summary>
    [ObservableProperty]
    public partial double LoadKilograms { get; set; }

    public override string ComponentType => "Load Cell";

    public override string DesignatorPrefix => "LC";

    public override string ValueLabel => $"{LoadKilograms:0.##} kg";

    /// <summary>Voltage across the excitation pins at the last solved point.</summary>
    public double Excitation { get; private set; }

    /// <summary>Voltage between the signal pins at the last solved point.</summary>
    public double Output { get; private set; }

    /// <summary>Fractional imbalance of the bridge arms — a few thousandths at full load.</summary>
    public double Imbalance =>
        LoadKilograms / Math.Max(CapacityKilograms, 1e-9) * SensitivityMillivoltsPerVolt * 1e-3;

    public IReadOnlyList<string> Violations
    {
        get
        {
            if (Math.Abs(LoadKilograms) > CapacityKilograms * 1.5)
            {
                return [$"{LoadKilograms:0.#} kg on a {CapacityKilograms:0.#} kg cell — past about " +
                        "one and a half times rated the gauges stop coming back, and the zero " +
                        "moves permanently"];
            }

            // Excited by nothing, and so saying nothing, however much is on it.
            if (Math.Abs(Excitation) < 1e-3 && Math.Abs(LoadKilograms) > 0)
            {
                return ["the bridge has no excitation across E+ and E-, so it cannot produce an " +
                        "output whatever is on it"];
            }

            return [];
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var ep = system.Node(ExcitationPositive);
        var en = system.Node(ExcitationNegative);
        var sp = system.Node(SignalPositive);
        var sn = system.Node(SignalNegative);

        var r = Math.Max(BridgeResistance, 1e-3);
        var x = Math.Clamp(Imbalance, -0.5, 0.5);

        // Two arms stretch and two compress, which is what makes it a full bridge: the output is
        // four times what one gauge alone would give, and temperature affects all four equally.
        system.StampResistor(ep, sp, r * (1.0 - x));
        system.StampResistor(sp, en, r * (1.0 + x));
        system.StampResistor(ep, sn, r * (1.0 + x));
        system.StampResistor(sn, en, r * (1.0 - x));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Excitation = system.NodeVoltage(ExcitationPositive) - system.NodeVoltage(ExcitationNegative);
        Output = system.NodeVoltage(SignalPositive) - system.NodeVoltage(SignalNegative);
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var r = Math.Max(BridgeResistance, 1e-3);
        var x = Math.Clamp(Imbalance, -0.5, 0.5);

        double Through(Terminal from, Terminal to, double resistance) =>
            (system.NodeVoltage(from) - system.NodeVoltage(to)) / resistance;

        if (ReferenceEquals(terminal, ExcitationPositive))
            return Through(ExcitationPositive, SignalPositive, r * (1.0 - x))
                   + Through(ExcitationPositive, SignalNegative, r * (1.0 + x));

        if (ReferenceEquals(terminal, ExcitationNegative))
            return Through(ExcitationNegative, SignalPositive, r * (1.0 + x))
                   + Through(ExcitationNegative, SignalNegative, r * (1.0 - x));

        if (ReferenceEquals(terminal, SignalPositive))
            return Through(SignalPositive, ExcitationPositive, r * (1.0 - x))
                   + Through(SignalPositive, ExcitationNegative, r * (1.0 + x));

        return Through(SignalNegative, ExcitationPositive, r * (1.0 + x))
               + Through(SignalNegative, ExcitationNegative, r * (1.0 - x));
    }

    public override void ResetState()
    {
        Excitation = 0;
        Output = 0;
    }

    partial void OnLoadKilogramsChanged(double value) => NotifyValueChanged();
}
