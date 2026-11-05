using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// What Intel's peripherals for the 8080 and 8085 on a demultiplexed bus share — the 8255A, 8251A,
/// 8253 and 8254, and 8259A: eight data lines, /CS, /RD, /WR and one or two address lines, A0 and
/// A1, from the address latch.
/// <para>
/// <b>The bus.</b> With /CS low, /RD low is a read: the chip drives D0-D7 with the register A0 and
/// A1 select for as long as it lasts, and does the read's side effects (a status cleared, a latch
/// let go) as /RD goes high. /WR low is a write: the byte on D0-D7 is taken as /WR goes high, as
/// the datasheets time it — data set up before the rising edge. The register is the one A0 and A1
/// select while the strobe is low. A chip whose interrupt acknowledge puts bytes on the bus (the
/// 8259A, on /INTA) supplies them through <see cref="Drive"/>.
/// </para>
/// </summary>
public abstract class IntelPeripheral : DigitalIc
{
    private readonly Terminal[] _data = new Terminal[8];

    private bool _accessWas;
    private bool _accessRead;
    private int _accessRegister;
    private byte _accessData;

    private protected IntelPeripheral(int pinCount) : base(pinCount)
    {
    }

    /// <summary>The data lines, D0 to D7. Output indices 0 to 7.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>/CS: low selects the chip.</summary>
    public Terminal ChipSelect { get; private protected set; } = null!;

    public Terminal ReadStrobe { get; private protected set; } = null!;

    public Terminal WriteStrobe { get; private protected set; } = null!;

    public override bool LabelsPins => true;

    private protected void SetDataPin(int index, Terminal pin) => _data[index] = pin;

    /// <summary>Which register an access selects, from the address pins.</summary>
    private protected abstract int SelectedRegister(IDigitalContext context);

    /// <summary>What a read of <paramref name="register"/> gives, with no side effects.</summary>
    private protected abstract byte ReadRegister(int register);

    /// <summary>A read of <paramref name="register"/> has ended: its side effects.</summary>
    private protected virtual void AfterRead(int register)
    {
    }

    /// <summary>A read of <paramref name="register"/> has begun (/RD and /CS low): the 8254's read-back and the 8251's status are taken then.</summary>
    private protected virtual void ReadStarted(int register)
    {
    }

    private protected abstract void WriteRegister(int register, byte value);

    /// <summary>The register an access in progress selects, or −1 between accesses.</summary>
    private protected int CurrentAccess => _accessWas ? _accessRegister : -1;

    /// <summary>The access in progress is a read.</summary>
    private protected bool CurrentAccessReads => _accessWas && _accessRead;

    /// <summary>
    /// A byte to put on D0-D7 regardless of /CS — the 8259A's answer to /INTA — or null. Asked
    /// every time the chip is evaluated, after the register access.
    /// </summary>
    private protected virtual byte? Drive(IDigitalContext context) => null;

    /// <summary>
    /// The bus, every time the chip is evaluated: a register access, its byte or side effects as the
    /// strobe ends, and the data lines.
    /// </summary>
    private protected void EvaluateBus(IDigitalContext context)
    {
        var cs = context.ReadInput(ChipSelect, Levels).IsLow();
        var rd = context.ReadInput(ReadStrobe, Levels).IsLow();
        var wr = context.ReadInput(WriteStrobe, Levels).IsLow();

        if (!context.IsInitializing)
        {
            var access = cs && (rd || wr);
            if (access)
            {
                var register = SelectedRegister(context);
                if (!_accessWas || register != _accessRegister || _accessRead != rd)
                {
                    _accessRegister = register;
                    _accessRead = rd;
                    if (rd) ReadStarted(register);
                }

                if (wr && ReadBus(context) is { } value) _accessData = value;
            }
            else if (_accessWas)
            {
                if (_accessRead) AfterRead(_accessRegister);
                else WriteRegister(_accessRegister, _accessData);
                NotifyValueChanged();
            }

            _accessWas = access;
        }

        var drive = _accessWas && _accessRead && cs && rd ? ReadRegister(_accessRegister) : Drive(context);
        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
            Request(context, i, drive is { } d ? ((d >> i) & 1) != 0 ? LogicState.High : LogicState.Low : LogicState.HighImpedance, delay);
    }

    /// <summary>The data bus, or null if any line is at no logic level.</summary>
    private protected byte? ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_data[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) return null;
        }

        return (byte)value;
    }

    /// <summary>Forgets an access in progress: a reset.</summary>
    private protected void ResetBus()
    {
        _accessWas = false;
        _accessRead = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetBus();
    }
}
