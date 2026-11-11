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
