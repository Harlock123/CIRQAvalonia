namespace Cirq.Core.Primitives;

/// <summary>A single sampled value on a probe trace.</summary>
public readonly record struct DataPoint(double Time, double Value);
