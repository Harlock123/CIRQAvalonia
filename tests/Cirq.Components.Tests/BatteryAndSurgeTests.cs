using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class BatteryTests
{
    private static (CircuitSimulator Sim, Battery Cell, Resistor Load) Rig(
        BatteryModel model, double ohms)
    {
        var circuit = new Circuit();
        var cell = circuit.Add(new Battery(model));
        var load = circuit.Add(new Resistor(ohms));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(cell.Negative, gnd.Pin);
        circuit.Connect(cell.Positive, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, cell, load);
    }

    /// <summary>
    /// The whole reason the part exists: a real cell is a source behind a resistance, so the
    /// terminal voltage falls as you take current. An ideal source does not do this, which is why
    /// circuits that work on the bench supply misbehave on batteries.
    /// </summary>
    [Fact]
    public void TheTerminalVoltageSagsUnderLoad()
    {
        var (sim, cell, load) = Rig(BatteryModel.Alkaline9V, 100);   // 2 ohm internal

        var terminal = sim.NodeVoltage(load.A);
        var expected = 9.0 * 100.0 / (100.0 + 2.0);

        Assert.Equal(expected, terminal, 0.02);
        Assert.True(terminal < cell.OpenCircuitVoltage, "loading it must cost something");
    }

    /// <summary>A coin cell is ten ohms, so an LED's worth of current brings it to its knees.</summary>
    [Fact]
    public void AHighImpedanceCellSagsFarMoreThanALowOne()
    {
        var (coinSim, _, coinLoad) = Rig(BatteryModel.CoinCell2032, 150);
        var (leadSim, _, leadLoad) = Rig(BatteryModel.LeadAcid12V, 150);

        var coinSag = 3.0 - coinSim.NodeVoltage(coinLoad.A);
        var leadSag = 12.0 - leadSim.NodeVoltage(leadLoad.A);

        Assert.True(coinSag > 0.15, $"a 10 ohm cell should sag visibly, not {coinSag:0.000} V");
        Assert.True(leadSag < 0.01, $"a 20 milliohm cell should barely notice, not {leadSag:0.000} V");
    }

    /// <summary>Charge is counted out as it is drawn: amps for seconds against the rating.</summary>
    [Fact]
    public void ItRunsDownInProportionToTheChargeTaken()
    {
        // 550 mAh at 9 V into 90 ohms is about 100 mA, so 1% of it is 19.8 seconds.
        var (sim, cell, _) = Rig(BatteryModel.Alkaline9V, 90);

        Assert.Equal(1.0, cell.StateOfCharge, 1e-9);

        sim.Run(1.0);

        var drawnMilliampHours = cell.OutputCurrent * 1e3 / 3600.0;
        Assert.Equal(drawnMilliampHours, cell.DeliveredMilliampHours, drawnMilliampHours * 0.02);
        Assert.Equal(1.0 - (drawnMilliampHours / 550.0), cell.StateOfCharge, 1e-4);
    }

    /// <summary>Switching the discharge off keeps the sag but stops the clock.</summary>
    [Fact]
    public void ItCanBeToldNotToRunDown()
    {
        var (sim, cell, load) = Rig(BatteryModel.Alkaline9V, 90);
        cell.Discharges = false;

        sim.Run(1.0);

        Assert.Equal(1.0, cell.StateOfCharge, 1e-9);
        Assert.True(sim.NodeVoltage(load.A) < 9.0, "it should still sag");
    }

    /// <summary>
    /// The discharge curve is flat across most of its life and then falls away, which is the shape
    /// that matters: a battery gives very little warning.
    /// </summary>
    [Fact]
    public void TheVoltageHoldsUpAndThenFallsOffAKnee()
    {
        var cell = new Battery(BatteryModel.Alkaline9V);

        cell.StateOfCharge = 1.0;
        var full = cell.OpenCircuitVoltage;

        cell.StateOfCharge = 0.5;
        var half = cell.OpenCircuitVoltage;

        cell.StateOfCharge = 0.02;
        var nearlyOut = cell.OpenCircuitVoltage;

        Assert.Equal(9.0, full, 1e-9);
        Assert.True(full - half < full * 0.2, "most of the discharge should be flat");
        Assert.True(full - nearlyOut > full * 0.2, "and the end should not be");
    }
}

