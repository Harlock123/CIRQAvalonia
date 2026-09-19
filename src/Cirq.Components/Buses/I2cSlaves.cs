using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// The receiving half of I²C: watching two lines and working out what is being said on them.
/// <para>
/// There is no framing beyond the lines themselves. A <b>start</b> is the data line falling while
/// the clock is high; a <b>stop</b> is it rising while the clock is high; everything in between is
/// bits, sampled while the clock is high because that is the only time the data is guaranteed
/// still. Every ninth clock is an acknowledge, and a device claims a transfer by pulling the line
/// down during it — which is the whole of addressing.
/// </para>
/// <para>
/// Clock stretching is not modelled. A slave here never holds the clock down to buy time, which
/// real ones do and which matters if you are simulating something slow.
/// </para>
/// </summary>
public abstract partial class I2cSlave : I2cDevice
{
    private enum Phase { Idle, Address, Receiving, Sending, AckFromMaster }

    private bool _previousScl;
    private bool _previousSda;

    private Phase _phase = Phase.Idle;
    private int _bits;
    private int _shift;
    private bool _driveLow;
    private bool _reading;
    private bool _masterAcknowledged;

    protected I2cSlave(Point? sda = null, Point? scl = null, Point? vcc = null, Point? gnd = null)
        : base(sda, scl, vcc, gnd)
    {
        PropagationDelay = 200e-9;
        ConfigurePins([Scl], [Sda]);
        Terminals = [Vcc, Sda, Gnd, Scl];
    }

    /// <summary>The seven-bit address it answers to.</summary>
    [ObservableProperty]
    public partial int Address { get; set; } = 0x50;

    /// <summary>True while this device is the one being talked to.</summary>
    public bool IsAddressed { get; private set; }

    /// <summary>Called when a transfer is addressed to this device. Return false to ignore it.</summary>
    protected abstract bool OnAddressed(bool reading);

    /// <summary>Called with each byte written to it. Return false to refuse the rest.</summary>
    protected abstract bool OnByteReceived(int value);

    /// <summary>Called when the master wants a byte.</summary>
    protected abstract int OnByteRequested();

    /// <summary>Called at a stop condition.</summary>
    protected virtual void OnTransferEnded() { }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var scl = IsHigh(context, Scl);
        var sda = IsHigh(context, Sda);

        // Start and stop are the only things that happen while the clock is high, which is why
        // the data line is otherwise required to be still during that window.
        if (scl && _previousScl && sda != _previousSda)
        {
            if (!sda) Begin();
            else End();
        }
        else if (scl && !_previousScl)
        {
            SampleOnRisingClock(sda);
        }
        else if (!scl && _previousScl)
        {
            PrepareWhileClockIsLow();
        }

        _previousScl = scl;
        _previousSda = sda;

        Drive(context, 0, _driveLow);
    }

    private void Begin()
    {
        _phase = Phase.Address;
        _bits = 0;
        _shift = 0;
        _driveLow = false;
        IsAddressed = false;
    }

    private void End()
    {
        if (IsAddressed) OnTransferEnded();

        _phase = Phase.Idle;
        _driveLow = false;
        IsAddressed = false;
    }

    private void SampleOnRisingClock(bool sda)
    {
        switch (_phase)
        {
            case Phase.Address or Phase.Receiving when _bits < 8:
                _shift = (_shift << 1) | (sda ? 1 : 0);
                _bits++;
                break;

            case Phase.AckFromMaster:
                // The master acknowledging means it wants another byte; not acknowledging is how
                // it says that was the last one. Acting on it waits for the clock to fall, since
                // that is when anything driven has to be set up.
                _masterAcknowledged = !sda;
                break;
        }
    }

    /// <summary>
    /// Everything a device drives, it drives while the clock is low, so that the line is settled
    /// before the master raises the clock over it.
    /// </summary>
    private void PrepareWhileClockIsLow()
    {
        switch (_phase)
        {
            case Phase.Address when _bits == 8:
                _reading = (_shift & 1) != 0;
                IsAddressed = (_shift >> 1) == (Address & 0x7F) && OnAddressed(_reading);

                _driveLow = IsAddressed;                // the acknowledge
                _bits = 9;
                break;

            case Phase.Address when _bits == 9:
                _driveLow = false;
                _bits = 0;
                _shift = 0;

                if (!IsAddressed) { _phase = Phase.Idle; break; }

                if (_reading)
                {
                    _phase = Phase.Sending;
                    _shift = OnByteRequested() & 0xFF;
                    _driveLow = (_shift & 0x80) == 0;
                    _bits = 1;
                }
                else
                {
                    _phase = Phase.Receiving;
                }
                break;

            case Phase.Receiving when _bits == 8:
                _driveLow = IsAddressed && OnByteReceived(_shift & 0xFF);
                _bits = 9;
                break;

            case Phase.Receiving when _bits == 9:
                _driveLow = false;
                _bits = 0;
                _shift = 0;
                break;

            case Phase.Sending when _bits < 8:
                _driveLow = (_shift & (0x80 >> _bits)) == 0;
                _bits++;
                break;

            case Phase.Sending when _bits == 8:
                _driveLow = false;                      // let go, so the master can acknowledge
                _phase = Phase.AckFromMaster;
                _masterAcknowledged = false;
                break;

            case Phase.AckFromMaster:
                if (!_masterAcknowledged)
                {
                    // That was the last byte it wanted.
                    End();
                    break;
                }

                // Another one. Fetch it and put its first bit up before the clock rises.
                _phase = Phase.Sending;
                _shift = OnByteRequested() & 0xFF;
                _driveLow = (_shift & 0x80) == 0;
                _bits = 1;
                break;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _previousScl = true;
        _previousSda = true;
        _phase = Phase.Idle;
        _bits = 0;
        _shift = 0;
        _driveLow = false;
        _reading = false;
        _masterAcknowledged = false;
        IsAddressed = false;
    }
}

