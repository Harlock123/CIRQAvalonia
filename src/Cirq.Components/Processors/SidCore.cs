namespace Cirq.Components.Processors;

/// <summary>Which SID: the NMOS 6581 of the first C64s, or the HMOS 8580 of the C64C.</summary>
public enum SidModel
{
    /// <summary>The 6581: a non-linear filter, a mixer with a DC offset, 12 V on VDD.</summary>
    Mos6581,

    /// <summary>The 8580: a linear filter, a mixer with almost none, 9 V on VDD.</summary>
    Mos8580,
}

/// <summary>Where a voice's envelope is: rising, falling to (and holding at) the sustain level, or falling to zero.</summary>
public enum EnvelopePhase
{
    Attack,
    DecaySustain,
    Release,
}

/// <summary>
/// The digital half of a 6581 or 8580 SID, one Φ2 cycle at a time, with no circuit attached: three
/// voices — oscillator, waveform and envelope each — and the registers that set them, the filter's
/// settings, and the mix of what goes through the filter and what goes round it. The filter itself
/// is analog and lives in the circuit, on the part's capacitors (see <see cref="SidChip"/>).
/// <para>
/// Written from the published register descriptions and the long-documented behaviour of the chip,
/// not from any emulator's code.
/// </para>
/// <para>
/// <b>Oscillators.</b> Each voice adds its 16-bit frequency into a 24-bit accumulator every cycle,
/// so it runs at F × Φ2 / 2<sup>24</sup> — 0.06 Hz a step at 1 MHz. The waveforms are read from the
/// accumulator as 12-bit values: the sawtooth is its top twelve bits; the triangle is bits 22-11,
/// folded by bit 23; the pulse is all ones while the top twelve bits are at least the 12-bit pulse
/// width and zero below; the noise is eight bits of a 23-bit shift register (taps 22 and 17) that
/// shifts each time bit 19 of the accumulator rises. Selecting more than one waveform gives their
/// bitwise AND, which is the usual first approximation of what the chip does — the silicon's
/// combined waveforms are quieter and stranger than that, and noise combined with another waveform
/// does not clear the shift register as it does on the chip.
/// </para>
/// <para>
/// <b>The control bits.</b> GATE starts the attack and, cleared, the release. SYNC resets the
/// accumulator whenever the previous voice's (voice 3's, for voice 1) bit 23 rises — hard sync.
/// RING MOD replaces the triangle's fold with that bit exclusive-ored with the previous voice's bit
/// 23, which with the triangle selected is ring modulation. TEST holds the accumulator at zero, the
/// pulse high and the noise register full of ones.
/// </para>
/// <para>
/// <b>Envelopes.</b> An 8-bit level with a 15-bit rate counter: the counter counts cycles, and each
/// time it reaches the period for the rate in force (<see cref="RatePeriods"/> — 9 cycles for 2 ms
/// of attack up to 31,251 for 8 s) the level moves one step. The attack is linear, up to 255; the
/// decay and release step only every 1, 2, 4, 8, 16 or 30 periods as the level falls past 93, 54,
/// 26, 14 and 6, which is the SID's approximation of an exponential and why decay and release take
/// three times the attack's time. The decay stops where the level equals the sustain nibble × 17;
/// at zero the level holds until the next gate. The counter is compared for equality and only reset
/// on a match, so a rate change to a shorter period than the count already reached has to wait for
/// the counter to wrap through 32,768 — the "ADSR delay bug", reproduced.
/// </para>
/// <para>
/// <b>Readback.</b> $19 and $1A are the paddles, $1B voice 3's waveform (top eight bits) and $1C its
/// envelope. Every other register is write-only and reads as the last byte written to the chip,
/// which is what the data bus's capacitance gives for a while on the real one.
/// </para>
/// </summary>
public sealed class SidCore
{
    /// <summary>Cycles per envelope step for each of the sixteen rate settings.</summary>
    public static readonly int[] RatePeriods =
        [9, 32, 63, 95, 149, 220, 267, 313, 392, 977, 1954, 3126, 3907, 11720, 19532, 31251];

