using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>What a behavioural source puts out.</summary>
public enum BehaviouralOutput
{
    /// <summary>Volts across its output pins, whatever the load does.</summary>
    Volts,

    /// <summary>Amps out of its positive pin, whatever the voltage does.</summary>
    Amps,
}

/// <summary>
/// A source whose output is an expression — SPICE's <c>B</c> element, and the part that turns the
/// library from a fixed set of devices into something you can write behaviour into.
/// <para>
/// Everything else in Sources is linear. A VCVS is <c>gain × v</c> and nothing else, so anything
/// with a curve to it — a thermistor's law, a sensor's transfer characteristic, a multiplier, an
/// automatic gain control, a stage that saturates — has to be written in C# and built into the
/// application. This one is written in the box: <c>= 2.5 + 0.8 * tanh(a / 0.5)</c>.
/// </para>
/// <para>
/// The variables are the three input pins <c>a</c>, <c>b</c> and <c>c</c> — each the voltage on
/// that pin with respect to ground — and <c>t</c>, the time in seconds. The last of those makes it
/// an arbitrary waveform generator as well: <c>= 5 * sin(2 * pi * 1000 * t) * exp(-t / 0.01)</c> is
/// a decaying tone that no combination of the stock sources produces.
/// </para>
/// <para>
/// The notation is the one the scope and the parameters already use, deliberately: a second dialect
/// of the same arithmetic is how an application ends up with two parsers that disagree about what
/// <c>-2^2</c> means.
/// </para>
/// <para>
/// It is solved as a non-linear device, by linearising: the expression is worked out at the current
/// iterate, its slope with respect to each input is measured by nudging that input a little either
/// way, and the source is stamped as that value plus those slopes. Newton then does what it does
/// for a diode. A formula that mentions no input pin is linear, and is stamped as a plain source —
/// so an arbitrary waveform costs nothing extra to solve.
/// </para>
/// </summary>
public partial class BehaviouralSource : CircuitComponent
{
    /// <summary>
    /// The names an expression may use: the three input pins, the time, and the constants every
    /// other expression in the application already knows — pi is not something anybody should have
    /// to type out.
    /// </summary>
    public static IReadOnlyList<string> Variables { get; } =
        [.. new[] { "a", "b", "c", "t" }.Concat(CircuitParameters.Constants.Keys)];

    private readonly Dictionary<string, double> _values =
        new(CircuitParameters.Constants, StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = 0, ["b"] = 0, ["c"] = 0, ["t"] = 0,
        };

    private TraceExpression.Compiled? _compiled;
    private string? _compiledFrom;
    private string? _problem;
    private double _value;
    private double _lastStamped = double.NaN;
    private readonly double[] _slopes = new double[3];

    public BehaviouralSource(string expression = "= a * a")
    {
        Expression = expression;

        OutputPositive = new Terminal("out+", "+", TerminalType.Output, new Point(40, -20));
        OutputNegative = new Terminal("out-", "−", TerminalType.Output, new Point(40, 20));
        A = new Terminal("a", "a", TerminalType.Input, new Point(-40, -24));
        B = new Terminal("b", "b", TerminalType.Input, new Point(-40, 0));
        C = new Terminal("c", "c", TerminalType.Input, new Point(-40, 24));

        Terminals = [OutputPositive, OutputNegative, A, B, C];
    }

    public Terminal OutputPositive { get; }

    public Terminal OutputNegative { get; }

    /// <summary>First input. Its voltage to ground is <c>a</c> in the expression.</summary>
    public Terminal A { get; }

    public Terminal B { get; }

    public Terminal C { get; }

    /// <summary>
    /// The formula. A leading <c>=</c> is allowed and ignored, because that is how a value is
    /// bound to an expression everywhere else in the application and the habit travels.
    /// </summary>
    [ObservableProperty]
    public partial string Expression { get; set; }

    /// <summary>Volts or amps.</summary>
    [ObservableProperty]
    public partial BehaviouralOutput Output { get; set; } = BehaviouralOutput.Volts;

