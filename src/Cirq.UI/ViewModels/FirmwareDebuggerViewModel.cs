using System.Globalization;
using System.Text;
using Cirq.Components.Processors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One line of the debugger's listing: an instruction's byte address, its text, whether the PC is there and whether it breaks there.</summary>
public sealed record DisassemblyLine(int Address, string Text, bool IsPc, bool HasBreakpoint)
{
    public override string ToString() =>
        $"{(HasBreakpoint ? "●" : " ")}{(IsPc ? "▶" : " ")} {Address.ToString("X4", CultureInfo.InvariantCulture)}  {Text}";
}

/// <summary>
/// The firmware debugger for an ATmega328P (or an Arduino Uno): breakpoints, a single step, and the chip laid open (its
/// registers, status flags, stack pointer and I/O, and the instructions round the program counter) while the circuit
/// round it keeps its state.
/// <para>
/// A breakpoint stops the firmware before the instruction at its address runs, and the run pauses with it; Continue
/// runs on from there, Step runs one instruction (or takes one interrupt) and stops again. The circuit's time stops with
/// the run, so what the pins show is what they were when it stopped.
/// </para>
/// </summary>
public sealed partial class FirmwareDebuggerViewModel(MainWindowViewModel main, Atmega328p chip) : ObservableObject
{
    public Atmega328p Chip { get; } = chip;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<DisassemblyLine> Lines { get; set; } = [];

    [ObservableProperty]
    public partial string Registers { get; set; } = "";

    [ObservableProperty]
    public partial string Io { get; set; } = "";

    /// <summary>What's typed into the breakpoint box: a byte address in hex, e.g. 0x0034 or $34.</summary>
    [ObservableProperty]
    public partial string BreakpointText { get; set; } = "";

    public string Title => $"Firmware Debugger: {Chip.Name} ({Chip.PartNumber})";

    /// <summary>Reads everything again from the chip.</summary>
    public void Refresh()
    {
        var core = Chip.Core;
        var pc = core.Pc;
        Status = Chip.Halted
            ? $"Stopped at ${pc * 2:X4} ({Chip.HaltReason}). Step runs one instruction; Continue runs on."
            : main.Simulation.IsRunning ? "Running." : "Not running: Continue (or F5) runs it, Step runs one instruction.";
        var lines = Listing(pc);
        if (!lines.SequenceEqual(Lines)) Lines = lines;
        Registers = RegisterText(core);
        Io = IoText(core);
    }

    /// <summary>The instructions round the PC: a few before it, as decoded from the start of flash so they line up, and more after.</summary>
    private List<DisassemblyLine> Listing(int pc)
    {
        var flash = Chip.Core.State.Flash;
        var starts = new List<int>();
        for (var at = 0; at < pc && at < flash.Length;) { starts.Add(at); at += AvrDisassembler.Disassemble(flash, at).Words; }
        var from = starts.Count > 6 ? starts[^6] : 0;
        var lines = new List<DisassemblyLine>();
        for (var at = from; lines.Count < 24 && at < flash.Length;)
        {
            var (text, words) = AvrDisassembler.Disassemble(flash, at);
            lines.Add(new DisassemblyLine(at * 2, text, at == pc, Chip.Breakpoints.Contains(at * 2)));
            at += words;
        }

        return lines;
    }

    private static string RegisterText(AvrCore core)
    {
        var text = new StringBuilder();
        var sreg = core.StatusRegister;
        var flags = string.Concat("ITHSVNZC".Select((f, i) => (sreg & (0x80 >> i)) != 0 ? f : char.ToLowerInvariant(f)));
        text.Append(CultureInfo.InvariantCulture, $"PC ${core.Pc * 2:X4}   SP ${core.StackPointer:X4}   SREG {flags}\n");
        text.Append(CultureInfo.InvariantCulture, $"cycles {core.State.Cycles:N0}{(core.State.Sleeping ? "   asleep" : "")}\n\n");
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var r = (col * 8) + row;
                text.Append(CultureInfo.InvariantCulture, $"r{r,-2} {core[r]:X2}   ");
            }

