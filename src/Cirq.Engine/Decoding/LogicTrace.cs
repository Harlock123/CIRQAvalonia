using Cirq.Core.Primitives;

namespace Cirq.Engine.Decoding;

/// <summary>
/// A recorded trace seen as logic: a list of the moments it changed, and a way to ask what it was
/// doing at any instant.
/// <para>
/// Every decoder below works from one of these rather than from the raw samples, because a
/// protocol is defined by edges and levels and not by voltages. Building it once also gets the
/// threshold decision out of five different state machines.
/// </para>
/// </summary>
public sealed class LogicTrace
{
    private readonly List<(double Time, bool Level)> _edges = [];

    /// <summary>
    /// Converts samples to edges at a threshold, with hysteresis either side of it.
    /// <para>
    /// The hysteresis is not optional. A real trace crosses its threshold several times on every
    /// edge — from ringing, from the solver's own step, from anything the circuit picked up — and
    /// a decoder fed those extra crossings sees a clock running at several times its actual rate
    /// and decodes nonsense. A tenth of the swing either side is enough and costs nothing.
    /// </para>
    /// </summary>
    public LogicTrace(IReadOnlyList<DataPoint> samples, double threshold, double hysteresis)
    {
        if (samples.Count == 0) return;

        var high = samples[0].Value >= threshold;

        _edges.Add((samples[0].Time, high));
        Start = samples[0].Time;
        End = samples[^1].Time;

        // How far apart the samples are, which is the limit on what can be decoded from them.
        SampleInterval = samples.Count > 1
            ? (samples[^1].Time - samples[0].Time) / (samples.Count - 1)
            : 0;

        foreach (var sample in samples)
        {
            var crossed = high
                ? sample.Value < threshold - hysteresis
                : sample.Value > threshold + hysteresis;

            if (!crossed) continue;

            high = !high;
            _edges.Add((sample.Time, high));
        }
    }

    /// <summary>Builds one from samples, choosing the threshold from the trace's own swing.</summary>
    public static LogicTrace From(IReadOnlyList<DataPoint> samples)
    {
        if (samples.Count == 0) return new LogicTrace([], 0.5, 0.1);

        var low = double.MaxValue;
        var high = double.MinValue;

        foreach (var sample in samples)
        {
            low = Math.Min(low, sample.Value);
            high = Math.Max(high, sample.Value);
        }

        var swing = high - low;

        // A trace that never moved has no threshold worth choosing. Half a volt keeps a line
        // parked at zero reading low and one parked at a rail reading high.
        if (swing < 1e-6) return new LogicTrace(samples, low + 0.5, 0.0);

        return new LogicTrace(samples, low + (swing / 2.0), swing * 0.1);
    }

    /// <summary>First sample time.</summary>
    public double Start { get; }

    /// <summary>Last sample time.</summary>
    public double End { get; }

    /// <summary>True when there is nothing to decode.</summary>
    public bool IsEmpty => _edges.Count == 0;

    /// <summary>Average spacing of the samples this was built from, in seconds.</summary>
    public double SampleInterval { get; }

    /// <summary>The shortest gap between two changes of level, or zero when there are none.</summary>
    public double ShortestPulse
    {
        get
        {
            var shortest = double.MaxValue;

            for (var i = 2; i < _edges.Count; i++)
                shortest = Math.Min(shortest, _edges[i].Time - _edges[i - 1].Time);

            return shortest == double.MaxValue ? 0 : shortest;
        }
    }

    /// <summary>
    /// What fraction of the pulses in this trace are only a sample or so wide.
    /// <para>
    /// A <i>robust</i> measure rather than the shortest one, and that distinction turned out to
    /// matter. An open-collector line re-driven immediately after being released shows a single
    /// one-sample transition, and condemning a whole capture on the strength of one such glitch
    /// refuses perfectly good traces. What actually indicates a capture too coarse to decode is
    /// that a <i>lot</i> of its pulses are down at the sample interval.
    /// </para>
    /// </summary>
    public double NarrowPulseFraction
    {
        get
        {
            if (SampleInterval <= 0 || _edges.Count < 4) return 0;

            var narrow = 0;
            var total = 0;

            for (var i = 2; i < _edges.Count; i++)
            {
                total++;
                if (_edges[i].Time - _edges[i - 1].Time < SampleInterval * 1.5) narrow++;
            }

            return total == 0 ? 0 : (double)narrow / total;
        }
    }

    /// <summary>
    /// True when the samples are too far apart to describe the edges in them.
    /// <para>
    /// This matters more for decoding than for anything else the scope does. A trace drawn from
    /// too few samples looks slightly wrong and everybody notices; a <i>decode</i> from too few
    /// samples produces confident, plausible, completely incorrect bytes, because a pulse that
    /// fell between two samples is not a pulse that looks short — it is a pulse that is not there
    /// at all, and every bit after it is shifted.
    /// </para>
    /// </summary>
    public bool IsUndersampled => NarrowPulseFraction > 0.2;

    /// <summary>A sentence about the sampling, or null when there is nothing to say.</summary>
    public string? SamplingWarning => IsUndersampled
        ? $"The capture has a sample every {SampleInterval * 1e6:0.##} µs, and " +
          $"{NarrowPulseFraction * 100:0} % of the pulses in it are no wider than that — so " +
          "pulses are being missed altogether. Decoding this would give bytes that look plausible " +
          "and are wrong. Lower the scope's timebase, or set a finer probe sample interval, and " +
          "capture it again."
        : null;

    /// <summary>Every change of level, in order, with the opening level as the first entry.</summary>
    public IReadOnlyList<(double Time, bool Level)> Edges => _edges;

    /// <summary>What the line was doing at one instant.</summary>
    public bool LevelAt(double time)
    {
        if (_edges.Count == 0) return false;

        var level = _edges[0].Level;

        foreach (var edge in _edges)
        {
            if (edge.Time > time) break;
            level = edge.Level;
        }

        return level;
    }

    /// <summary>
    /// The times the line went the given way, not counting the opening entry — that one records
    /// where the trace started rather than a transition.
    /// </summary>
    public IEnumerable<double> EdgesGoing(bool rising)
    {
        for (var i = 1; i < _edges.Count; i++)
            if (_edges[i].Level == rising)
                yield return _edges[i].Time;
    }

    /// <summary>The next change after a time, or null when the trace ends first.</summary>
    public (double Time, bool Level)? NextAfter(double time)
    {
        for (var i = 1; i < _edges.Count; i++)
            if (_edges[i].Time > time)
                return _edges[i];

        return null;
    }
}