    /// <summary>The datasheet's attack times for the sixteen settings, in seconds; decay and release take three times as long.</summary>
    public static readonly double[] AttackTimes =
        [2e-3, 8e-3, 16e-3, 24e-3, 38e-3, 56e-3, 68e-3, 80e-3, 100e-3, 250e-3, 500e-3, 800e-3, 1, 3, 5, 8];

    public const byte Gate = 0x01, Sync = 0x02, Ring = 0x04, Test = 0x08, Triangle = 0x10, Sawtooth = 0x20, Pulse = 0x40, Noise = 0x80;

    /// <summary>One of the three voices: an oscillator, its waveform and its envelope.</summary>
    public sealed class Voice
    {
        public uint Accumulator;
        public ushort Frequency;
        public ushort PulseWidth;
        public byte Control;
        public byte AttackDecay;
        public byte SustainRelease;
        public uint ShiftRegister = 0x7FFFF8;

        public byte Envelope;
        public EnvelopePhase Phase = EnvelopePhase.Release;
        public int RateCounter;
        public int ExponentCounter;
        public int ExponentPeriod = 1;
        public bool HoldZero = true;

        /// <summary>Bit 23 rose this cycle: what syncs the next voice.</summary>
        public bool MsbRose;

        public bool Gated => (Control & SidCore.Gate) != 0;

        /// <summary>The 12-bit output of the waveforms selected, before the envelope.</summary>
        public int Waveform(Voice source)
        {
            var selected = Control & 0xF0;
            if (selected == 0) return -1;

            var value = 0xFFF;

            if ((Control & Triangle) != 0)
            {
                var fold = (Accumulator & 0x800000) != 0;
                if ((Control & Ring) != 0 && (source.Accumulator & 0x800000) != 0) fold = !fold;
                value &= (int)(((fold ? ~Accumulator : Accumulator) >> 11) & 0xFFF);
            }

            if ((Control & Sawtooth) != 0) value &= (int)(Accumulator >> 12);

            if ((Control & Pulse) != 0)
                value &= (Control & Test) != 0 || (Accumulator >> 12) >= PulseWidth ? 0xFFF : 0;

            if ((Control & SidCore.Noise) != 0)
            {
                var r = ShiftRegister;
                value &= (int)((((r >> 20) & 1) << 11) | (((r >> 18) & 1) << 10) | (((r >> 14) & 1) << 9) |
                               (((r >> 11) & 1) << 8) | (((r >> 9) & 1) << 7) | (((r >> 5) & 1) << 6) |
                               (((r >> 2) & 1) << 5) | ((r & 1) << 4));
            }

            return value;
        }

        /// <summary>What the voice puts into the mix: its waveform about mid-scale, times its envelope, from −1 to 1.</summary>
        public double Output(Voice source)
        {
            var wave = Waveform(source);
            return wave < 0 ? 0.0 : (wave - 2048) / 2048.0 * (Envelope / 255.0);
        }

        public Voice Clone() => (Voice)MemberwiseClone();
    }

    private readonly byte[] _written = new byte[0x20];
    private Voice[] _voices = [new(), new(), new()];

    public SidCore(SidModel model)
    {
        Model = model;
    }

    public SidModel Model { get; }

    public IReadOnlyList<Voice> Voices => _voices;

    /// <summary>The last byte written to any register: what a write-only register reads as.</summary>
    public byte LastWritten { get; private set; }

    /// <summary>The paddle counts, set by whoever measures the POT pins.</summary>
    public byte PotX { get; set; } = 0xFF;

    public byte PotY { get; set; } = 0xFF;

    /// <summary>What is on EXT IN, in the same units as a voice (±1 a full-scale voice).</summary>
    public double ExternalInput { get; set; }

    /// <summary>The 11-bit filter cutoff, $15 bits 0-2 and $16.</summary>
    public int Cutoff => (_written[0x15] & 0x07) | (_written[0x16] << 3);

    /// <summary>The 4-bit resonance, the top of $17.</summary>
    public int Resonance => _written[0x17] >> 4;

