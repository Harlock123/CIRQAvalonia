namespace Cirq.Core.Digital;

/// <summary>Logic level carried by a digital pin.</summary>
public enum LogicState
{
    /// <summary>Driven low.</summary>
    Low = 0,
    /// <summary>Driven high.</summary>
    High = 1,
    /// <summary>Indeterminate: the analog level sits between VIL and VIH.</summary>
    Unknown = 2,
    /// <summary>Output disabled (tri-state / open collector released).</summary>
    HighImpedance = 3,
}

public static class LogicStateExtensions
{
    public static bool IsHigh(this LogicState s) => s == LogicState.High;

    public static bool IsLow(this LogicState s) => s == LogicState.Low;

    public static bool IsDriven(this LogicState s) => s is LogicState.Low or LogicState.High;

    public static LogicState Invert(this LogicState s) => s switch
    {
        LogicState.Low => LogicState.High,
        LogicState.High => LogicState.Low,
        _ => LogicState.Unknown,
    };

    /// <summary>Treats <see cref="LogicState.Unknown"/> and Hi-Z as low, the pull-down default.</summary>
    public static bool ToBool(this LogicState s) => s == LogicState.High;

    public static LogicState FromBool(bool value) => value ? LogicState.High : LogicState.Low;

    public static char ToChar(this LogicState s) => s switch
    {
        LogicState.Low => '0',
        LogicState.High => '1',
        LogicState.HighImpedance => 'Z',
        _ => 'X',
    };
}
