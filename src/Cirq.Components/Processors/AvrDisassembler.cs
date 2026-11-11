using System.Globalization;

namespace Cirq.Components.Processors;

/// <summary>
/// The AVR instruction set read back as assembly, for the ATmega328P's debugger: in the syntax avr-as takes, the aliases
/// it prints (CLR, LSL, ROL, TST, SER, BREQ and the other branches), jump and branch targets as word addresses
/// (<c>rjmp 0x0016</c>), I/O addresses as I/O space. A word that is no instruction comes back as <c>.dw</c>.
/// </summary>
public static class AvrDisassembler
{
    /// <summary>The instruction at word <paramref name="pc"/> of <paramref name="flash"/>, and how many words it takes.</summary>
    public static (string Text, int Words) Disassemble(IReadOnlyList<ushort> flash, int pc)
    {
        ArgumentNullException.ThrowIfNull(flash);
        var mask = flash.Count - 1;
        var op = flash[pc & mask];
        var next = flash[(pc + 1) & mask];
        var d5 = (op >> 4) & 0x1F;
        var r5 = (op & 0x0F) | ((op >> 5) & 0x10);
        var d4 = 16 + ((op >> 4) & 0x0F);
        var k8 = ((op >> 4) & 0xF0) | (op & 0x0F);
        string H(int v, int digits = 2) => "0x" + v.ToString(digits == 4 ? "X4" : "X2", CultureInfo.InvariantCulture);
        string W(int target) => H(target & mask, 4);

        switch (op)
        {
            case 0x0000: return ("nop", 1);
            case 0x9409: return ("ijmp", 1);
            case 0x9509: return ("icall", 1);
            case 0x9508: return ("ret", 1);
            case 0x9518: return ("reti", 1);
            case 0x9588: return ("sleep", 1);
            case 0x95A8: return ("wdr", 1);
            case 0x9598: return ("break", 1);
            case 0x95C8: return ("lpm", 1);
            case 0x95E8: return ("spm", 1);
            case 0x9478: return ("sei", 1);
            case 0x94F8: return ("cli", 1);
            case 0x9408: return ("sec", 1);
            case 0x9488: return ("clc", 1);
            case 0x9468: return ("set", 1);
            case 0x94E8: return ("clt", 1);
            case 0x9428: return ("sen", 1);
            case 0x94A8: return ("cln", 1);
            case 0x9418: return ("sez", 1);
            case 0x9498: return ("clz", 1);
            case 0x9438: return ("sev", 1);
            case 0x94B8: return ("clv", 1);
            case 0x9448: return ("ses", 1);
            case 0x94C8: return ("cls", 1);
            case 0x9458: return ("seh", 1);
            case 0x94D8: return ("clh", 1);
        }

        string Rr(string name) => $"{name} r{d5}, r{r5}";
        switch (op & 0xFC00)
        {
            case 0x0400: return (Rr("cpc"), 1);
            case 0x0800: return (Rr("sbc"), 1);
            case 0x0C00: return (d5 == r5 ? $"lsl r{d5}" : Rr("add"), 1);
            case 0x1000: return (Rr("cpse"), 1);
            case 0x1400: return (Rr("cp"), 1);
            case 0x1800: return (Rr("sub"), 1);
            case 0x1C00: return (d5 == r5 ? $"rol r{d5}" : Rr("adc"), 1);
            case 0x2000: return (d5 == r5 ? $"tst r{d5}" : Rr("and"), 1);
            case 0x2400: return (d5 == r5 ? $"clr r{d5}" : Rr("eor"), 1);
            case 0x2800: return (Rr("or"), 1);
            case 0x2C00: return (Rr("mov"), 1);
            case 0x9C00: return (Rr("mul"), 1);
        }

        switch (op & 0xFF00)
        {
            case 0x0100: return ($"movw r{((op >> 4) & 0xF) * 2}, r{(op & 0xF) * 2}", 1);
            case 0x0200: return ($"muls r{16 + ((op >> 4) & 0xF)}, r{16 + (op & 0xF)}", 1);
            case 0x9600: return ($"adiw r{24 + (((op >> 4) & 3) * 2)}, {((op >> 2) & 0x30) | (op & 0xF)}", 1);
            case 0x9700: return ($"sbiw r{24 + (((op >> 4) & 3) * 2)}, {((op >> 2) & 0x30) | (op & 0xF)}", 1);
            case 0x9800: return ($"cbi {H((op >> 3) & 0x1F)}, {op & 7}", 1);
            case 0x9900: return ($"sbic {H((op >> 3) & 0x1F)}, {op & 7}", 1);
            case 0x9A00: return ($"sbi {H((op >> 3) & 0x1F)}, {op & 7}", 1);
            case 0x9B00: return ($"sbis {H((op >> 3) & 0x1F)}, {op & 7}", 1);
        }

        switch (op & 0xF000)
        {
            case 0x3000: return ($"cpi r{d4}, {H(k8)}", 1);
            case 0x4000: return ($"sbci r{d4}, {H(k8)}", 1);
            case 0x5000: return ($"subi r{d4}, {H(k8)}", 1);
            case 0x6000: return ($"ori r{d4}, {H(k8)}", 1);
            case 0x7000: return ($"andi r{d4}, {H(k8)}", 1);
            case 0xC000: return ($"rjmp {W(pc + 1 + Signed(op & 0xFFF, 12))}", 1);
            case 0xD000: return ($"rcall {W(pc + 1 + Signed(op & 0xFFF, 12))}", 1);
            case 0xE000: return (k8 == 0xFF ? $"ser r{d4}" : $"ldi r{d4}, {H(k8)}", 1);
        }

        if ((op & 0xF800) == 0xB000) return ($"in r{d5}, {H((op & 0xF) | ((op >> 5) & 0x30))}", 1);
        if ((op & 0xF800) == 0xB800) return ($"out {H((op & 0xF) | ((op >> 5) & 0x30))}, r{d5}", 1);

        // Branches on a flag, by their aliases.
        if ((op & 0xF800) is 0xF000 or 0xF400)
        {
            var set = (op & 0x0400) == 0;
            var bit = op & 7;
            string[] taken = ["brcs", "breq", "brmi", "brvs", "brlt", "brhs", "brts", "brie"];
            string[] clear = ["brcc", "brne", "brpl", "brvc", "brge", "brhc", "brtc", "brid"];
            return ($"{(set ? taken : clear)[bit]} {W(pc + 1 + Signed((op >> 3) & 0x7F, 7))}", 1);
        }

        switch (op & 0xFE08)
        {
            case 0xF800: return ($"bld r{d5}, {op & 7}", 1);
            case 0xFA00: return ($"bst r{d5}, {op & 7}", 1);
            case 0xFC00: return ($"sbrc r{d5}, {op & 7}", 1);
            case 0xFE00: return ($"sbrs r{d5}, {op & 7}", 1);
        }

        switch (op & 0xFE0F)
        {
            case 0x9400: return ($"com r{d5}", 1);
            case 0x9401: return ($"neg r{d5}", 1);
            case 0x9402: return ($"swap r{d5}", 1);
            case 0x9403: return ($"inc r{d5}", 1);
            case 0x9405: return ($"asr r{d5}", 1);
            case 0x9406: return ($"lsr r{d5}", 1);
            case 0x9407: return ($"ror r{d5}", 1);
            case 0x940A: return ($"dec r{d5}", 1);
            case 0x920F: return ($"push r{d5}", 1);
            case 0x900F: return ($"pop r{d5}", 1);
            case 0x9000: return ($"lds r{d5}, {H(next, 4)}", 2);
            case 0x9200: return ($"sts {H(next, 4)}, r{d5}", 2);
            case 0x9004: return ($"lpm r{d5}, Z", 1);
            case 0x9005: return ($"lpm r{d5}, Z+", 1);
        }

        if ((op & 0xFE0E) is 0x940C or 0x940E)
        {
            var target = (((op >> 3) & 0x3E) | (op & 1)) << 16 | next;
            return ($"{((op & 0xFE0E) == 0x940C ? "jmp" : "call")} {W(target)}", 2);
        }

        // Loads and stores through X, Y and Z: plain, post-increment, pre-decrement, and with a displacement.
        if ((op & 0xFC00) == 0x9000 && (op & 0x0F) is 0xC or 0xD or 0xE or 0x9 or 0xA or 0x1 or 0x2)
        {
            var store = (op & 0x0200) != 0;
            var pointer = (op & 0x0F) switch { 0xC => "X", 0xD => "X+", 0xE => "-X", 0x9 => "Y+", 0xA => "-Y", 0x1 => "Z+", _ => "-Z" };
            return (store ? $"st {pointer}, r{d5}" : $"ld r{d5}, {pointer}", 1);
        }

        if ((op & 0xD000) == 0x8000)
        {
            var q = (op & 0x7) | ((op >> 7) & 0x18) | ((op >> 8) & 0x20);
            var y = (op & 0x08) != 0;
            var store = (op & 0x0200) != 0;
            var at = q == 0 ? (y ? "Y" : "Z") : $"{(y ? "Y" : "Z")}+{q}";
            return (q == 0 ? store ? $"st {at}, r{d5}" : $"ld r{d5}, {at}" : store ? $"std {at}, r{d5}" : $"ldd r{d5}, {at}", 1);
        }

        return ($".dw {H(op, 4)}", 1);
    }

    private static int Signed(int value, int bits) => (value & (1 << (bits - 1))) != 0 ? value - (1 << bits) : value;
}
