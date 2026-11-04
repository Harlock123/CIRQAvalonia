using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// An incandescent lamp: a tungsten filament whose resistance climbs steeply as it heats.
/// <para>
/// The number on the bulb is its <i>hot</i> resistance in disguise — a 12 V 5 W lamp is 28.8 Ω at
/// full brightness, which is <c>V²/P</c>. Measure the same lamp with a meter and it reads about a
/// tenth of that, because tungsten's resistance rises roughly in proportion to its absolute
/// temperature and the filament runs at some 2700 K. Switch it on and for the first few
/// milliseconds it draws ten times its rated current. That inrush is what blows the fuse, welds
/// the relay contact and makes a lamp that has been fine for years fail at the moment it is switched
/// on.
/// </para>
/// <para>
/// The filament is a heat balance: electrical power in, radiation out (as <c>T⁴</c>, which is what
/// it is), and a heat capacity between them set by <see cref="ThermalTimeConstant"/>. The
/// resistance follows the temperature as <c>R = R_hot·(T/T_hot)^α</c>, with α chosen so that the
/// lamp at room temperature reads <see cref="ColdResistanceRatio"/> of its hot resistance. In a
/// transient the filament warms with the time constant, so the inrush is in the current trace; at
/// an operating point it sits wherever its own dissipation puts it — except under initial
/// conditions, where the supply has only just arrived and the filament is still cold.
/// </para>
/// <para>
/// Light comes out as roughly the 3.4th power of the voltage — run one at half voltage and it gives
/// a tenth of the light and glows orange — and the symbol shows that, dull red through to white.
/// </para>
/// </summary>
public partial class IncandescentLamp : TwoTerminalComponent, ICurrentReporting
{
    /// <summary>Filament temperature at the rated voltage, in kelvin. Typical of a small lamp.</summary>
    public const double RatedFilamentKelvin = 2700.0;

    /// <summary>The temperature the cold resistance is quoted at, in kelvin.</summary>
    private const double ReferenceKelvin = 300.0;

    /// <summary>
    /// Light output against filament temperature. Lumens go as about V^3.4 and the temperature as
    /// V^0.4 for a tungsten filament, so light goes as about the eighth-and-a-half power of the
    /// temperature: a filament at two thirds of its rated temperature gives a thirtieth of the light.
    /// </summary>
    private const double LightExponent = 8.5;

    private double _kelvin = double.NaN;
    private double _resistance;

    public IncandescentLamp(double ratedVoltage = 12.0, double ratedPower = 5.0)
    {
        RatedVoltage = ratedVoltage;
        RatedPower = ratedPower;
    }

    /// <summary>The voltage it is made for, in volts — the first number printed on the bulb.</summary>
    [ObservableProperty]
    public partial double RatedVoltage { get; set; }

    /// <summary>What it dissipates at that voltage, in watts — the second number.</summary>
    [ObservableProperty]
    public partial double RatedPower { get; set; }

    /// <summary>
    /// Cold resistance as a fraction of hot. About a tenth for a small tungsten lamp; a mains
    /// lamp, with a hotter filament, nearer a fifteenth.
    /// </summary>
    [ObservableProperty]
    public partial double ColdResistanceRatio { get; set; } = 0.1;

    /// <summary>
    /// How long the filament takes to follow a change in power near full brightness, in seconds.
    /// Tens of milliseconds for a small lamp, which is why a lamp on 50 Hz AC does not visibly
    /// flicker and one on a 1 Hz square wave does.
    /// </summary>
    [ObservableProperty]
    public partial double ThermalTimeConstant { get; set; } = 0.05;

    public override string ComponentType => "Incandescent Lamp";

    public override string DesignatorPrefix => "LA";

    public override string ValueLabel => $"{RatedVoltage:0.##}V {RatedPower:0.##}W";

    /// <summary>Resistance at full brightness, in ohms: V²/P.</summary>
    public double HotResistance => RatedVoltage * RatedVoltage / Math.Max(RatedPower, 1e-9);

    /// <summary>Resistance at room temperature, in ohms — what a meter reads on a lamp that is off.</summary>
    public double ColdResistance => HotResistance * Math.Clamp(ColdResistanceRatio, 1e-3, 1.0);

    /// <summary>Filament temperature at the last solved point, in kelvin.</summary>
    public double FilamentKelvin => double.IsNaN(_kelvin) ? ReferenceKelvin : _kelvin;

    /// <summary>Resistance at the last solved point, in ohms.</summary>
    public double Resistance => _resistance > 0 ? _resistance : ResistanceAt(FilamentKelvin);

    /// <summary>Current through the filament at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>What the filament was dissipating at the last solved point, in watts.</summary>
    public double Power { get; private set; }

    /// <summary>
    /// Light output as a fraction of what it gives at its rating — 1 at the rated voltage, above it
    /// when over-run.
    /// </summary>
    public double LightOutput => Math.Pow(FilamentKelvin / RatedFilamentKelvin, LightExponent);

    /// <summary>How bright the symbol is drawn, 0 to 1 — the perceived, square-root response.</summary>
    public double Brightness => Math.Sqrt(Math.Clamp(LightOutput, 0, 1));

