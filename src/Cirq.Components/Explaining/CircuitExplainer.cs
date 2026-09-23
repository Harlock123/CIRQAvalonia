using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Core.Units;

namespace Cirq.Components.Explaining;

/// <summary>One thing the explainer recognised.</summary>
/// <param name="Headline">What it is — "R1 and R2 are a divider".</param>
/// <param name="Detail">What that means here, with the numbers worked out.</param>
/// <param name="Parts">The parts involved, so the drawing can light them up.</param>
public sealed record Explanation(string Headline, string Detail, IReadOnlyList<CircuitComponent> Parts)
{
    public string Designators => string.Join(", ", Parts.Select(p => p.Name).Order());
}

/// <summary>
/// Reads a schematic and says what it recognises.
/// <para>
/// A netlist says what is joined to what. A person reading the same drawing sees <i>a divider
/// setting 3.3 V</i>, <i>a low-pass turning over at 1.6 kHz</i>, <i>a follower</i> — structures,
/// each with a number that follows from it. That translation is most of what "knowing how to read
/// a schematic" is, and it is the part a simulator normally leaves entirely to the reader.
/// </para>
/// <para>
/// Each recogniser is deliberately narrow and says nothing when it is not sure. An explainer that
/// guesses is worse than none: a confident wrong description of a circuit is the one thing that
/// could teach somebody something false, and they would have no way to tell. Everything here is
/// either a structure that can be pattern-matched exactly or it is left alone.
/// </para>
/// </summary>
public static class CircuitExplainer
{
    /// <summary>Everything recognised in a circuit, in a stable order.</summary>
    public static IReadOnlyList<Explanation> Explain(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        var sheet = new Sheet(circuit);

        List<Explanation> found =
        [
            .. Dividers(sheet),
            .. Filters(sheet),
            .. Resonators(sheet),
            .. Decoupling(sheet),
            .. Pulls(sheet),
            .. LedResistors(sheet),
            .. OpAmpStages(sheet),
            .. Followers(sheet),
            .. Flybacks(sheet),
            .. Astables(sheet),
        ];

        return [.. found.OrderBy(e => e.Designators, StringComparer.OrdinalIgnoreCase)];
    }

    // ---- dividers ----------------------------------------------------------

    /// <summary>
    /// Two resistors in series from something to ground, with a tap between them. The most
    /// common structure on any schematic and the one people most often want the number for.
    /// </summary>
    private static IEnumerable<Explanation> Dividers(Sheet sheet)
    {
        foreach (var net in sheet.Nets)
        {
            if (net.IsGround) continue;

            var resistors = sheet.On<Resistor>(net).ToList();
            if (resistors.Count != 2) continue;

            // Nothing else may be loading the tap, or the ratio is not the ratio.
            if (sheet.Count(net) != 2) continue;

            var (top, bottom) = (resistors[0], resistors[1]);

            var topFar = sheet.Other(top, net);
            var bottomFar = sheet.Other(bottom, net);

            if (topFar is null || bottomFar is null) continue;

            // One leg to ground and the other to something that is not.
            if (sheet.IsGround(bottomFar) == sheet.IsGround(topFar)) continue;

            if (sheet.IsGround(topFar)) (top, bottom, topFar) = (bottom, top, bottomFar);

            var ratio = bottom.Resistance / (top.Resistance + bottom.Resistance);

            var supply = sheet.RailVoltage(topFar);

            var detail = supply is { } volts
                ? $"the tap sits at {ratio:0.###} of {SiPrefix.Format(volts, "V")}, which is " +
                  $"{SiPrefix.Format(volts * ratio, "V")} — with nothing drawing from it"
                : $"the tap sits at {ratio:0.###} of whatever is on {topFar.Name} — with nothing " +
                  "drawing from it";

            yield return new Explanation(
                $"{top.Name} and {bottom.Name} are a divider", detail, [top, bottom]);
        }
    }

    // ---- filters -----------------------------------------------------------

