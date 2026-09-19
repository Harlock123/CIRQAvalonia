using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class DcMotorTests
{
    private static (CircuitSimulator Sim, DcMotor Motor) Driven(double volts, double load = 0)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(volts));
        var motor = circuit.Add(new DcMotor { LoadTorque = load });
        var gnd = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(supply.Positive, motor.A);
        circuit.Connect(motor.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, motor);
    }

    /// <summary>
    /// At rest there is no back-EMF, so the armature is nothing but R and L across the supply and
    /// the current follows <c>i = V/R·(1 − e^(−tR/L))</c> towards the stall current of 4 A. The
    /// rotor has barely moved over these times, so the analytic curve is the answer.
    /// </summary>
    [Theory]
    [InlineData(200e-6)]
    [InlineData(667e-6)]     // one time constant: 63.2% of stall
    [InlineData(2e-3)]       // three: 95%
    public void AtRestTheArmatureCurrentFollowsItsLOverRCurve(double when)
    {
        var (sim, motor) = Driven(12.0);

        sim.Run(when);

        var stall = 12.0 / motor.ArmatureResistance;
        var tau = motor.ArmatureInductance / motor.ArmatureResistance;
        var expected = stall * (1.0 - Math.Exp(-when / tau));

        // Back-EMF is still a fraction of a volt at these times, so it moves the answer by
        // a couple of percent at most.
        Assert.Equal(expected, Math.Abs(motor.Current), expected * 0.05);
        Assert.True(motor.BackEmf < 0.5, $"back-EMF had already reached {motor.BackEmf:0.00} V");
    }

    [Fact]
    public void TheStallCurrentIsSetByTheArmatureResistanceAlone()
    {
        var (sim, motor) = Driven(12.0, load: 1.0);     // far more load than it can turn

        sim.Run(50e-3);

        Assert.True(Math.Abs(motor.AngularVelocity) < 1.0, "it should not be turning");
        Assert.Equal(12.0 / motor.ArmatureResistance, Math.Abs(motor.Current), 0.1);
    }

    /// <summary>
    /// Spun up with no load, the back-EMF rises until it nearly cancels the supply and the current
    /// falls away. The no-load speed is where back-EMF equals supply: omega = V / Ke.
    /// </summary>
    [Fact]
    public void ItSettlesNearTheNoLoadSpeedWhereBackEmfCancelsTheSupply()
    {
        var (sim, motor) = Driven(12.0);

        sim.Run(2.0);

        var ideal = 12.0 / motor.TorqueConstant;        // 600 rad/s
        Assert.InRange(motor.AngularVelocity, ideal * 0.9, ideal);
        Assert.True(Math.Abs(motor.Current) < 0.2,
            $"a free-running motor should draw almost nothing, not {motor.Current:0.00} A");
        Assert.Equal(12.0, motor.BackEmf, 1.5);
    }

    [Fact]
    public void TheStartupSurgeIsFarLargerThanTheRunningCurrent()
    {
        var (sim, motor) = Driven(12.0);

        sim.Run(2.0);

        // The surge is what welds relay contacts and trips supplies.
        Assert.True(motor.PeakCurrent > 10 * Math.Abs(motor.Current),
            $"peak {motor.PeakCurrent:0.00} A against a running {Math.Abs(motor.Current):0.000} A");
    }

    /// <summary>
    /// Loading the shaft slows it until the torque balances: at steady state Kt·i = load, so the
    /// current the motor draws is set by the load, not by the supply.
    /// </summary>
    [Fact]
    public void ALoadedShaftDrawsTheCurrentItsTorqueNeeds()
    {
        const double load = 0.01;                        // N·m
        var (sim, motor) = Driven(12.0, load);

        sim.Run(3.0);

        // i = load / Kt = 0.01 / 0.02 = 0.5 A, plus a little for viscous drag.
        Assert.Equal(load / motor.TorqueConstant, Math.Abs(motor.Current), 0.08);
        Assert.True(motor.AngularVelocity > 0, "it should still be turning");
    }

    [Fact]
    public void AHeavierLoadRunsSlower()
    {
        var (light, lightMotor) = Driven(12.0, 0.002);
        var (heavy, heavyMotor) = Driven(12.0, 0.012);

        light.Run(3.0);
        heavy.Run(3.0);

        Assert.True(heavyMotor.AngularVelocity < lightMotor.AngularVelocity,
            $"heavy {heavyMotor.Rpm:0} rpm vs light {lightMotor.Rpm:0} rpm");
    }

    [Fact]
    public void ReversingTheSupplyReversesTheShaft()
    {
        var (forward, forwardMotor) = Driven(12.0);
        var (reverse, reverseMotor) = Driven(-12.0);

        forward.Run(1.0);
        reverse.Run(1.0);

        Assert.True(forwardMotor.AngularVelocity > 0);
        Assert.True(reverseMotor.AngularVelocity < 0);
    }

    /// <summary>
    /// A motor held at standstill has no back-EMF, so nothing limits the current but the armature
    /// resistance. That is how they burn out, and it is worth saying so.
    /// </summary>
    [Fact]
    public void AMotorHeldStalledIsReported()
    {
        // A load far beyond what the motor can produce: Kt * stall current = 0.02 * 4 = 0.08 N·m.
        var (sim, motor) = Driven(12.0, load: 1.0);

        sim.Run(1.0);

        Assert.True(Math.Abs(motor.AngularVelocity) < 1.0, $"it turned at {motor.Rpm:0} rpm");
        Assert.Contains("stalled", string.Join(" | ", motor.Violations));
    }

    [Fact]
    public void TheStartupStallDoesNotCountAsBeingStalled()
    {
        // Every motor is stalled for an instant at switch-on; only sustained stall cooks it.
        var (sim, motor) = Driven(12.0);

        sim.Run(100e-3);

        Assert.Empty(motor.Violations);
    }
}

