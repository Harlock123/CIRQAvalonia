using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>What sets one analog temperature sensor apart from another: its transfer line and its supply.</summary>
/// <param name="Name">Part number.</param>
/// <param name="VoltsPerDegree">Slope of the output, in volts per degree Celsius.</param>
/// <param name="OffsetVolts">Output at 0 °C, in volts.</param>
/// <param name="MinimumSupply">Lowest supply it works from, in volts.</param>
/// <param name="MaximumSupply">Highest supply it is rated for, in volts.</param>
/// <param name="Headroom">How far below its supply the output can still reach, in volts.</param>
/// <param name="OutputResistance">Resistance behind the output, in ohms.</param>
/// <param name="QuiescentCurrent">What it draws from the supply, in amps.</param>
/// <param name="MinimumCelsius">Bottom of its rated range.</param>
/// <param name="MaximumCelsius">Top of its rated range.</param>
public sealed record TemperatureSensorModel(
    string Name,
    double VoltsPerDegree,
    double OffsetVolts,
    double MinimumSupply,
    double MaximumSupply,
    double Headroom,
    double OutputResistance,
    double QuiescentCurrent,
    double MinimumCelsius,
    double MaximumCelsius)
{
    /// <summary>
    /// LM35: 10 mV per degree from 0 V at 0 °C, on 4 to 30 V. On a single supply its output cannot
    /// go below ground, so below freezing it reads zero — the datasheet's negative-temperature
    /// circuit needs a pull-down to a negative rail, which this does not model.
    /// </summary>
    public static readonly TemperatureSensorModel Lm35 = new(
        "LM35", 0.010, 0.0, 4.0, 30.0, 1.5, 0.1, 60e-6, -55, 150);

    /// <summary>
    /// TMP36: 10 mV per degree with a 500 mV offset, so 750 mV at 25 °C and still positive at
    /// −40 °C — which is the whole reason for the offset. 2.7 to 5.5 V, so it runs off a 3.3 V board.
    /// </summary>
    public static readonly TemperatureSensorModel Tmp36 = new(
        "TMP36", 0.010, 0.5, 2.7, 5.5, 0.6, 10.0, 50e-6, -40, 125);

    /// <summary>Every analog temperature sensor the application knows.</summary>
    public static readonly IReadOnlyList<TemperatureSensorModel> Library = [Lm35, Tmp36];

    /// <summary>What the output reads at a temperature, before the supply has its say.</summary>
    public double OutputAt(double celsius) => OffsetVolts + (VoltsPerDegree * celsius);

    public override string ToString() => Name;
}

/// <summary>
/// An analog temperature sensor IC — an LM35 or a TMP36: three pins, and an output voltage that is
/// the temperature, ten millivolts a degree.
/// <para>
/// It sits between the thermistor and the DS18B20. The thermistor is a resistance you have to turn
/// into a temperature yourself, through a divider, a reference and a curve; the DS18B20 hands you
/// the number over a bus. This hands you a straight line: read the pin with an ADC, multiply by a
/// hundred, and that is degrees — subtracting fifty first on a TMP36, whose 500 mV offset is what
/// lets it report below freezing on a single supply.
/// </para>
/// <para>
/// The temperature at the sensor is its own setting, like the thermistor's and the DS18B20's, and
/// double-clicking it swaps to an alternate one — a hand round it. Set
/// <see cref="FollowsAmbient"/> and it reads the circuit's temperature instead, so a DC sweep over
/// temperature draws its transfer line. The output is a source behind a small resistance, held
/// below the supply by the part's headroom, and nothing at all when the supply is too low to run it.
/// </para>
/// </summary>
public partial class TemperatureSensorIc : CircuitComponent, IInteractiveComponent, ICurrentReporting
{
    private double _target;
    private double _previousTarget = double.NaN;

    public TemperatureSensorIc() : this(null)
    {
    }