    /// <summary>
    /// A resistor and a capacitor meeting at a net, with the capacitor's other end on ground: a
    /// low-pass. The other way round — the capacitor in series and the resistor to ground — is a
    /// high-pass. Both turn over at the same 1/2πRC, which is the point worth making.
    /// </summary>
    private static IEnumerable<Explanation> Filters(Sheet sheet)
    {
        foreach (var net in sheet.Nets)
        {
            if (net.IsGround) continue;

            var resistors = sheet.On<Resistor>(net).ToList();
            var capacitors = sheet.On<Capacitor>(net).Where(c => c is not ElectrolyticCapacitor).ToList();

            if (resistors.Count != 1 || capacitors.Count != 1) continue;

            var resistor = resistors[0];
            var capacitor = capacitors[0];

            var capacitorFar = sheet.Other(capacitor, net);
            var resistorFar = sheet.Other(resistor, net);

            if (capacitorFar is null || resistorFar is null) continue;

            var corner = 1.0 / (2.0 * Math.PI * resistor.Resistance * capacitor.Capacitance);

            if (sheet.IsGround(capacitorFar) && !sheet.IsGround(resistorFar))
            {
                yield return new Explanation(
                    $"{resistor.Name} and {capacitor.Name} are a low-pass",
                    $"it turns over at {SiPrefix.Format(corner, "Hz")} — above that the capacitor " +
                    "is the lower impedance and the signal goes to ground instead of onward",
                    [resistor, capacitor]);

                continue;
            }

            if (sheet.IsGround(resistorFar) && !sheet.IsGround(capacitorFar))
            {
                yield return new Explanation(
                    $"{capacitor.Name} and {resistor.Name} are a high-pass",
                    $"it turns over at {SiPrefix.Format(corner, "Hz")} — below that the capacitor " +
                    "is the higher impedance and blocks, which is also what makes it AC coupling",
                    [capacitor, resistor]);
            }
        }
    }

    // ---- tuned circuits ----------------------------------------------------

    private static IEnumerable<Explanation> Resonators(Sheet sheet)
    {
        foreach (var net in sheet.Nets)
        {
            if (net.IsGround) continue;

            var inductors = sheet.On<Inductor>(net).ToList();
            var capacitors = sheet.On<Capacitor>(net).ToList();

            if (inductors.Count != 1 || capacitors.Count != 1) continue;

            var inductor = inductors[0];
            var capacitor = capacitors[0];

            var product = inductor.Inductance * capacitor.Capacitance;
            if (product <= 0) continue;

            var resonance = 1.0 / (2.0 * Math.PI * Math.Sqrt(product));

            var inductorFar = sheet.Other(inductor, net);
            var capacitorFar = sheet.Other(capacitor, net);

            // Both other ends on the same net is a parallel tank; otherwise they are in series.
            var parallel = inductorFar is not null && capacitorFar is not null
                           && inductorFar.Index == capacitorFar.Index;

            yield return new Explanation(
                $"{inductor.Name} and {capacitor.Name} are a tuned circuit",
                parallel
                    ? $"in parallel, so the impedance peaks at {SiPrefix.Format(resonance, "Hz")} " +
                      "— it passes everything except that"
                    : $"in series, so the impedance dips at {SiPrefix.Format(resonance, "Hz")} " +
                      "— it passes that and little else",
                [inductor, capacitor]);
        }
    }

    // ---- decoupling --------------------------------------------------------

    private static IEnumerable<Explanation> Decoupling(Sheet sheet)
    {
        foreach (var capacitor in sheet.All<Capacitor>())
        {
            var a = sheet.NetOf(capacitor.A);
            var b = sheet.NetOf(capacitor.B);

            if (a is null || b is null) continue;

            var rail = a.IsGround ? b : b.IsGround ? a : null;
            if (rail is null) continue;

            if (sheet.RailVoltage(rail) is not { } volts) continue;

            yield return new Explanation(
                $"{capacitor.Name} decouples {rail.Name}",
                $"straight across {SiPrefix.Format(volts, "V")} and ground, so it holds the rail " +
                "up through whatever the parts on it ask for faster than the supply can answer",
                [capacitor]);
        }
    }

    // ---- pull-ups ----------------------------------------------------------

