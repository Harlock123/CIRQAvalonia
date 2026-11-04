using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A 2K × 8 static memory of the 6116 sort: eleven address lines, eight tri-state data lines, and
/// three control pins.
/// <para>
/// This is the part that turns the logic in the palette into a <b>system</b>. A counter on the
/// address lines and a transceiver on the data lines is, in the sense that matters for
/// understanding one, a computer: something walks through addresses, something else answers with
/// what is stored there, and the bus carries the answers one at a time. Until the 74245 arrived
/// there was no way to build that here, because there was no output that could let go of a wire.
/// </para>
/// <para>
/// Everything it does — the contents, the controls, the timing — is <see cref="ParallelMemory"/>'s;
/// this is the 2K part that came first, kept under its own name so circuits saved with it open
/// unchanged. The <see cref="Sram6116"/> is the same chip under its part number.
/// </para>
/// <para>
/// Set <see cref="IsReadOnly"/> and it is a ROM: writes are ignored rather than obeyed, which is
/// the difference between a part that holds a program and one that holds a variable.
/// </para>
/// <para>
/// Pinout follows the 6116: 1-8 = A7-A0, 9-11 and 13-17 = the data lines, 12 = GND, 18 = /CE,
/// 19 = A10, 20 = /OE, 21 = /WE, 22 = A9, 23 = A8, 24 = VCC. All three controls are active low.
/// </para>
/// </summary>
public sealed partial class MemoryDevice : ParallelMemory
{
    /// <summary>Address lines, and so the size: eleven of them is two kilobytes.</summary>
    public const int AddressLines = 11;

    /// <summary>How many bytes it holds.</summary>
    public const int Capacity = 1 << AddressLines;

    public MemoryDevice() : base(24, AddressLines, MemoryPinout.Sram6116)
    {
        PropagationDelay = 70e-9;
    }

    /// <summary>The write enable, which this part always has.</summary>
    public Terminal WriteEnable => WriteEnablePin!;

    /// <summary>When true, writes are ignored — which makes it a ROM rather than a RAM.</summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    public override bool AcceptsWrites => !IsReadOnly;

    public override string PartNumber => IsReadOnly ? "ROM 2Kx8" : "SRAM 2Kx8";

    public override string ComponentType => IsReadOnly ? "ROM" : "SRAM";

    partial void OnIsReadOnlyChanged(bool value) => NotifyValueChanged();
}
