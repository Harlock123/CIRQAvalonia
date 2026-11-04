using System.Globalization;
using Cirq.Core.Digital;

namespace Cirq.Components.Processors;

/// <summary>
/// The MOS 6510 — the Commodore 64's processor — and its HMOS remake the 8500: an NMOS 6502 core
/// with a six-bit I/O port on the chip and an AEC pin, on their own DIP-40 pinout.
/// <para>
/// <b>The core is the 6502's</b>, the same <see cref="Mos6502Core"/> cycle for cycle, undocumented
/// opcodes and decimal quirks included: the 6510 differs only in what is around it.
/// </para>
/// <para>
/// <b>The port</b> is two registers at the bottom of memory: the data direction register at $0000
/// (a one makes that pin an output) and the data register at $0001, driving P0-P5. The registers sit
/// inside the processor, but the processor still runs a normal bus cycle for them: a write to $0000
/// or $0001 goes out on the address and data buses too, so the RAM underneath takes the byte as well
/// — which is what happens on a C64 — and a read puts the address on the bus, but the processor
/// takes the port's register and ignores whatever the bus holds. Reading $0001 gives the data
/// register for output bits and the pins for inputs; reading $0000 gives the direction register.
/// The writes take effect on the pins as the cycle ends.
/// </para>
/// <para>
/// <b>AEC</b> (address enable control) low lets go of the address bus, the data bus and R/W, as the
/// 65C02's BE does, so another bus master — the C64's video chip — can use them; it does not stop
/// the processor, which is what RDY is for.
/// </para>
/// <para>
/// Simplified: a port input with nothing driving it reads as a one (the C64 has pull-ups on the
/// lines it uses; the chip has none inside here). Bits 6 and 7 have no pins and read back what was
/// last written to them; on the silicon they hold it only for a while when set as inputs, which is
/// not modelled. /RES clears both registers, so every port pin starts as an input.
/// </para>
/// <para>
/// Pinout: 1=Φ0 in, 2=RDY, 3=/IRQ, 4=/NMI, 5=AEC, 6=VCC, 7-20=A0-A13, 21=VSS, 22-23=A14-A15,
/// 24-29=P5-P0, 30-37=D7-D0, 38=R/W, 39=Φ2 out, 40=/RES. There is no SYNC, no SO and no Φ1 out.
/// </para>
/// </summary>
public abstract class Processor6510 : Processor6502
{
    private byte _direction;
    private byte _output;
    private byte _pins = 0x3F;

    private protected Processor6510() : base(Cpu6502Variant.Nmos6502, ProcessorPackage.Mos6510)
    {
    }

    /// <summary>The data direction register at $0000: a one makes that port pin an output.</summary>
    public byte PortDirection => _direction;

    /// <summary>The data register at $0001, as written.</summary>
    public byte PortOutput => _output;

    /// <summary>P0-P5 as the processor last saw them, a one for a pin at no level or high.</summary>
    public byte PortPins => _pins;

    /// <summary>What a read of $0001 gives: the data register for output bits, the pins for inputs.</summary>
    public byte PortValue => (byte)((_output & (_direction | 0xC0)) | (_pins & ~_direction & 0x3F));

    /// <summary>The port as a line: <c>DDR=2F PORT=37 PINS=37</c>.</summary>
    public string PortText =>
        string.Create(CultureInfo.InvariantCulture, $"DDR={_direction:X2} PORT={_output:X2} PINS={_pins:X2}");

    private protected override byte? ReadInternal(IDigitalContext context, ushort address) => address switch
    {
        0 => _direction,
        1 => PortValue,
        _ => null,
    };

    private protected override void WriteInternal(IDigitalContext context, ushort address, byte value)
    {
        if (address > 1) return;

        if (address == 0) _direction = value;
        else _output = value;

        DrivePort(context, DelayFor(context));
        NotifyValueChanged();
    }

    private protected override void EndCycle(IDigitalContext context)
    {
        var pins = 0;
        for (var i = 0; i < 6; i++)
            if (!context.ReadInput(Port[i], Levels).IsLow()) pins |= 1 << i;

        _pins = (byte)pins;
    }

    private protected override void PresentPort(IDigitalContext context) => DrivePort(context, 0);

    private protected override void ResetPort(IDigitalContext? context)
    {
        _direction = 0;
        _output = 0;
        if (context is not null) DrivePort(context, DelayFor(context));
    }

    private void DrivePort(IDigitalContext context, double delay)
    {
        for (var i = 0; i < 6; i++)
        {
            var output = (_direction & (1 << i)) != 0;
            Request(context, PortOutputIndex + i,
                !output ? LogicState.HighImpedance : (_output & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);
        }
    }

    private sealed record PortState(byte Direction, byte Output, byte Pins);

    private protected override object? CapturePort() => new PortState(_direction, _output, _pins);

    private protected override void AdoptPort(object? state)
    {
        if (state is not PortState port) return;
        _direction = port.Direction;
        _output = port.Output;
        _pins = port.Pins;
    }
}

/// <summary>
/// The MOS 6510, the Commodore 64's processor: a 6502 with a six-bit I/O port at $0000/$0001 and
/// AEC. See <see cref="Processor6510"/>.
/// </summary>
public sealed class Cpu6510() : Processor6510
{
    public override string PartNumber => "6510";
}

/// <summary>
/// The MOS 8500, the 6510 made in HMOS for the later C64s: the same pinout, port and instruction
/// set. It runs cooler and its unused port bits forget sooner, neither of which is modelled, so here
/// it is the 6510 under another number.
/// </summary>
public sealed class Cpu8500() : Processor6510
{
    public override string PartNumber => "8500";
}
