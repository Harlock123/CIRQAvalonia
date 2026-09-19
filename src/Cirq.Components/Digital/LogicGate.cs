using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

public enum GateFunction
{
    And,
    Or,
    Not,
    Nand,
    Nor,
    Xor,
    Xnor,
    Buffer,
}

/// <summary>
/// A discrete logic gate with a configurable function and input count. Thresholds and drive
/// levels come from <see cref="DigitalComponent.Levels"/>, so the same symbol can behave as TTL
/// or as CMOS.
/// </summary>
public partial class LogicGate : DigitalComponent
{
    public LogicGate(GateFunction function = GateFunction.And, int inputCount = 2)
    {
        Function = function;
        var inputs = BuildInputs(IsUnary(function) ? 1 : Math.Max(2, inputCount));
        Out = new Terminal("y", "Y", TerminalType.Output, new Point(40, 0));

        Terminals = [.. inputs, Out];
        ConfigurePins(inputs, [Out]);
    }

    [ObservableProperty]
    public partial GateFunction Function { get; set; }

    public Terminal Out { get; }

    public override string ComponentType => Function.ToString().ToUpperInvariant();

    public override string ValueLabel => $"{Function}{(InputTerminals.Count > 2 ? $" x{InputTerminals.Count}" : "")}";

    private static bool IsUnary(GateFunction f) => f is GateFunction.Not or GateFunction.Buffer;

    private static List<Terminal> BuildInputs(int count)
    {
        var inputs = new List<Terminal>(count);
        var span = (count - 1) * 20.0;
        for (var i = 0; i < count; i++)
        {
            var y = count == 1 ? 0 : -span / 2 + i * 20.0;
            inputs.Add(new Terminal($"in{i}", $"{(char)('A' + i)}", TerminalType.Input, new Point(-40, y)));
        }
        return inputs;
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var states = new LogicState[InputTerminals.Count];
        for (var i = 0; i < states.Length; i++)
            states[i] = context.ReadInput(InputTerminals[i], Levels);

        context.Schedule(this, 0, Evaluate(Function, states), DelayFor(context));
    }

    /// <summary>
    /// Truth table for the gate. An unknown input only forces an unknown output when it could
    /// actually change the result: a single low input still forces an AND gate low, for instance.
    /// </summary>
    public static LogicState Evaluate(GateFunction function, ReadOnlySpan<LogicState> inputs)
    {
        if (inputs.Length == 0) return LogicState.Unknown;

        var anyUnknown = false;
        var anyHigh = false;
        var anyLow = false;
        var highCount = 0;

        foreach (var s in inputs)
        {
            switch (s)
            {
                case LogicState.High: anyHigh = true; highCount++; break;
                case LogicState.Low: anyLow = true; break;
                default: anyUnknown = true; break;
            }
        }

        return function switch
        {
            GateFunction.And => anyLow ? LogicState.Low : anyUnknown ? LogicState.Unknown : LogicState.High,
            GateFunction.Nand => anyLow ? LogicState.High : anyUnknown ? LogicState.Unknown : LogicState.Low,
            GateFunction.Or => anyHigh ? LogicState.High : anyUnknown ? LogicState.Unknown : LogicState.Low,
            GateFunction.Nor => anyHigh ? LogicState.Low : anyUnknown ? LogicState.Unknown : LogicState.High,
            // Parity gates need every input to be known.
            GateFunction.Xor => anyUnknown ? LogicState.Unknown : LogicStateExtensions.FromBool(highCount % 2 == 1),
            GateFunction.Xnor => anyUnknown ? LogicState.Unknown : LogicStateExtensions.FromBool(highCount % 2 == 0),
            GateFunction.Not => inputs[0].Invert(),
            GateFunction.Buffer => inputs[0],
            _ => LogicState.Unknown,
        };
    }

    partial void OnFunctionChanged(GateFunction value) => NotifyValueChanged();
}