    public TemperatureSensorIc(TemperatureSensorModel? model)
    {
        Model = model ?? TemperatureSensorModel.Lm35;

        Supply = new Terminal("vs", "+VS", TerminalType.Power, new Point(-70, -20));
        Ground = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-70, 20));
        Output = new Terminal("vout", "VOUT", TerminalType.Output, new Point(70, 0));

        Terminals = [Supply, Output, Ground];
    }

    public Terminal Supply { get; }

    public Terminal Output { get; }

    public Terminal Ground { get; }

    [ObservableProperty]
    public partial TemperatureSensorModel Model { get; set; }

    /// <summary>The temperature at the sensor, in degrees Celsius.</summary>
    [ObservableProperty]
    [Operable("Temperature", Minimum = -40, Maximum = 125, Unit = "°C")]
    public partial double Temperature { get; set; } = 25.0;

    /// <summary>What it swings to when you double-click it — a hand round the sensor.</summary>
    [ObservableProperty]
    public partial double AlternateTemperature { get; set; } = 36.0;

    /// <summary>
    /// Read the circuit's temperature (Simulate &gt; Conditions, or a DC sweep over temperature)
    /// rather than its own setting.
    /// </summary>
    [ObservableProperty]
    public partial bool FollowsAmbient { get; set; }

    public override string ComponentType => "Temperature Sensor";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => FollowsAmbient ? $"{Model.Name} · ambient" : $"{Model.Name} · {Temperature:0.#} °C";

    public override int VoltageSourceCount => 1;

    public override bool IsNonlinear => true;

    /// <summary>The temperature it read at the last solved point, in degrees Celsius.</summary>
    public double SensedCelsius { get; private set; } = 25.0;

    /// <summary>What it was putting out at the last solved point, in volts above its ground pin.</summary>
    public double OutputVoltage { get; private set; }

    /// <summary>True when the supply across it was enough to run it.</summary>
    public bool IsPowered { get; private set; }

    /// <summary>What is wrong with how it is being run, if anything.</summary>
    public IReadOnlyList<string> Violations { get; private set; } = [];

    public string InteractionHint => "Put a hand round it";

    public void Interact() => (Temperature, AlternateTemperature) = (AlternateTemperature, Temperature);

    private double Celsius(SimulationState state) =>
        FollowsAmbient ? state.TemperatureKelvin - 273.15 : Temperature;

    /// <summary>The output it would settle at with a given supply across it, in volts above ground.</summary>
    private double Target(double supply, double celsius)
    {
        if (supply < Model.MinimumSupply) return 0.0;

        var limited = Math.Clamp(celsius, Model.MinimumCelsius, Model.MaximumCelsius);

        // The output stage sources and cannot pull below its own ground, so the LM35 in its basic
        // circuit floors at zero; and it cannot rise to its supply.
        return Math.Clamp(Model.OutputAt(limited), 0.0, Math.Max(supply - Model.Headroom, 0.0));
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var vs = system.Node(Supply);
        var gnd = system.Node(Ground);
        var output = system.Node(Output);

        var supply = system.IterationVoltageAcross(vs, gnd);
        _target = Target(supply, Celsius(state));

        // A dead part holds its output through something high rather than driving it.
        var resistance = supply < Model.MinimumSupply ? 1e6 : Math.Max(Model.OutputResistance, 1e-3);
        system.StampTheveninSource(system.Branch(this, 0), output, gnd, _target, resistance);

        // The quiescent current, as the resistance that draws it from a 5 V supply.
        system.StampConductance(vs, gnd, Model.QuiescentCurrent / 5.0);
    }

    public override bool HasConverged(MnaSystem system, SimulationState state)
    {
        var settled = !double.IsNaN(_previousTarget) && Math.Abs(_previousTarget - _target) < 1e-6;
        _previousTarget = _target;
        return settled;
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var supply = system.NodeVoltage(Supply) - system.NodeVoltage(Ground);

        SensedCelsius = Celsius(state);
        OutputVoltage = system.NodeVoltage(Output) - system.NodeVoltage(Ground);
        IsPowered = supply >= Model.MinimumSupply;

        List<string> found = [];

        if (!IsPowered && supply > 0.1)
            found.Add($"its supply is {supply:0.0} V, below the {Model.MinimumSupply:0.0} V it needs to run");

        if (supply > Model.MaximumSupply + 0.05)
            found.Add($"its supply is {supply:0.0} V, over its {Model.MaximumSupply:0.#} V rating");

        if (SensedCelsius < Model.MinimumCelsius || SensedCelsius > Model.MaximumCelsius)
            found.Add($"{SensedCelsius:0} °C is outside its {Model.MinimumCelsius:0} to {Model.MaximumCelsius:0} °C range");

        Violations = found;
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var output = -system.BranchCurrent(this, 0);
        var quiescent = (system.NodeVoltage(Supply) - system.NodeVoltage(Ground)) * Model.QuiescentCurrent / 5.0;

        // What the output sources comes in through the supply pin, with the quiescent current, and
        // the quiescent current leaves through ground.
        if (ReferenceEquals(terminal, Output)) return -output;
        if (ReferenceEquals(terminal, Supply)) return output + quiescent;
        return -quiescent;
    }

    public override void ResetState()
    {
        _previousTarget = double.NaN;
        OutputVoltage = 0;
        IsPowered = false;
        Violations = [];
    }

    partial void OnModelChanged(TemperatureSensorModel value) => NotifyValueChanged();

    partial void OnTemperatureChanged(double value) => NotifyValueChanged();

    partial void OnFollowsAmbientChanged(bool value) => NotifyValueChanged();
}