public class LightDependentResistorTests
{
    [Fact]
    public void ItsResistanceFallsAsTheLightRises()
    {
        var ldr = new LightDependentResistor();

        ldr.Illuminance = 1.0;
        var dim = ldr.Resistance;
        ldr.Illuminance = 100.0;
        var bright = ldr.Resistance;

        Assert.True(bright < dim / 10, $"{dim:0} ohm at 1 lux against {bright:0} ohm at 100 lux");
    }

    /// <summary>The datasheet figure: whatever else the curve does, it passes through R at 10 lux.</summary>
    [Fact]
    public void ItHitsItsQuotedResistanceAtTenLux()
    {
        var ldr = new LightDependentResistor { ResistanceAtTenLux = 10e3, Illuminance = 10.0 };

        Assert.Equal(10e3, ldr.Resistance, 1.0);
    }

    /// <summary>
    /// The response is a power law, so a decade of light is a fixed ratio of resistance:
    /// 10^gamma, which for a CdS cell at gamma 0.8 is about 6.3x per decade.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void ADecadeOfLightChangesResistanceByTenToTheGamma(double gamma)
    {
        var ldr = new LightDependentResistor { Gamma = gamma, ResistanceAtTenLux = 10e3 };

        ldr.Illuminance = 10.0;
        var atTen = ldr.Resistance;
        ldr.Illuminance = 100.0;
        var atHundred = ldr.Resistance;

        Assert.Equal(Math.Pow(10.0, gamma), atTen / atHundred, 0.05);
    }

    [Fact]
    public void InDarknessItFlattensOutAtItsDarkResistance()
    {
        var ldr = new LightDependentResistor { DarkResistance = 1e6, Illuminance = 1e-6 };

        Assert.Equal(1e6, ldr.Resistance, 1.0);
    }

    [Fact]
    public void CoveringItIsAnInteractionLikeOperatingASwitch()
    {
        var ldr = new LightDependentResistor();
        Assert.True(ldr.IsLit);

        var lit = ldr.Resistance;
        ldr.Interact();

        Assert.False(ldr.IsLit);
        Assert.True(ldr.Resistance > lit * 10, "covering it should raise the resistance sharply");

        ldr.Interact();
        Assert.True(ldr.IsLit);
    }

    /// <summary>
    /// What an LDR is actually for: one arm of a divider read by a comparator, so a circuit can
    /// act on light. Covering the cell has to flip the output.
    /// </summary>
    [Fact]
    public void ItDrivesAComparatorAcrossTheLightThreshold()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());

        var ldr = circuit.Add(new LightDependentResistor());
        var lower = circuit.Add(new Resistor(10e3));
        var reference = circuit.Add(new Resistor(10e3));
        var referenceLower = circuit.Add(new Resistor(10e3));
        var comparator = circuit.Add(new Comparator(ComparatorModel.Lm393));
        var pullUp = circuit.Add(new Resistor(4.7e3));

        circuit.Connect(supply.Negative, gnd.Pin);

        // Light divider: LDR on top, fixed resistor below.
        circuit.Connect(supply.Positive, ldr.A);
        circuit.Connect(ldr.B, lower.A);
        circuit.Connect(lower.B, gnd.Pin);

        // Fixed half-supply reference.
        circuit.Connect(supply.Positive, reference.A);
        circuit.Connect(reference.B, referenceLower.A);
        circuit.Connect(referenceLower.B, gnd.Pin);

        circuit.Connect(ldr.B, comparator.NonInverting);
        circuit.Connect(reference.B, comparator.Inverting);
        circuit.Connect(comparator.PositiveSupply, supply.Positive);
        circuit.Connect(comparator.NegativeSupply, gnd.Pin);
        circuit.Connect(supply.Positive, pullUp.A);
        circuit.Connect(pullUp.B, comparator.Output);

        double Solve()
        {
            var sim = new CircuitSimulator(circuit);
            sim.SolveOperatingPoint();
            return sim.NodeVoltage(comparator.Output);
        }

        // Lit: the LDR is the small resistance, so the divider sits high and the output is high.
        var lit = Solve();

        ldr.Interact();          // cover it
        var covered = Solve();

        Assert.True(lit > 3.0, $"lit output was {lit:0.00} V");
        Assert.True(covered < 1.0, $"covered output was {covered:0.00} V");
    }
}