    /// <summary>What is wrong with how it is being run, if anything.</summary>
    public IReadOnlyList<string> Violations =>
        Power > RatedPower * 1.44 + 1e-9
            ?
            [
                $"dissipating {SiPrefix.Format(Power, "W")} against a {SiPrefix.Format(RatedPower, "W")} " +
                "rating — over-run like this, a filament's life is measured in minutes",
            ]
            : [];

    /// <summary>
    /// The exponent in <c>R = R_hot·(T/T_hot)^α</c>, chosen so the room-temperature resistance is
    /// the stated fraction of the hot one.
    /// </summary>
    private double Alpha =>
        Math.Log(Math.Clamp(ColdResistanceRatio, 1e-3, 1.0)) / Math.Log(ReferenceKelvin / RatedFilamentKelvin);

    /// <summary>Filament resistance at a temperature, in ohms.</summary>
    public double ResistanceAt(double kelvin) =>
        HotResistance * Math.Pow(Math.Max(kelvin, 1.0) / RatedFilamentKelvin, Alpha);

    /// <summary>
    /// Radiation constant: what makes the filament shed exactly its rated power at its rated
    /// temperature in a room at the reference temperature.
    /// </summary>
    private double Radiation => RatedPower / (Math.Pow(RatedFilamentKelvin, 4) - Math.Pow(ReferenceKelvin, 4));

    /// <summary>Heat capacity, in joules per kelvin, from the time constant at full brightness.</summary>
    private double HeatCapacity => Math.Max(ThermalTimeConstant, 1e-6) * 4.0 * Radiation * Math.Pow(RatedFilamentKelvin, 3);

    private double Radiated(double kelvin, double ambient) =>
        Radiation * (Math.Pow(kelvin, 4) - Math.Pow(ambient, 4));

    public override bool IsNonlinear => true;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var ambient = state.TemperatureKelvin;

        if (double.IsNaN(_kelvin)) _kelvin = ambient;

        if (!state.IsTransient)
        {
            // At an operating point the filament is wherever its own dissipation puts it, found
            // from the voltage of the iterate being solved; the Newton loop closes on both at
            // once. Under initial conditions it is the instant of switch-on, and it is cold.
            _kelvin = state.Settings.UseInitialConditions
                ? ambient
                : Steady(system.IterationVoltageAcross(system.Node(A), system.Node(B)), ambient);
        }

        _resistance = ResistanceAt(_kelvin);
        system.StampResistor(system.Node(A), system.Node(B), _resistance);
    }

    private double _convergedAt = double.NaN;

    public override bool HasConverged(MnaSystem system, SimulationState state)
    {
        if (state.IsTransient) return true;

        // The temperature has to have stopped moving, not only the voltage: the first iterate of a
        // cold filament can look settled while its resistance is still a tenth of what it will be.
        var settled = !double.IsNaN(_convergedAt) && Math.Abs(_convergedAt - _kelvin) < 0.5;
        _convergedAt = _kelvin;
        return settled;
    }

    /// <summary>
    /// The temperature at which the filament radiates exactly what a voltage puts into it. Found by
    /// bisection, because the balance is monotonic and a bisection cannot overshoot.
    /// </summary>
    private double Steady(double volts, double ambient)
    {
        var low = ambient;
        var high = RatedFilamentKelvin * 3.0;

        for (var i = 0; i < 60; i++)
        {
            var mid = 0.5 * (low + high);
            var surplus = (volts * volts / ResistanceAt(mid)) - Radiated(mid, ambient);

            if (surplus > 0) low = mid;
            else high = mid;
        }

        return 0.5 * (low + high);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var volts = VoltageAcross(system, A, B);
        var ambient = state.TemperatureKelvin;

        // The current that actually flowed this step is the one through the resistance it was
        // solved with — which in the first step of a cold lamp is the inrush.
        Current = volts / Math.Max(_resistance, 1e-9);
        Power = volts * Current;

        if (!state.IsTransient || state.TimeStep <= 0) return;

        // Backward Euler on the heat balance, solved by Newton for the new temperature, so a step
        // longer than the time constant settles rather than oscillates.
        var capacity = HeatCapacity / state.TimeStep;
        var t = _kelvin;

        for (var i = 0; i < 20; i++)
        {
            var r = ResistanceAt(t);
            var f = (capacity * (t - _kelvin)) - (volts * volts / r) + Radiated(t, ambient);
            var df = capacity + (volts * volts * Alpha / (r * t)) + (4.0 * Radiation * t * t * t);
            var next = Math.Max(t - (f / df), 1.0);

            if (Math.Abs(next - t) < 1e-6) { t = next; break; }
            t = next;
        }

        _kelvin = t;
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    public override void ResetState()
    {
        _kelvin = double.NaN;
        _resistance = 0;
        _convergedAt = double.NaN;
        Current = 0;
        Power = 0;
    }

    partial void OnRatedVoltageChanged(double value) => NotifyValueChanged();

    partial void OnRatedPowerChanged(double value) => NotifyValueChanged();
}