public class SurgeSuppressorTests
{
    private static (CircuitSimulator Sim, T Part, Resistor Feed) Rig<T>(T part, double supply, double feed)
        where T : Cirq.Core.Topology.CircuitComponent
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(supply));
        var resistor = circuit.Add(new Resistor(feed));
        circuit.Add(part);
        var gnd = circuit.Add(new Ground());

        var a = part is TransientSuppressor t ? t.A : ((Varistor)(object)part).A;
        var b = part is TransientSuppressor t2 ? t2.B : ((Varistor)(object)part).B;

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, a);
        circuit.Connect(b, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, part, resistor);
    }

    /// <summary>
    /// Below its standoff voltage a TVS has to be invisible, or it is interfering with the circuit
    /// it is supposed to be protecting.
    /// </summary>
    [Fact]
    public void ATvsDoesNothingBelowItsStandoffVoltage()
    {
        var (sim, tvs, feed) = Rig(new TransientSuppressor(24.0), supply: 12.0, feed: 100);

        Assert.Equal(12.0, sim.NodeVoltage(feed.B), 0.01);
        Assert.False(tvs.IsClamping);
        Assert.True(Math.Abs(tvs.Current) < 1e-6);
    }

    /// <summary>Past it, the whole point: the node is held near the clamping voltage.</summary>
    [Fact]
    public void ATvsClampsAnOvervoltage()
    {
        var (sim, tvs, feed) = Rig(new TransientSuppressor(24.0), supply: 200.0, feed: 10);

        var clamped = sim.NodeVoltage(feed.B);

        Assert.True(tvs.IsClamping);
        Assert.InRange(clamped, 24.0, 55.0);
        Assert.True(clamped < 200.0 * 0.4, "an unclamped node would sit near the source");
    }

    /// <summary>It clamps the same way whichever way the surge arrives.</summary>
    [Fact]
    public void ATvsClampsBothPolarities()
    {
        var (positive, _, feedP) = Rig(new TransientSuppressor(24.0), supply: 200.0, feed: 10);
        var (negative, _, feedN) = Rig(new TransientSuppressor(24.0), supply: -200.0, feed: 10);

        Assert.Equal(positive.NodeVoltage(feedP.B), -negative.NodeVoltage(feedN.B), 1.0);
    }

    /// <summary>
    /// An MOV is a power law, so the current rises enormously for a small rise in voltage — that
    /// exponent is what makes it a clamp at all.
    /// </summary>
    [Fact]
    public void AVaristorsCurrentFollowsAPowerLaw()
    {
        var mov = new Varistor(275.0) { Nonlinearity = 30.0 };

        // Fed through an ohm, so the voltage across the part is the source voltage and what is
        // measured is the device rather than the resistor feeding it.
        var (_, _, _) = Rig(mov, supply: 275.0, feed: 1.0);
        var atRating = Math.Abs(mov.Current);

        var higher = new Varistor(275.0) { Nonlinearity = 30.0 };
        var (_, _, _) = Rig(higher, supply: 330.0, feed: 1.0);
        var above = Math.Abs(higher.Current);

        // A 20% rise in voltage raises the current by 1.2^30, which is a factor of about 240.
        Assert.True(above > atRating * 20,
            $"{atRating:g3} A to {above:g3} A is not a power law");
    }

    /// <summary>
    /// It wears out, which is the thing about MOVs nobody expects: they are consumable, and a
    /// spent one conducts at working voltage instead of failing open.
    /// </summary>
    [Fact]
    public void AVaristorWearsOutOnTheEnergyItAbsorbs()
    {
        var mov = new Varistor(50.0) { EnergyRating = 0.05, Nonlinearity = 30.0 };
        var (sim, _, _) = Rig(mov, supply: 120.0, feed: 20);

        Assert.False(mov.IsWornOut);
        Assert.Empty(mov.Violations);

        sim.Run(20e-3);

        Assert.True(mov.AbsorbedJoules > 0, "it should be counting the energy");
        Assert.True(mov.IsWornOut, $"only absorbed {mov.AbsorbedJoules:g3} J of a 0.05 J rating");
        Assert.Contains("worn out", string.Join(" ", mov.Violations));
    }
}

