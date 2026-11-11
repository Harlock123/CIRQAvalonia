namespace Cirq.Components.Processors;

/// <summary>
/// An Arduino Uno running real firmware: the <see cref="Atmega328p"/> on the Uno's own board, its pins brought out by the
/// names a sketch uses (D0-D13, A0-A5), so a sketch's <c>digitalWrite(13, HIGH)</c> is the pin marked D13 on the
/// schematic.
/// <para>
/// Load the sketch's <c>.hex</c> (the Arduino IDE's Sketch › Export Compiled Binary writes it, and its
/// <c>with_bootloader</c> twin, into the sketch's folder) with Load Memory Image. The board feeds the circuit round it from
/// 5V, IOREF and 3V3, as a Uno on USB does; VIN is not modelled. D0 and D1 are RX and TX; D11, D12 and D13 SPI's MOSI,
/// MISO and SCK; A4 and A5 I2C's SDA and SCL; and D13 has the board's own "L" LED, lit on the symbol.
/// </para>
/// </summary>
public sealed partial class ArduinoUnoFirmware : Atmega328p
{
    public ArduinoUnoFirmware() : base(uno: true)
    {
    }

    public override string PartNumber => "Arduino Uno";

    public override string ComponentType => nameof(ArduinoUnoFirmware);

    /// <summary>Whether the board's "L" LED, on D13, is lit: the pin as it is driven now (the core runs a little ahead).</summary>
    public bool LedLit => GetOutputState(13) == Cirq.Core.Digital.LogicState.High;
}

/// <summary>
/// The ATtiny85: the same AVR core in an 8-pin DIP, 8 KB of flash, 512 bytes of SRAM and 512 of EEPROM, five GPIO pins
/// (PB0-PB4; PB5 is RESET), Timer0 with its two PWM outputs on PB0 and PB1, the ADC on four of them, INT0 and the
/// pin-change interrupt. Its firmware is an Intel HEX file from avr-gcc (-mmcu=attiny85) or the Arduino ATTinyCore,
/// loaded the same way. It runs at 8 MHz, its internal oscillator, unless <see cref="Atmega328p.ClockFrequency"/> says
/// otherwise. Timer1 and the USI are held as registers but not run.
/// </summary>
public sealed partial class Attiny85 : Atmega328p
{
    public Attiny85() : base(layout: 2)
    {
    }

    public override string PartNumber => "ATtiny85";

    public override string ComponentType => "ATtiny85";
}
