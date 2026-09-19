using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for a package holding several independent gates of the same function. A part is then just
/// its pin map: which pins feed which gate, and which pin each gate drives.
/// <para>
/// This matters more than it looks. The 7400 and the 7402 are both quad two-input packages, but
/// the 7402 puts its outputs on pins 1, 4, 10 and 13 rather than 3, 6, 8 and 11 — wiring one as if
/// it were the other is a classic breadboard mistake, and it should be a mistake here too.
/// </para>
/// </summary>
public abstract class MultiGateIc : DigitalIc
{
    /// <summary>One gate: the pins feeding it, and the pin it drives.</summary>
    protected sealed record GateDefinition(int[] InputPins, int OutputPin);

    private readonly GateDefinition[] _gates;
    private readonly Terminal[][] _gateInputs;
    private readonly Terminal[] _gateOutputs;

    protected MultiGateIc(
        int pinCount,
        GateFunction function,
        int vccPin,
        int gndPin,
        IReadOnlyList<GateDefinition> gates,
        IReadOnlyList<int>? unconnectedPins = null)
        : base(pinCount)
    {
        Function = function;
        _gates = [.. gates];
        _gateInputs = new Terminal[_gates.Length][];
        _gateOutputs = new Terminal[_gates.Length];

        var pins = new Terminal[pinCount + 1];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));

        for (var g = 0; g < _gates.Length; g++)
        {
            var definition = _gates[g];
            _gateInputs[g] = new Terminal[definition.InputPins.Length];

            for (var i = 0; i < definition.InputPins.Length; i++)
            {
                var label = definition.InputPins.Length == 1
                    ? $"{g + 1}A"
                    : $"{g + 1}{(char)('A' + i)}";
                _gateInputs[g][i] = Pin(definition.InputPins[i], label, TerminalType.Input);
            }

            _gateOutputs[g] = Pin(definition.OutputPin, $"{g + 1}Y", TerminalType.Output);
        }

        Gnd = Pin(gndPin, "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, "VCC", TerminalType.Power);

        foreach (var pin in unconnectedPins ?? [])
            Pin(pin, "NC", TerminalType.Passive);

        Terminals = [.. pins.Skip(1).Where(p => p is not null)];
        ConfigurePins([.. _gateInputs.SelectMany(g => g)], _gateOutputs);
    }

    public GateFunction Function { get; }

    /// <summary>Number of independent gates in the package.</summary>
    public int GateCount => _gates.Length;

    /// <summary>Input pins of one gate, indexed from 0.</summary>
    public IReadOnlyList<Terminal> GateInputs(int index) => _gateInputs[index];

    /// <summary>Output pin of one gate, indexed from 0.</summary>
    public Terminal GateOutput(int index) => _gateOutputs[index];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var g = 0; g < _gates.Length; g++)
        {
            var inputs = _gateInputs[g];
            Span<LogicState> states = inputs.Length <= 8 ? stackalloc LogicState[inputs.Length] : new LogicState[inputs.Length];

            for (var i = 0; i < inputs.Length; i++)
                states[i] = context.ReadInput(inputs[i], Levels);

            context.Schedule(this, g, LogicGate.Evaluate(Function, states), delay);
        }
    }
}