/// <summary>
/// Which way a source says its current is going.
/// <para>
/// <see cref="ICurrentReporting"/> asks every part for the current flowing <b>into</b> it through
/// the probed pin — the convention a clamp meter uses, so that clamping the two ends of anything
/// reads equal and opposite. A source delivering power therefore reports a <i>negative</i> current
/// into its positive pin, because the charge is coming out.
/// </para>
/// <para>
/// This is tested across the three kinds of source together, and deliberately. The battery used to
/// report the current it was delivering rather than the current going in, and it was the only part
/// in the library doing so — which is exactly the shape of bug that survives: the magnitude was
/// right, each end still read equal and opposite, and "current out of a battery" is such a natural
/// phrase that the wrong sign looked like the right answer. Nothing but a comparison against its
/// neighbours catches it.
/// </para>
/// </summary>
public class SourceCurrentDirectionTests
{
    /// <summary>A source, a kilohm and a ground. Whatever the source is, the loop is the same.</summary>
    private static (CircuitSimulator Sim, CircuitComponent Supply, Resistor R) Loop(CircuitComponent supply)
    {
        var circuit = new Circuit();

        circuit.Add(supply);

        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment
        {
            SourceTerminal = supply.Terminals[0],
            TargetTerminal = r.A,
        });

        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = ground.Pin });

        circuit.Wires.Add(new WireSegment
        {
            SourceTerminal = supply.Terminals[1],
            TargetTerminal = ground.Pin,
        });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, supply, r);
    }

    /// <summary>The three kinds of source, which all have to agree about this.</summary>
    public static TheoryData<string> Sources() => new("battery", "solar cell", "dc source");

    private static CircuitComponent Make(string kind) => kind switch
    {
        "battery" => new Battery { Name = "BT1", Discharges = false },
        "solar cell" => new SolarCell { Name = "PV1", Illumination = 1.0 },
        _ => new DcVoltageSource(9.0) { Name = "V1" },
    };

    /// <summary>
    /// Every source delivering into a load reports current flowing out of its positive pin, which
    /// under the clamp meter's convention is a negative reading there.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void ADeliveringSourceReadsNegativeIntoItsPositivePin(string kind)
    {
        var (sim, supply, r) = Loop(Make(kind));

        var intoPositive = sim.TerminalCurrent(supply.Terminals[0]);
        var intoLoad = sim.TerminalCurrent(r.A);

        Assert.True(intoLoad > 0, $"the load takes current in: {intoLoad}");

        Assert.True(intoPositive < 0,
            $"a {kind} delivering {intoLoad:0.###} A should read that as negative into its + pin, not {intoPositive}");

        // And it is the same current: what leaves the source is what arrives at the load, because
        // there is nowhere else in this circuit for it to be.
        Assert.Equal(intoLoad, -intoPositive, 1e-6);
    }

    /// <summary>
    /// Clamping the far end of a part that speaks for its own pins reads the same current the
    /// other way round, which is what a clamp meter does and what the convention is for.
    /// </summary>
    [Theory]
    [InlineData("battery")]
    [InlineData("solar cell")]
    public void TheTwoEndsOfAReportingSourceReadEqualAndOpposite(string kind)
    {
        var (sim, supply, _) = Loop(Make(kind));

        Assert.Equal(
            sim.TerminalCurrent(supply.Terminals[0]),
            -sim.TerminalCurrent(supply.Terminals[1]),
            1e-6);
    }

    /// <summary>
    /// A source that does not implement <see cref="ICurrentReporting"/> falls back to its branch
    /// current, and that reads the same at both ends rather than equal and opposite.
    /// <para>
    /// Pinned rather than fixed, because it is the documented limit of the fallback and not a
    /// defect in this part: a branch is a property of the whole component, and on a package with
    /// ten driven outputs there are ten branches and no way to say which pin was meant. The
    /// magnitude and the sign are both right for the part as a whole. What is missing is any notion
    /// of which end you clamped — so anything that needs that asks only parts that can answer.
    /// </para>
    /// </summary>
    [Fact]
    public void TheBranchFallbackCannotTellOneEndFromTheOther()
    {
        var (sim, supply, r) = Loop(Make("dc source"));

        Assert.IsNotAssignableFrom<ICurrentReporting>(supply);

        var first = sim.TerminalCurrent(supply.Terminals[0]);
        var second = sim.TerminalCurrent(supply.Terminals[1]);

        Assert.Equal(first, second, 1e-12);

        // It is still the right current, and still signed as going into the part.
        Assert.Equal(-sim.TerminalCurrent(r.A), first, 1e-6);
    }
}