    /// <summary>Which inputs go through the filter: bits 0-2 the voices, bit 3 EXT IN.</summary>
    public int Routing => _written[0x17] & 0x0F;

    /// <summary>The master volume, 0 to 15.</summary>
    public int Volume => _written[0x18] & 0x0F;

    public bool LowPass => (_written[0x18] & 0x10) != 0;

    public bool BandPass => (_written[0x18] & 0x20) != 0;

    public bool HighPass => (_written[0x18] & 0x40) != 0;

    /// <summary>Voice 3 left out of the mix, unless it goes through the filter.</summary>
    public bool Voice3Off => (_written[0x18] & 0x80) != 0;

    /// <summary>The mix of what does not go through the filter, after the last cycle, in voice units.</summary>
    public double Direct { get; private set; }

    /// <summary>The mix of what does, in voice units.</summary>
    public double FilterInput { get; private set; }

    public long Cycles { get; private set; }

    /// <summary>The voice whose bit 23 syncs and ring-modulates voice <paramref name="index"/>: the one before it.</summary>
    public Voice SourceOf(int index) => _voices[(index + 2) % 3];

    /// <summary>A register as last written; the read-only ones are what a read gives.</summary>
    public byte Register(int index) => index switch
    {
        0x19 or 0x1A or 0x1B or 0x1C => Read(index),
        _ => _written[index & 0x1F],
    };

    public byte Read(int register) => (register & 0x1F) switch
    {
        0x19 => PotX,
        0x1A => PotY,
        0x1B => (byte)Math.Max(_voices[2].Waveform(_voices[1]) >> 4, 0),
        0x1C => _voices[2].Envelope,
        _ => LastWritten,
    };

    public void Write(int register, byte value)
    {
        register &= 0x1F;
        LastWritten = value;
        if (register > 0x18) return;
        _written[register] = value;

        if (register < 0x15)
        {
            var voice = _voices[register / 7];
            switch (register % 7)
            {
                case 0:
                    voice.Frequency = (ushort)((voice.Frequency & 0xFF00) | value);
                    break;
                case 1:
                    voice.Frequency = (ushort)((voice.Frequency & 0x00FF) | (value << 8));
                    break;
                case 2:
                    voice.PulseWidth = (ushort)((voice.PulseWidth & 0x0F00) | value);
                    break;
                case 3:
                    voice.PulseWidth = (ushort)((voice.PulseWidth & 0x00FF) | ((value & 0x0F) << 8));
                    break;
                case 4:
                    var gateWas = voice.Gated;
                    voice.Control = value;
                    if (!gateWas && voice.Gated)
                    {
                        voice.Phase = EnvelopePhase.Attack;
                        voice.HoldZero = false;
                    }
                    else if (gateWas && !voice.Gated)
                    {
                        voice.Phase = EnvelopePhase.Release;
                    }

                    if ((value & Test) != 0)
                    {
                        voice.Accumulator = 0;
                        voice.ShiftRegister = 0x7FFFFF;
                    }

                    break;
                case 5:
                    voice.AttackDecay = value;
                    break;
                default:
                    voice.SustainRelease = value;
                    break;
            }
        }
    }

    /// <summary>One Φ2 cycle: the oscillators, hard sync, the envelopes, and the two mixes.</summary>
    public void Clock()
    {
        Cycles++;

        foreach (var v in _voices)
        {
            if ((v.Control & Test) != 0)
            {
                v.Accumulator = 0;
                v.MsbRose = false;
                continue;
            }

            var before = v.Accumulator;
            v.Accumulator = (v.Accumulator + v.Frequency) & 0xFFFFFF;
            v.MsbRose = (before & 0x800000) == 0 && (v.Accumulator & 0x800000) != 0;

            // The noise register shifts as bit 19 rises: feedback from taps 22 and 17.
            if ((before & 0x080000) == 0 && (v.Accumulator & 0x080000) != 0)
            {
                var bit = ((v.ShiftRegister >> 22) ^ (v.ShiftRegister >> 17)) & 1;
                v.ShiftRegister = ((v.ShiftRegister << 1) | bit) & 0x7FFFFF;
            }
        }

        for (var i = 0; i < 3; i++)
            if ((_voices[i].Control & Sync) != 0 && SourceOf(i).MsbRose) _voices[i].Accumulator = 0;

        foreach (var v in _voices) Envelope(v);

        var direct = 0.0;
        var filtered = 0.0;
        for (var i = 0; i < 3; i++)
        {
            var output = _voices[i].Output(SourceOf(i));
            if ((Routing & (1 << i)) != 0) filtered += output;
            else if (i != 2 || !Voice3Off) direct += output;
        }

        if ((Routing & 0x08) != 0) filtered += ExternalInput;
        else direct += ExternalInput;

        Direct = direct;
        FilterInput = filtered;
    }