            text.Append('\n');
        }

        int Pair(int low) => core[low] | (core[low + 1] << 8);
        text.Append(CultureInfo.InvariantCulture, $"\nX ${Pair(26):X4}   Y ${Pair(28):X4}   Z ${Pair(30):X4}");
        return text.ToString();
    }

    private static string IoText(AvrCore core)
    {
        var text = new StringBuilder();
        foreach (var (name, pin, ddr, port) in new[] { ("B", AvrCore.PINB, AvrCore.DDRB, AvrCore.PORTB), ("C", AvrCore.PINC, AvrCore.DDRC, AvrCore.PORTC), ("D", AvrCore.PIND, AvrCore.DDRD, AvrCore.PORTD) })
            text.Append(CultureInfo.InvariantCulture, $"PORT{name} {Bits(core[port])}  DDR{name} {Bits(core[ddr])}  PIN{name} {Bits(core.PinInputs[name == "B" ? 0 : name == "C" ? 1 : 2])}\n");
        text.Append(CultureInfo.InvariantCulture, $"\nTCNT0 {core[AvrCore.TCNT0]:X2}  TCNT1 {core[AvrCore.TCNT1L] | (core[AvrCore.TCNT1H] << 8):X4}  TCNT2 {core[AvrCore.TCNT2]:X2}\n");
        text.Append(CultureInfo.InvariantCulture, $"OCR1A {core[AvrCore.OCR1AL] | (core[AvrCore.OCR1AH] << 8):X4}  TIMSK1 {core[AvrCore.TIMSK1]:X2}  TIFR1 {core[AvrCore.TIFR1]:X2}\n");
        text.Append(CultureInfo.InvariantCulture, $"UCSR0A {core[AvrCore.UCSR0A]:X2}  UCSR0B {core[AvrCore.UCSR0B]:X2}  UDR0 {core[AvrCore.UDR0]:X2}\n");
        text.Append(CultureInfo.InvariantCulture, $"SPCR {core[AvrCore.SPCR]:X2}  SPSR {core[AvrCore.SPSR]:X2}  SPDR {core[AvrCore.SPDR]:X2}\n");
        text.Append(CultureInfo.InvariantCulture, $"TWCR {core[AvrCore.TWCR]:X2}  TWSR {core[AvrCore.TWSR]:X2}  TWDR {core[AvrCore.TWDR]:X2}\n");
        text.Append(CultureInfo.InvariantCulture, $"ADMUX {core[AvrCore.ADMUX]:X2}  ADCSRA {core[AvrCore.ADCSRA]:X2}  ADC {core[AvrCore.ADCL] | (core[AvrCore.ADCH] << 8)}");
        return text.ToString();

        static string Bits(byte b) => Convert.ToString(b, 2).PadLeft(8, '0');
    }

    [RelayCommand]
    private void Continue()
    {
        if (main.Simulation.IsRunning) Chip.Resume();
        else main.Simulation.Play();
        Refresh();
    }

    [RelayCommand]
    private void Pause()
    {
        Chip.Break();
        Refresh();
    }

    /// <summary>One instruction, then stopped again: the run goes just long enough for it.</summary>
    [RelayCommand]
    private void Step()
    {
        Chip.StepInstruction();
        if (!main.Simulation.IsRunning) main.Simulation.Play();
        Refresh();
    }

    [RelayCommand]
    private void AddBreakpoint()
    {
        if (Parse(BreakpointText) is not { } address)
        {
            Status = $"\"{BreakpointText}\" isn't an address: give a byte address in hex, as the listing shows it (0x0034, $34 or 34).";
            return;
        }

        Chip.Breakpoints.Add(address);
        BreakpointText = "";
        Refresh();
    }

    /// <summary>Breaks at an instruction, or stops breaking there.</summary>
    public void ToggleBreakpoint(int address)
    {
        if (!Chip.Breakpoints.Remove(address)) Chip.Breakpoints.Add(address);
        Refresh();
    }

    [RelayCommand]
    private void ClearBreakpoints()
    {
        Chip.Breakpoints.Clear();
        Refresh();
    }

    /// <summary>A byte address in hex (0x34, $34, 34), made even: instructions start on a word.</summary>
    public static int? Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        else if (t.StartsWith('$')) t = t[1..];
        return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < AvrCore.FlashWords * 2 ? v & ~1 : null;
    }
}