    /// <summary>
    /// How far each input is nudged to measure the slope, in volts.
    /// <para>
    /// A numerical derivative is a balance: too small and it is the difference of two nearly equal
    /// numbers, which is noise; too large and it is the slope of a chord rather than a tangent, and
    /// Newton converges slowly or not at all. A millivolt suits the range real signals live in.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double SlopeStep { get; set; } = 1e-3;

    public override string ComponentType => "Behavioural Source";

    public override string DesignatorPrefix => "B";

    public override string ValueLabel => Text.Length == 0 ? "= ?" : $"= {Text}";

    public override int VoltageSourceCount => Output == BehaviouralOutput.Volts ? 1 : 0;

    /// <summary>
    /// Non-linear only when the formula actually depends on a pin. A waveform written in <c>t</c>
    /// alone is a source like any other, and making the whole circuit iterate for it would be a
    /// cost for nothing.
    /// </summary>
    public override bool IsNonlinear => Compiled() is not null && DependsOnInputs;

    /// <summary>What the source is putting out at the last solved point, in volts or amps.</summary>
    public double Value => _value;

    /// <summary>What is wrong with the formula, in the words the scope's expression box uses.</summary>
    public string? Problem
    {
        get
        {
            Compiled();
            return _problem;
        }
    }

    public IReadOnlyList<string> Violations => Problem is { } problem
        ? [$"the formula cannot be worked out: {problem}. The source is putting out nothing until " +
           "it can be — it names a, b, c (the input pins) and t (seconds)"]
        : [];

    private string Text => Expression.TrimStart().TrimStart('=').Trim();

    /// <summary>
    /// True when the formula mentions a pin. Time and the constants do not count: a waveform
    /// written in <c>t</c> is a source, not a non-linear device.
    /// </summary>
    private bool DependsOnInputs =>
        _compiled is not null && _compiled.Names.Any(
            n => n.Length == 1 && char.ToLowerInvariant(n[0]) is 'a' or 'b' or 'c');

    /// <summary>
    /// The parsed formula, parsed again only when the text has changed. A solver asks for this
    /// several times per iteration.
    /// </summary>
    private TraceExpression.Compiled? Compiled()
    {
        if (_compiledFrom == Expression) return _compiled;

        _compiledFrom = Expression;

        try
        {
            _compiled = TraceExpression.Compile(Text, Variables, "input");
            _problem = null;
        }
        catch (ExpressionException ex)
        {
            _compiled = null;
            _problem = ex.Message;
        }

        return _compiled;
    }

