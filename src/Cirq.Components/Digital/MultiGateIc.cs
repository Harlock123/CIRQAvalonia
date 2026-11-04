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

    /// <summary>Widest gate that still gets a stack buffer rather than a heap array.</summary>
    private const int StackLimit = 8;

    private readonly GateDefinition[] _gates;
    private readonly Terminal[][] _gateInputs;
    private readonly Terminal[] _gateOutputs;
    private readonly int _widestGate;

    protected MultiGateIc(
        int pinCount,
        GateFunction function,
        int vccPin,
        int gndPin,
        IReadOnlyList<GateDefinition> gates,
        IReadOnlyList<int>? unconnectedPins = null,
        bool openCollector = false)
        : base(pinCount)
    {
        Function = function;
        IsOpenCollector = openCollector;
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

            // An open-collector output is built to share a wire, and says so to the rule check the
            // way a tri-state one does.
            _gateOutputs[g] = Pin(definition.OutputPin, $"{g + 1}Y",
                openCollector ? TerminalType.Bidirectional : TerminalType.Output);
        }

        foreach (var gate in _gateInputs) _widestGate = Math.Max(_widestGate, gate.Length);

        Gnd = Pin(gndPin, "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, "VCC", TerminalType.Power);

        foreach (var pin in unconnectedPins ?? [])
            Pin(pin, "NC", TerminalType.Passive);

        Terminals = [.. pins.Skip(1).Where(p => p is not null)];
        ConfigurePins([.. _gateInputs.SelectMany(g => g)], _gateOutputs);
    }

    public GateFunction Function { get; }

    /// <summary>
    /// True for the open-collector packages (7403, 7406, 7407): an output pulls down or lets go,
    /// never drives high, so it needs a pull-up — and several can be tied together, any one of
    /// them pulling the wire low.
    /// </summary>
    public bool IsOpenCollector { get; }

    /// <summary>Number of independent gates in the package.</summary>
    public int GateCount => _gates.Length;

    /// <summary>Input pins of one gate, indexed from 0.</summary>
    public IReadOnlyList<Terminal> GateInputs(int index) => _gateInputs[index];

    /// <summary>Output pin of one gate, indexed from 0.</summary>
    public Terminal GateOutput(int index) => _gateOutputs[index];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        // One buffer for the whole call, sized to the widest gate in the package and re-sliced
        // per gate. A stackalloc inside the loop is not released until the method returns, so the
        // frame grew with every gate instead of being reused (CA2014). Nothing outlives the call:
        // Evaluate returns a LogicState, and a Span cannot be stored anyway.
        Span<LogicState> buffer = _widestGate <= StackLimit
            ? stackalloc LogicState[StackLimit]
            : new LogicState[_widestGate];

        for (var g = 0; g < _gates.Length; g++)
        {
            var inputs = _gateInputs[g];
            var states = buffer[..inputs.Length];

            for (var i = 0; i < inputs.Length; i++)
                states[i] = context.ReadInput(inputs[i], Levels);

            context.Schedule(this, g, Drive(LogicGate.Evaluate(Function, states)), delay);
        }
    }

    /// <summary>
    /// What the output stage does with a gate's answer: a push-pull stage drives it, an open
    /// collector releases a high rather than driving it.
    /// </summary>
    protected LogicState Drive(LogicState state) =>
        IsOpenCollector && state == LogicState.High ? LogicState.HighImpedance : state;
}