/// <summary>
/// A 24LC256 I²C EEPROM: the little eight-pin chip that remembers things across a power cycle.
/// <para>
/// Writing means sending two address bytes and then the data; reading means writing the address,
/// then a fresh start and a read. That second start — a <i>repeated start</i> — is why reading
/// from an EEPROM looks more complicated than it ought to, and it is the shape of nearly every
/// I²C device that has registers.
/// </para>
/// </summary>
public sealed partial class I2cEeprom : I2cSlave
{
    private readonly byte[] _memory;

    private int _pointer;
    private int _addressBytesSeen;

    public I2cEeprom()
    {
        Address = 0x50;
        _memory = new byte[32768];
    }

    /// <summary>Bytes it holds. A 24LC256 is 32 kilobytes.</summary>
    public int Capacity => _memory.Length;

    public override string ComponentType => "I2C EEPROM";

    public override string ValueLabel => $"24LC256 @ {Address:X2}";

    /// <summary>Reads a byte out of the array, for checking what was stored.</summary>
    public int Read(int address) => _memory[address % _memory.Length];

    protected override bool OnAddressed(bool reading)
    {
        // A write starts with the address it is about to use; a read carries on from wherever the
        // pointer was left, which is what makes the repeated-start dance work.
        if (!reading) _addressBytesSeen = 0;
        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        if (_addressBytesSeen < 2)
        {
            _pointer = _addressBytesSeen == 0 ? value << 8 : (_pointer & 0xFF00) | value;
            _addressBytesSeen++;
            return true;
        }

        _memory[_pointer % _memory.Length] = (byte)value;
        _pointer = (_pointer + 1) % _memory.Length;
        return true;
    }

    protected override int OnByteRequested()
    {
        var value = _memory[_pointer % _memory.Length];
        _pointer = (_pointer + 1) % _memory.Length;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_memory);
        _pointer = 0;
        _addressBytesSeen = 0;
    }
}

/// <summary>
/// A PCF8574 I²C port expander: eight pins you can set over two wires.
/// <para>
/// It is the answer to running out of GPIO, and it is what is on the back of every "I²C LCD" —
/// the backpack is one of these wired to an HD44780's parallel pins. A byte written to it appears
/// on the eight outputs, and that is the whole device.
/// </para>
/// <para>
/// Its outputs are <b>quasi-bidirectional</b>: writing a one does not drive the pin high, it
/// releases it to a weak pull-up inside the chip. That is why a PCF8574 can sink an LED nicely
/// and barely source anything at all, and why you wire indicators to it the other way up from
/// what you would expect.
/// </para>
/// </summary>
public sealed partial class Pcf8574 : I2cSlave
{
    private readonly Terminal[] _pins = new Terminal[8];

    private int _port = 0xFF;

    public Pcf8574()
        : base(new Point(-40, -50), new Point(-40, -20), new Point(-40, 20), new Point(-40, 50))
    {
        Address = 0x20;

        for (var i = 0; i < 8; i++)
            _pins[i] = new Terminal($"p{i}", $"P{i}", TerminalType.Output,
                new Point(40, -70 + (i * 20)));

        // The bus pins are on the left; the port pins go down the other side.
        Terminals = [Sda, Scl, Vcc, Gnd, .. _pins];
        ConfigurePins([Scl], [Sda, .. _pins]);
    }

    /// <summary>The eight port pins.</summary>
    public IReadOnlyList<Terminal> Pins => _pins;

    public override string ComponentType => "I2C Expander";

    public override string ValueLabel => $"PCF8574 @ {Address:X2}";

    /// <summary>The byte last written to the port.</summary>
    public int Port => _port;

    protected override bool OnAddressed(bool reading) => true;

    protected override bool OnByteReceived(int value)
    {
        _port = value & 0xFF;
        NotifyValueChanged();
        return true;
    }

    protected override int OnByteRequested() => _port;

    public override void EvaluateLogic(IDigitalContext context)
    {
        base.EvaluateLogic(context);

        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
        {
            // A one releases the pin rather than driving it, which is what quasi-bidirectional
            // means and why these sink far better than they source.
            var high = (_port & (1 << i)) != 0;
            context.Schedule(this, i + 1, high ? LogicState.HighImpedance : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _port = 0xFF;
    }
}
