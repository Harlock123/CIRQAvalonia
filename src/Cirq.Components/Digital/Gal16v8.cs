using System.Globalization;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A GAL16V8 programmable logic device, programmed with equations rather than a JEDEC fuse map: eight outputs (pins
/// 12-19), each the sum of products of the inputs (pins 1-9 and 11) and the outputs fed back, combinational or
/// registered.
/// <para>
/// <see cref="Equations"/> is a line each, in the PALASM and galasm way: <c>pin 2 = A</c> names a pin (otherwise it's
/// P2); <c>Y = A &amp; B | !C</c> is a combinational output (!, &amp;, | and ^, or /, * and +, with parentheses);
/// <c>Q.R = Q ^ T</c> a registered one, a D flip-flop loaded on CLK's rising edge, which puts the part in its registered
/// mode, pin 1 becoming CLK and pin 11 /OE for the registers; <c>Y.OE = EN</c> gives an output its own three-state
/// enable. An output name in an expression is its pin (for a registered output, its flip-flop). A <c>;</c> starts a
/// comment. Each output has eight product terms on the chip (seven with .OE), which is checked as the equation is written,
/// before it's multiplied out.
/// </para>
/// </summary>
public sealed partial class Gal16v8 : DigitalIc
{
    private abstract record Node;
    private sealed record Leaf(int Pin) : Node;
    private sealed record Const(bool Value) : Node;
    private sealed record Not(Node Inner) : Node;
    private sealed record Op(char Kind, Node Left, Node Right) : Node;

    private readonly Terminal[] _pins = new Terminal[21];
    private readonly Node?[] _logic = new Node?[21];
    private readonly Node?[] _enable = new Node?[21];
    private readonly bool[] _registered = new bool[21];
    private readonly bool[] _q = new bool[21];
    private bool _lastClock;
    private bool _registeredMode;
    private static readonly int[] OutputPins = [12, 13, 14, 15, 16, 17, 18, 19];

    public Gal16v8() : base(20)
    {
        PropagationDelay = 15e-9;
        var pins = new Terminal[21];
        for (var n = 1; n <= 20; n++)
        {
            var type = n == 10 ? TerminalType.Ground : n == 20 ? TerminalType.Power : n >= 12 ? TerminalType.Bidirectional : TerminalType.Input;
            pins[n] = _pins[n] = new Terminal($"p{n}", n == 10 ? "GND" : n == 20 ? "VCC" : $"P{n}", type, DipPackage.PinOffset(n, 20));
        }

        Gnd = pins[10];
        Vcc = pins[20];
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. Enumerable.Range(1, 19).Where(n => n != 10).Select(n => pins[n])], [.. OutputPins.Select(n => pins[n])]);
        Compile();
    }

    /// <summary>The program, as equations.</summary>
    [ObservableProperty]
    public partial string Equations { get; set; } = "; A two-input AND and an XOR\npin 2 = A\npin 3 = B\npin 19 = AND\npin 18 = XOR\nAND = A & B\nXOR = A ^ B";

    /// <summary>What's wrong with the equations, or null.</summary>
    public string? EquationError { get; private set; }

    public bool RegisteredMode => _registeredMode;

    public override string PartNumber => "GAL16V8";

    public override bool LabelsPins => true;

    private readonly Dictionary<int, string> _names = [];

    public override string PinLabel(Terminal pin)
    {
        var n = Array.IndexOf(_pins, pin);
        if (_registeredMode && n == 1) return "CLK";
        if (_registeredMode && n == 11) return "/OE";
        return n > 0 && _names.TryGetValue(n, out var name) ? name : pin.Name;
    }

    public override string ValueLabel => EquationError is not null ? "bad equations" : _registeredMode ? "GAL16V8 registered" : "GAL16V8";

    partial void OnEquationsChanged(string value) => Compile();

    private void Compile()
    {
        Array.Clear(_logic);
        Array.Clear(_enable);
        Array.Clear(_registered);
        _names.Clear();
        EquationError = null;
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; n <= 20; n++) byName[$"P{n}"] = n;
        var lines = Equations.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(l => (l.Contains(';', StringComparison.Ordinal) ? l[..l.IndexOf(';', StringComparison.Ordinal)] : l).Trim())
            .Where(l => l.Length > 0).ToList();
        try
        {
            foreach (var line in lines.Where(l => l.StartsWith("pin ", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = line[4..].Split('=', 2);
                if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 20 or 10 or 20)
                    throw new FormatException($"\"{line}\" isn't \"pin n = NAME\" with n a signal pin.");
                var name = parts[1].Trim();
                byName[name] = n;
                _names[n] = name;
            }

            foreach (var line in lines.Where(l => !l.StartsWith("pin ", StringComparison.OrdinalIgnoreCase)))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0) throw new FormatException($"\"{line}\" has no =.");
                var target = line[..eq].Trim();
                var suffix = "";
                var dot = target.LastIndexOf('.');
                if (dot > 0) (suffix, target) = (target[(dot + 1)..].ToUpperInvariant(), target[..dot]);
                if (!byName.TryGetValue(target, out var pin) || !OutputPins.Contains(pin))
                    throw new FormatException($"{target} isn't one of the outputs, pins 12 to 19.");
                var tree = Parse(line[(eq + 1)..], byName);
                var terms = Terms(tree);
                if (suffix == "OE") _enable[pin] = tree;
                else
                {
                    _logic[pin] = tree;
                    _registered[pin] = suffix == "R";
                    if (suffix is not ("" or "R")) throw new FormatException($".{suffix} isn't .R or .OE.");
                }

                var limit = _enable[pin] is null ? 8 : 7;
                if (suffix != "OE" && terms > limit) throw new FormatException($"{target} has {terms} product terms; the GAL16V8 gives an output {limit}.");
            }
        }
        catch (FormatException e)
        {
            EquationError = e.Message;
            Array.Clear(_logic);
        }

        _registeredMode = _registered.Any(r => r);
        NotifyValueChanged();
    }

    private static int Terms(Node node) => node is Op { Kind: '|' } or ? Terms(or.Left) + Terms(or.Right) : 1;

    private static Node Parse(string text, Dictionary<string, int> names)
    {
        var tokens = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if ("!/&*|+^()#".Contains(c, StringComparison.Ordinal)) { tokens.Add(c.ToString()); i++; continue; }
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (i == start) throw new FormatException($"Unexpected '{c}'.");
            tokens.Add(text[start..i]);
        }

        var pos = 0;
        string? Peek() => pos < tokens.Count ? tokens[pos] : null;
        Node Or()
        {
            var left = Xor();
            while (Peek() is "|" or "+" or "#") { pos++; left = new Op('|', left, Xor()); }
            return left;
        }

        Node Xor()
        {
            var left = And();
            while (Peek() == "^") { pos++; left = new Op('^', left, And()); }
            return left;
        }

        Node And()
        {
            var left = Unary();
            while (Peek() is "&" or "*") { pos++; left = new Op('&', left, Unary()); }
            return left;
        }

        Node Unary()
        {
            var t = Peek() ?? throw new FormatException("An expression ends too soon.");
            pos++;
            switch (t)
            {
                case "!" or "/": return new Not(Unary());
                case "(":
                    var inner = Or();
                    if (Peek() != ")") throw new FormatException("A ( isn't closed.");
                    pos++;
                    return inner;
                case "1" or "VCC": return new Const(true);
                case "0" or "GND": return new Const(false);
            }

            return names.TryGetValue(t, out var pin) ? new Leaf(pin) : throw new FormatException($"{t} isn't a pin name.");
        }

        var tree = Or();
        if (pos != tokens.Count) throw new FormatException($"Unexpected {tokens[pos]}.");
        return tree;
    }

    private bool Eval(Node node, Func<int, bool> pin) => node switch
    {
        Leaf l => _registered[l.Pin] ? _q[l.Pin] : pin(l.Pin),
        Const c => c.Value,
        Not n => !Eval(n.Inner, pin),
        Op { Kind: '&' } o => Eval(o.Left, pin) && Eval(o.Right, pin),
        Op { Kind: '|' } o => Eval(o.Left, pin) || Eval(o.Right, pin),
        Op o => Eval(o.Left, pin) ^ Eval(o.Right, pin),
        _ => false,
    };

    /// <summary>A registered output's flip-flop.</summary>
    public bool Register(int pin) => _q[pin];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        bool Pin(int n) => context.ReadInput(_pins[n], Levels).IsHigh();
        var clock = Pin(1);
        if (_registeredMode && clock && !_lastClock && !context.IsInitializing)
        {
            var next = new bool[21];
            foreach (var p in OutputPins)
                if (_registered[p] && _logic[p] is { } tree) next[p] = Eval(tree, Pin);
            foreach (var p in OutputPins)
                if (_registered[p]) _q[p] = next[p];
            NotifyValueChanged();
        }

        _lastClock = clock;
        var delay = DelayFor(context);
        var registersOn = !Pin(11);
        for (var i = 0; i < 8; i++)
        {
            var p = OutputPins[i];
            LogicState state;
            if (_logic[p] is not { } tree) state = LogicState.HighImpedance;
            else if (_registered[p]) state = registersOn ? LogicStateExtensions.FromBool(_q[p]) : LogicState.HighImpedance;
            else
            {
                var enabled = _enable[p] is not { } oe || Eval(oe, Pin);
                state = enabled ? LogicStateExtensions.FromBool(Eval(tree, Pin)) : LogicState.HighImpedance;
            }

            Request(context, i, state, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_q);
        _lastClock = false;
    }
}
