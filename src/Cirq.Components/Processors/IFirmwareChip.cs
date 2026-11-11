using Cirq.Components.Digital;

namespace Cirq.Components.Processors;

/// <summary>A microcontroller whose program is a firmware image loaded into its own flash: the ATmega328P (and the ATtiny85 and the Uno built on it) and the AT89C2051.</summary>
public interface IFirmwareChip
{
    string Name { get; }

    string PartNumber { get; }

    /// <summary>Writes the image into the flash and keeps it with the circuit; says how much it was.</summary>
    string Load(MemoryImage image);
}