    private static void Envelope(Voice v)
    {
        var period = RatePeriods[v.Phase switch
        {
            EnvelopePhase.Attack => v.AttackDecay >> 4,
            EnvelopePhase.DecaySustain => v.AttackDecay & 0x0F,
            _ => v.SustainRelease & 0x0F,
        }];

        // Equality, not "at least": a period shorter than the count so far waits for the wrap.
        v.RateCounter = (v.RateCounter + 1) & 0x7FFF;
        if (v.RateCounter != period) return;
        v.RateCounter = 0;

        if (v.Phase == EnvelopePhase.Attack)
        {
            v.ExponentCounter = 0;
            if (v.Envelope < 0xFF) v.Envelope++;
            if (v.Envelope == 0xFF) v.Phase = EnvelopePhase.DecaySustain;
        }
        else
        {
            if (v.HoldZero || ++v.ExponentCounter < v.ExponentPeriod) return;
            v.ExponentCounter = 0;

            if (v.Phase == EnvelopePhase.DecaySustain && v.Envelope == (v.SustainRelease >> 4) * 0x11) return;
            v.Envelope--;
        }

        v.ExponentPeriod = v.Envelope switch
        {
            0xFF => 1,
            0x5D => 2,
            0x36 => 4,
            0x1A => 8,
            0x0E => 16,
            0x06 => 30,
            0x00 => 1,
            _ => v.ExponentPeriod,
        };

        if (v.Envelope == 0 && v.Phase != EnvelopePhase.Attack) v.HoldZero = true;
    }

    /// <summary>The filter's cutoff for the 11-bit setting, in hertz, with the datasheet capacitors (<see cref="SidChip"/>).</summary>
    /// <remarks>
    /// The 8580's is close to a straight line, 30 Hz to 12 kHz. The 6581's is not, and varies from
    /// chip to chip: it barely moves over the bottom of the range and then climbs steeply; this is a
    /// smooth exponential through about 220 Hz at zero, 2 kHz at half way and 12 kHz at the top.
    /// </remarks>
    public static double CutoffFrequency(SidModel model, int cutoff)
    {
        var x = Math.Clamp(cutoff, 0, 2047) / 2047.0;
        return model == SidModel.Mos8580
            ? 30.0 + ((12000.0 - 30.0) * x)
            : 220.0 + (12000.0 - 220.0) * (Math.Exp(3.5 * x) - 1) / (Math.Exp(3.5) - 1);
    }

    /// <summary>The filter's Q for the 4-bit resonance: 0.71 at zero, up to about 2 (6581) or 2.5 (8580) at fifteen.</summary>
    public static double QualityFactor(SidModel model, int resonance) =>
        0.707 + (Math.Clamp(resonance, 0, 15) / 15.0 * (model == SidModel.Mos8580 ? 1.8 : 1.3));

    /// <summary>A copy, for a run to hand back.</summary>
    public SidCore Clone()
    {
        var copy = new SidCore(Model) { PotX = PotX, PotY = PotY, ExternalInput = ExternalInput };
        Array.Copy(_written, copy._written, _written.Length);
        copy._voices = [.. _voices.Select(v => v.Clone())];
        copy.LastWritten = LastWritten;
        copy.Direct = Direct;
        copy.FilterInput = FilterInput;
        copy.Cycles = Cycles;
        return copy;
    }
}