    private static IEnumerable<Explanation> Pulls(Sheet sheet)
    {
        foreach (var resistor in sheet.All<Resistor>())
        {
            var a = sheet.NetOf(resistor.A);
            var b = sheet.NetOf(resistor.B);

            if (a is null || b is null) continue;

            // One end on a rail or on ground, the other on a net with something switching on it.
            var (railNet, signal) = sheet.RailVoltage(a) is not null || a.IsGround
                ? (a, b)
                : sheet.RailVoltage(b) is not null || b.IsGround ? (b, a) : (null, null);

            if (railNet is null || signal is null) continue;

            // Only when the signal net has a switch or an open-collector-ish part on it, or the
            // resistor would just be a load.
            if (!sheet.HasSwitching(signal)) continue;

            var up = !railNet.IsGround;

            yield return new Explanation(
                $"{resistor.Name} is a pull-{(up ? "up" : "down")} on {signal.Name}",
                up
                    ? "it holds the net high when nothing else is driving it, so a switch only " +
                      "ever has to pull it down"
                    : "it holds the net low when nothing else is driving it, so a floating input " +
                      "cannot pick up whatever is nearby",
                [resistor]);
        }
    }

    // ---- LEDs --------------------------------------------------------------

    private static IEnumerable<Explanation> LedResistors(Sheet sheet)
    {
        foreach (var led in sheet.All<Led>())
        {
            foreach (var pin in (Terminal[])[led.Anode, led.Cathode])
            {
                var net = sheet.NetOf(pin);
                if (net is null || net.IsGround) continue;

                var resistors = sheet.On<Resistor>(net).ToList();
                if (resistors.Count != 1 || sheet.Count(net) != 2) continue;

                var resistor = resistors[0];
                var far = sheet.Other(resistor, net);

                if (far is null || sheet.RailVoltage(far) is not { } volts) continue;

                var other = sheet.NetOf(pin == led.Anode ? led.Cathode : led.Anode);
                if (other is null || !other.IsGround) continue;

                // The drop across a lit LED, near enough. The exact figure comes out of a solve;
                // this is the number somebody does on the back of an envelope, and saying which
                // it is matters more than the third digit.
                var drop = led.Model.ForwardVoltageAt(0.01);
                var current = (Math.Abs(volts) - drop) / resistor.Resistance;

                if (current <= 0) continue;

                yield return new Explanation(
                    $"{resistor.Name} sets {led.Name}'s current",
                    $"about {SiPrefix.Format(current, "A")}, from " +
                    $"({SiPrefix.Format(Math.Abs(volts), "V")} − {drop:0.#} V) ÷ " +
                    $"{SiPrefix.Format(resistor.Resistance, "Ω")}",
                    [resistor, led]);
            }
        }
    }

    // ---- op-amps -----------------------------------------------------------

    private static IEnumerable<Explanation> OpAmpStages(Sheet sheet)
    {
        foreach (var amp in sheet.All<OperationalAmplifier>())
        {
            var output = sheet.NetOf(amp.Output);
            var inverting = sheet.NetOf(amp.Inverting);
            var positive = sheet.NetOf(amp.NonInverting);

            if (output is null || inverting is null || positive is null) continue;

            if (output.Index == inverting.Index)
            {
                yield return new Explanation(
                    $"{amp.Name} is a follower",
                    "the output is tied straight back to the inverting input, so it copies its " +
                    "input at a gain of one — for the impedance, not the gain",
                    [amp]);

                continue;
            }

            // The feedback resistor: one between the output and the inverting input.
            var feedback = sheet.Between<Resistor>(output, inverting).FirstOrDefault();
            if (feedback is null) continue;

            // And the one that sets the ratio: from the inverting input to ground, or to the input.
            var gainLeg = sheet.On<Resistor>(inverting)
                .FirstOrDefault(r => !ReferenceEquals(r, feedback));

            if (gainLeg is null) continue;

            var far = sheet.Other(gainLeg, inverting);
            if (far is null) continue;

            var ratio = feedback.Resistance / gainLeg.Resistance;

            if (far.IsGround && !positive.IsGround)
            {
                yield return new Explanation(
                    $"{amp.Name} is a non-inverting amplifier",
                    $"a gain of 1 + {feedback.Name}/{gainLeg.Name} = {1 + ratio:0.###}, with the " +
                    "input going straight to the non-inverting pin so it loads nothing",
                    [amp, feedback, gainLeg]);

                continue;
            }

            if (positive.IsGround && !far.IsGround)
            {
                yield return new Explanation(
                    $"{amp.Name} is an inverting amplifier",
                    $"a gain of −{feedback.Name}/{gainLeg.Name} = −{ratio:0.###}, and the input " +
                    $"sees {SiPrefix.Format(gainLeg.Resistance, "Ω")} because the inverting pin " +
                    "is held at ground by the feedback",
                    [amp, feedback, gainLeg]);
            }
        }
    }

