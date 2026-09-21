namespace Cirq.Engine.Decoding;

/// <summary>What kind of thing a decoded span is, which is how it is coloured and grouped.</summary>
public enum SpanKind
{
    /// <summary>A framing element: a start condition, a stop, a reset pulse, a chip select.</summary>
    Control,

    /// <summary>An address, a device id, a register number.</summary>
    Address,

    /// <summary>A payload byte.</summary>
    Data,

    /// <summary>An acknowledgement or its absence, or anything else the protocol answers with.</summary>
    Response,

    /// <summary>Something the protocol does not allow. The interesting ones, usually.</summary>
    Error,
}

/// <summary>One decoded element of a transaction.</summary>
/// <param name="Start">When it began, in seconds.</param>
/// <param name="End">When it finished.</param>
/// <param name="Kind">What sort of element it is.</param>
/// <param name="Text">Short form, e.g. "0x50" or "START".</param>
/// <param name="Detail">A sentence about it, or empty.</param>
public sealed record DecodedSpan(
    double Start, double End, SpanKind Kind, string Text, string Detail = "")
{
    public double Duration => End - Start;

    public override string ToString() => Text;
}

/// <summary>Everything one decoder made of one stretch of trace.</summary>
/// <param name="Protocol">What it was decoded as.</param>
/// <param name="Spans">The elements, in time order.</param>
/// <param name="Summary">A line about the whole thing, for the status strip.</param>
public sealed record DecodeResult(string Protocol, IReadOnlyList<DecodedSpan> Spans, string Summary)
{
    public bool IsEmpty => Spans.Count == 0;

    /// <summary>The whole transaction as one line, which is how a decode is usually read.</summary>
    public string Transcript => string.Join(" ", Spans.Select(s => s.Text));

    public static DecodeResult Nothing(string protocol, string why) => new(protocol, [], why);
}