    private double Evaluate(TraceExpression.Compiled compiled, double a, double b, double c, double time)
    {
        _values["a"] = a;
        _values["b"] = b;
        _values["c"] = c;
        _values["t"] = time;

        var result = compiled.Evaluate(_values);

        // A formula can produce a number that is not one — a division by zero, a log of something
        // negative. Stamping it would put a NaN into the matrix and lose the whole circuit rather
        // than this one part, so it comes out as nothing at all.
        return double.IsFinite(result) ? result : 0.0;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(state);

        var ground = Netlist.GroundIndex;
        var plus = system.Node(OutputPositive);
        var minus = system.Node(OutputNegative);

        // The pins draw nothing, but a pin joined to nothing at all would leave its node out of the
        // matrix; a very large resistance to ground keeps it solvable without changing anything.
        system.StampResistor(system.Node(A), ground, 1e12);
        system.StampResistor(system.Node(B), ground, 1e12);
        system.StampResistor(system.Node(C), ground, 1e12);

        if (Compiled() is not { } compiled)
        {
            // A formula that does not parse produces nothing rather than an arbitrary number: an
            // open circuit for a current source, and zero volts for a voltage one.
            if (Output == BehaviouralOutput.Volts)
                system.StampVoltageSource(system.Branch(this), plus, minus, 0);

            _value = 0;
            return;
        }

        var inputs = new[] { system.Node(A), system.Node(B), system.Node(C) };

        var a = system.IterationVoltage(inputs[0]);
        var b = system.IterationVoltage(inputs[1]);
        var c = system.IterationVoltage(inputs[2]);

        var here = Evaluate(compiled, a, b, c, state.Time);

        _value = here;

        // The slope in each input, measured either side of where the iterate is. Central rather
        // than forward: the error in a central difference goes as the step squared, and the extra
        // evaluation is one call to an expression a dozen characters long.
        var h = Math.Max(Math.Abs(SlopeStep), 1e-9);

        if (DependsOnInputs)
        {
            _slopes[0] = (Evaluate(compiled, a + h, b, c, state.Time)
                          - Evaluate(compiled, a - h, b, c, state.Time)) / (2 * h);
            _slopes[1] = (Evaluate(compiled, a, b + h, c, state.Time)
                          - Evaluate(compiled, a, b - h, c, state.Time)) / (2 * h);
            _slopes[2] = (Evaluate(compiled, a, b, c + h, state.Time)
                          - Evaluate(compiled, a, b, c - h, state.Time)) / (2 * h);
        }
        else
        {
            Array.Clear(_slopes);
        }

        // Linearised: f ≈ f(here) + Σ slope·(v − v_here). The constant is what is left when the
        // slope terms are moved to the matrix.
        var constant = here
                       - (_slopes[0] * a)
                       - (_slopes[1] * b)
                       - (_slopes[2] * c);

        if (Output == BehaviouralOutput.Volts)
        {
            var branch = system.Branch(this);

            system.StampVoltageSource(branch, plus, minus, constant);

            for (var i = 0; i < inputs.Length; i++)
            {
                if (_slopes[i] == 0) continue;

                // The row reads v+ − v− − Σ slope·v_in = constant.
                system.Add(branch, inputs[i], -_slopes[i]);
            }
        }
        else
        {
            // Out of the positive pin and into the circuit, which is what a source does and the
            // opposite of the direction these two stamps are written in: both say "through the
            // element, from the first node to the second".
            system.StampCurrentSource(minus, plus, constant);

            for (var i = 0; i < inputs.Length; i++)
            {
                if (_slopes[i] == 0) continue;

                system.StampVccs(minus, plus, inputs[i], ground, _slopes[i]);
            }
        }
    }

    /// <summary>
    /// Small-signal, for a frequency response: the slopes measured at the bias point are the gains.
    /// That is exactly what a Bode plot of a non-linear stage means — what it does to a small
    /// wiggle about where it is sitting.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(system);

        var plus = system.Node(OutputPositive);
        var minus = system.Node(OutputNegative);
        var inputs = new[] { system.Node(A), system.Node(B), system.Node(C) };

        if (Output == BehaviouralOutput.Volts)
        {
            var branch = system.Branch(this);

            system.Add(plus, branch, 1.0);
            system.Add(minus, branch, -1.0);
            system.Add(branch, plus, 1.0);
            system.Add(branch, minus, -1.0);

            for (var i = 0; i < inputs.Length; i++)
                if (_slopes[i] != 0)
                    system.Add(branch, inputs[i], -_slopes[i]);

            return;
        }

        for (var i = 0; i < inputs.Length; i++)
        {
            if (_slopes[i] == 0) continue;

            system.Add(plus, inputs[i], -_slopes[i]);
            system.Add(minus, inputs[i], _slopes[i]);
        }
    }

    /// <summary>
    /// Converged when the value it is asking for has stopped moving. The solver's own test is on
    /// the node voltages, and a source whose formula is steep can satisfy that while still being
    /// several iterations away from consistent with itself.
    /// </summary>
    public override bool HasConverged(MnaSystem system, SimulationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!IsNonlinear) return true;

        var moved = Math.Abs(_value - _lastStamped);

        _lastStamped = _value;

        if (double.IsNaN(moved)) return false;

        var tolerance = Output == BehaviouralOutput.Volts
            ? state.Settings.VoltageTolerance
            : state.Settings.CurrentTolerance;

        return moved <= tolerance + (state.Settings.RelativeTolerance * Math.Abs(_value));
    }

    public override void ResetState()
    {
        _value = 0;
        _lastStamped = double.NaN;
        Array.Clear(_slopes);
    }

    partial void OnExpressionChanged(string value) => NotifyValueChanged();

    partial void OnOutputChanged(BehaviouralOutput value) => NotifyValueChanged();
}