    // ---- transistors -------------------------------------------------------

    private static IEnumerable<Explanation> Followers(Sheet sheet)
    {
        foreach (var transistor in sheet.All<BipolarTransistor>())
        {
            var collector = sheet.NetOf(transistor.Collector);
            var emitter = sheet.NetOf(transistor.Emitter);

            if (collector is null || emitter is null) continue;

            var rail = sheet.RailVoltage(collector);

            // Collector straight on a rail and a resistor from the emitter to ground: a follower.
            if (rail is not null && sheet.On<Resistor>(emitter).Count() == 1 && !emitter.IsGround)
            {
                yield return new Explanation(
                    $"{transistor.Name} is an emitter follower",
                    "the collector is on the rail and the output comes off the emitter, so it " +
                    "copies its base a diode drop lower — for current, not voltage",
                    [transistor]);

                continue;
            }

            // A resistor from the collector to a rail is a common-emitter stage.
            var load = sheet.On<Resistor>(collector).FirstOrDefault();
            if (load is null || rail is not null) continue;

            var far = sheet.Other(load, collector);
            if (far is null || sheet.RailVoltage(far) is null) continue;

            yield return new Explanation(
                $"{transistor.Name} is a common-emitter stage",
                $"{load.Name} turns its collector current into a voltage, and the output is " +
                "inverted — the classic voltage amplifier, and the classic way to get gain",
                [transistor, load]);
        }
    }

    // ---- flyback -----------------------------------------------------------

    private static IEnumerable<Explanation> Flybacks(Sheet sheet)
    {
        foreach (var diode in sheet.All<Diode>())
        {
            if (diode is Led) continue;

            var anode = sheet.NetOf(diode.Anode);
            var cathode = sheet.NetOf(diode.Cathode);

            if (anode is null || cathode is null) continue;

            // Something with a coil in it across the same two nets, the other way up.
            var coil = sheet.Between<CircuitComponent>(anode, cathode)
                .FirstOrDefault(c => c is Inductor or Relay or DcMotor);

            if (coil is null) continue;

            yield return new Explanation(
                $"{diode.Name} is {coil.Name}'s flyback diode",
                "it is reverse biased while the coil is driven and does nothing. The moment the " +
                "drive is removed the coil's current has to keep flowing, and without somewhere " +
                "to go it makes whatever voltage it takes — hundreds of volts, into whatever was " +
                "switching it",
                [diode, coil]);
        }
    }

    // ---- 555 ---------------------------------------------------------------

    private static IEnumerable<Explanation> Astables(Sheet sheet)
    {
        foreach (var timer in sheet.All<Ne555>())
        {
            var discharge = sheet.NetOf(timer.Discharge);
            var threshold = sheet.NetOf(timer.Threshold);
            var trigger = sheet.NetOf(timer.Trigger);

            if (discharge is null || threshold is null || trigger is null) continue;

            // Astable: threshold and trigger tied together, a capacitor from there to ground,
            // and two resistors — one to the rail, one down to the discharge pin.
            if (threshold.Index != trigger.Index) continue;

            var capacitor = sheet.On<Capacitor>(threshold)
                .FirstOrDefault(c => sheet.Other(c, threshold)?.IsGround == true);

            if (capacitor is null) continue;

            var lower = sheet.Between<Resistor>(discharge, threshold).FirstOrDefault();
            var upper = sheet.On<Resistor>(discharge)
                .FirstOrDefault(r => !ReferenceEquals(r, lower)
                                     && sheet.RailVoltage(sheet.Other(r, discharge)) is not null);

            if (lower is null || upper is null) continue;

            var c = capacitor.Capacitance;
            var high = 0.693 * (upper.Resistance + lower.Resistance) * c;
            var low = 0.693 * lower.Resistance * c;

            var period = high + low;
            if (period <= 0) continue;

            yield return new Explanation(
                $"{timer.Name} is an astable",
                $"about {SiPrefix.Format(1.0 / period, "Hz")} at a duty of " +
                $"{high / period:0.###} — the capacitor charges through {upper.Name} and " +
                $"{lower.Name} and discharges through {lower.Name} alone, which is why the duty " +
                "cannot get below a half without a diode across the upper one",
                [timer, upper, lower, capacitor]);
        }
    }
}
