namespace Cirq.Components.Processors;

/// <summary>The parity bit a serial frame carries, if any.</summary>
internal enum SerialParity
{
    None,
    Even,
    Odd,

    /// <summary>Always 0, a space.</summary>
    Space,

    /// <summary>Always 1, a mark.</summary>
    Mark,
}

/// <summary>
/// One direction pair of an asynchronous serial port — a transmitter and a receiver at a bit rate —
/// timed in seconds rather than clocked, for the UARTs of the 68000's peripheral chips. A frame is a
/// start bit, five to eight data bits least significant first, an optional parity bit, and a stop
/// length that need not be whole bits (the 68681 can stop for 0.563 to 2 bits). The receiver finds
/// the falling edge of a start bit, checks the start bit is still low half a bit later, then samples
/// the middle of each bit on its own clock, so a mismatched rate gives definite wrong bytes and
/// framing errors, as a real one does.
/// </summary>
internal sealed class SerialLine
{
    public int DataBits { get; set; } = 8;

    public SerialParity Parity { get; set; }

    /// <summary>The stop length in bit times.</summary>
    public double StopBits { get; set; } = 1;

    /// <summary>Seconds per bit for the transmitter; zero or less stops it.</summary>
    public double TransmitBitTime { get; set; }

    /// <summary>Seconds per bit for the receiver.</summary>
    public double ReceiveBitTime { get; set; }

    // ---- transmitter --------------------------------------------------------------

    private bool[] _bits = [];
    private double _stop;
    private double _start = double.NaN;
    private double _bitTime;

    /// <summary>True while a frame is going out, stop bits included.</summary>
    public bool Sending(double now) => !double.IsNaN(_start) && now < End - 1e-12;

    private double End => _start + ((_bits.Length + _stop) * _bitTime);

    /// <summary>Starts a frame with <paramref name="value"/> at <paramref name="now"/>.</summary>
    public void Send(double now, int value)
    {
        List<bool> bits = [false];
        var ones = 0;
        for (var i = 0; i < DataBits; i++)
        {
            var bit = (value & (1 << i)) != 0;
            if (bit) ones++;
            bits.Add(bit);
        }

        switch (Parity)
        {
            case SerialParity.Even: bits.Add(ones % 2 != 0); break;
            case SerialParity.Odd: bits.Add(ones % 2 == 0); break;
            case SerialParity.Space: bits.Add(false); break;
            case SerialParity.Mark: bits.Add(true); break;
        }

        _bits = [.. bits];
        _stop = StopBits;
        _bitTime = TransmitBitTime;
        _start = now;
    }

    /// <summary>Forgets a frame in progress: the line goes back to marking.</summary>
    public void StopSending()
    {
        _bits = [];
        _start = double.NaN;
    }

    /// <summary>The transmit line's level: high (marking) when idle and in the stop bits.</summary>
    public bool Level(double now)
    {
        if (double.IsNaN(_start)) return true;
        var index = (int)Math.Floor(((now - _start) / _bitTime) + 1e-6);
        return index < 0 || index >= _bits.Length || _bits[index];
    }

    // ---- receiver -----------------------------------------------------------------

    private bool _lastRx = true;
    private double _sampleAt = double.NaN;
    private int _index;
    private int _shift;
    private bool _parityBad;
    private bool _anyHigh;

    /// <summary>True while a frame is being received.</summary>
    public bool Receiving => !double.IsNaN(_sampleAt);

    /// <summary>
    /// Looks at the receive line at <paramref name="now"/>, sampling any bit centres due. A whole
    /// frame calls <paramref name="received"/> with the byte, a framing error (the stop bit low), a
    /// parity error, and a break (every bit low, the stop bit included).
    /// </summary>
    public void Receive(double now, bool rx, bool enabled, Action<int, bool, bool, bool> received)
    {
        if (enabled && double.IsNaN(_sampleAt) && _lastRx && !rx && ReceiveBitTime > 0)
        {
            _index = 0;
            _shift = 0;
            _parityBad = false;
            _anyHigh = false;
            _sampleAt = now + (ReceiveBitTime / 2);
        }

        _lastRx = rx;

        while (!double.IsNaN(_sampleAt) && now >= _sampleAt - 1e-12)
        {
            Sample(rx, received);
            if (!double.IsNaN(_sampleAt)) _sampleAt += ReceiveBitTime;
        }
    }

    private void Sample(bool high, Action<int, bool, bool, bool> received)
    {
        if (_index == 0)
        {
            // A start bit that has gone high again by its middle was a glitch.
            if (high) _sampleAt = double.NaN;
            else _index++;
            return;
        }

        if (high) _anyHigh = true;

        if (_index <= DataBits)
        {
            if (high) _shift |= 1 << (_index - 1);
            _index++;
            return;
        }

        if (Parity != SerialParity.None && _index == DataBits + 1)
        {
            var ones = System.Numerics.BitOperations.PopCount((uint)_shift);
            var expected = Parity switch
            {
                SerialParity.Even => ones % 2 != 0,
                SerialParity.Odd => ones % 2 == 0,
                SerialParity.Space => false,
                _ => true,
            };
            if (high != expected) _parityBad = true;
            _index++;
            return;
        }

        // The stop bit.
        _sampleAt = double.NaN;
        _index = 0;
        received(_shift, !high, _parityBad, !_anyHigh && !high);
    }

    /// <summary>The next instant either half needs a time point at: a bit edge going out or a sample coming in.</summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;

        if (!double.IsNaN(_start) && _bitTime > 0)
        {
            for (var i = 1; i <= _bits.Length; i++)
            {
                var at = _start + (i * _bitTime);
                if (at > time + 1e-12)
                {
                    next = at;
                    break;
                }
            }

            if (next is null && End > time + 1e-12) next = End;
        }

        if (!double.IsNaN(_sampleAt) && _sampleAt > time && (next is null || _sampleAt < next)) next = _sampleAt;
        return next;
    }

    public void Reset()
    {
        StopSending();
        _lastRx = true;
        _sampleAt = double.NaN;
        _index = 0;
    }
}
