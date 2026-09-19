using System.Runtime.CompilerServices;

// The engine drives the mutable parts of SimulationState (time, mode, iteration counters) that
// components must only ever read. Friend access keeps those setters off the public API.
[assembly: InternalsVisibleTo("Cirq.Engine")]
[assembly: InternalsVisibleTo("Cirq.Components")]
[assembly: InternalsVisibleTo("Cirq.Engine.Tests")]
[assembly: InternalsVisibleTo("Cirq.Components.Tests")]
