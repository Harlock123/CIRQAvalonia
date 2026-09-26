using Cirq.Cli;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Verification;

namespace Cirq.Cli.Tests;

/// <summary>
/// The engine without the editor.
/// <para>
/// Every test here drives the same entry point the executable does, with writers it can read back —
/// so what is checked is the tool's actual behaviour, including its exit code, which is the whole
/// reason for the tool. A command-line program whose behaviour can only be verified by running a
/// process is a command-line program nobody verifies.
/// </para>
/// </summary>
public class CommandLineTests : IDisposable
{
    private readonly List<string> _files = [];

    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    private string Output => _out.ToString();

    private string Error => _error.ToString();

    private int Run(params string[] arguments) => CommandLine.Run(arguments, _out, _error);

    public void Dispose()
    {
        foreach (var path in _files.Where(File.Exists)) File.Delete(path);

        _out.Dispose();
        _error.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A diode fed from a resistor, saved to a file: the drop is about 650 mV at room temperature
    /// and falls with heat, so a requirement can be written that passes now and fails hot.
    /// </summary>
    private string Reference(double limit = 0.6, bool probed = true, bool required = true)
    {
        var circuit = new Circuit { Title = "Diode reference" };

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10e3) { Name = "R1" });
        var diode = circuit.Add(new Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        if (probed) circuit.Probes.Add(new SignalProbe("Vf", diode.Anode, default));

        if (required)
        {
            circuit.Specs.Add(new DesignSpec
            {
                Name = "Reference",
                Trace = "Vf",
                Quantity = SpecQuantity.Mean,
                Comparison = SpecComparison.AtLeast,
                Limit = limit,
            });
        }

        return Save(circuit);
    }

    private string Save(Circuit circuit)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-cli-{Guid.NewGuid():N}.cirq");

        File.WriteAllText(path, CircuitSerializer.ToJson(circuit));
        _files.Add(path);

        return path;
    }

    // ---- the command line itself -------------------------------------------

    [Fact]
    public void WithNothingToDoItSaysWhatItCanDo()
    {
        Assert.Equal(CommandLine.Misused, Run());

        Assert.Contains("cirq check", Output);
        Assert.Contains("Exit codes", Output);
    }

    [Fact]
    public void HelpIsNotAnError()
    {
        Assert.Equal(CommandLine.Ok, Run("--help"));
        Assert.Contains("cirq run", Output);
    }

    [Fact]
    public void HelpAfterACommandDescribesThatCommand()
    {
        Assert.Equal(CommandLine.Ok, Run("check", "--help"));

        Assert.Contains("--over", Output);
        Assert.DoesNotContain("cirq netlist", Output);
    }

    [Fact]
    public void AnUnknownCommandSaysSoAndIsAMisuse()
    {
        Assert.Equal(CommandLine.Misused, Run("simulate", "x.cirq"));
        Assert.Contains("no command called", Error);
    }

    [Fact]
    public void AFileThatIsNotThereIsAMisuseRatherThanACrash()
    {
        Assert.Equal(CommandLine.Misused, Run("check", "/nowhere/at/all.cirq"));
        Assert.Contains("no file called", Error);
    }

    [Fact]
    public void AFileThatIsNotACircuitSaysSo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cirq-cli-{Guid.NewGuid():N}.cirq");

        File.WriteAllText(path, "this is not a circuit");
        _files.Add(path);

        Assert.Equal(CommandLine.Misused, Run("check", path));
        Assert.Contains("not a circuit", Error);
    }

    [Fact]
    public void ANumberThatIsNotANumberIsRefusedBeforeAnythingRuns()
    {
        Assert.Equal(CommandLine.Misused, Run("check", Reference(), "--for", "soon"));
        Assert.Contains("wants a number", Error);
    }

    // ---- check --------------------------------------------------------------

    [Fact]
    public void AMetRequirementExitsZero()
    {
        Assert.Equal(CommandLine.Ok, Run("check", Reference(limit: 0.2), "--for", "1e-4"));

        Assert.Contains("pass", Output);
        Assert.Contains("All 1 requirement(s) met", Output);
    }

    /// <summary>The whole point of the tool: a circuit that does not meet its requirements fails a build.</summary>
    [Fact]
    public void AnUnmetRequirementExitsOne()
    {
        Assert.Equal(CommandLine.Failed, Run("check", Reference(limit: 5.0), "--for", "1e-4"));

        Assert.Contains("FAIL", Output);
        Assert.Contains("1 of 1 requirement(s) not met", Output);
    }

    [Fact]
    public void QuietSaysOnlyTheVerdict()
    {
        Assert.Equal(CommandLine.Ok, Run("check", Reference(limit: 0.2), "--for", "1e-4", "--quiet"));

        Assert.DoesNotContain("pass", Output);
        Assert.Contains("All 1 requirement(s) met", Output);
    }

    [Fact]
    public void ACircuitWithNoRequirementsIsAMisuseRatherThanAPass()
    {
        // Silently exiting zero would be the dangerous answer: a build would go green because
        // nobody had written anything down.
        Assert.Equal(CommandLine.Misused, Run("check", Reference(required: false)));
        Assert.Contains("no requirements", Error);
    }

    [Fact]
    public void ACircuitWithNoProbesIsAMisuseToo()
    {
        Assert.Equal(CommandLine.Misused, Run("check", Reference(probed: false)));
        Assert.Contains("no probes", Error);
    }

    // ---- check over a range --------------------------------------------------

    [Fact]
    public void CheckingOverTemperatureFindsWhereItGivesOut()
    {
        var exit = Run("check", Reference(limit: 0.6), "--for", "1e-4", "--over", "temperature");

        Assert.Equal(CommandLine.Failed, exit);
        Assert.Contains("FAIL", Output);
        Assert.Contains("met from -40", Output);
        Assert.Contains("it holds from", Output);
    }

    [Fact]
    public void ARangeThatItSurvivesExitsZero()
    {
        var exit = Run(
            "check", Reference(limit: 0.6), "--for", "1e-4",
            "--over", "temperature", "--from", "-40", "--to", "-20", "--points", "4");

        Assert.Equal(CommandLine.Ok, exit);
        Assert.Contains("Every requirement is met", Output);
    }

    [Fact]
    public void ItCanSweepAPartInsteadOfTheTemperature()
    {
        var exit = Run(
            "check", Reference(limit: 0.6), "--for", "1e-4",
            "--over", "R1.Resistance", "--from", "1000", "--to", "1000000", "--points", "6");

        // More series resistance is less current and less forward drop, so it gives out somewhere.
        Assert.Equal(CommandLine.Failed, exit);
    }

    [Fact]
    public void SweepingSomethingThatIsNotThereIsAMisuse()
    {
        var exit = Run("check", Reference(), "--for", "1e-4", "--over", "R9.Resistance");

        Assert.Equal(CommandLine.Misused, exit);
        Assert.Contains("nothing called", Error);
    }

    // ---- run, netlist, info --------------------------------------------------

    [Fact]
    public void RunWritesTheTracesAsCsv()
    {
        var circuit = Reference();
        var csv = Path.ChangeExtension(circuit, ".csv");

        _files.Add(csv);

        Assert.Equal(CommandLine.Ok, Run("run", circuit, "--for", "1e-4"));

        Assert.True(File.Exists(csv));

        var lines = File.ReadAllLines(csv);

        Assert.Equal("time,Vf", lines[0]);
        Assert.True(lines.Length > 10, $"only {lines.Length} rows");

        // A real reading rather than a column of zeros: a silicon diode's drop.
        var first = lines[1].Split(',');

        Assert.InRange(double.Parse(first[1], System.Globalization.CultureInfo.InvariantCulture), 0.3, 0.9);
    }

    [Fact]
    public void RunWithoutProbesHasNothingToWrite()
    {
        Assert.Equal(CommandLine.Misused, Run("run", Reference(probed: false)));
        Assert.Contains("no probes", Error);
    }

    [Fact]
    public void NetlistGoesToTheScreenOrToAFile()
    {
        var circuit = Reference();

        Assert.Equal(CommandLine.Ok, Run("netlist", circuit));

        Assert.Contains("R1", Output);

        var deck = Path.ChangeExtension(circuit, ".cir");
        _files.Add(deck);

        Assert.Equal(CommandLine.Ok, Run("netlist", circuit, "-o", deck));
        Assert.True(File.Exists(deck));
        Assert.Contains("R1", File.ReadAllText(deck));
    }

    [Fact]
    public void InfoSaysWhatIsInIt()
    {
        Assert.Equal(CommandLine.Ok, Run("info", Reference()));

        Assert.Contains("Diode reference", Output);
        Assert.Contains("part(s)", Output);
        Assert.Contains("1 probe(s): Vf", Output);
        Assert.Contains("1 requirement(s)", Output);
        Assert.Contains("ambient 27 °C", Output);
    }

    [Fact]
    public void InfoNamesTheSheetsWhenThereAreSome()
    {
        var circuit = new Circuit { Title = "Split" };

        circuit.Sheets.Add("Power");
        circuit.Sheets.Add("Logic");
        circuit.Add(new Resistor(1e3) { Sheet = "Logic" });
        circuit.Add(new Ground());

        Assert.Equal(CommandLine.Ok, Run("info", Save(circuit)));

        Assert.Contains("2 sheet(s): Power, Logic", Output);
    }

    [Fact]
    public void VersionSaysSomething()
    {
        Assert.Equal(CommandLine.Ok, Run("version"));
        Assert.NotEmpty(Output.Trim());
    }

    // ---- baselines -----------------------------------------------------------

    /// <summary>
    /// A resistive divider, whose output is exactly what the arithmetic says — so a change to a
    /// resistor is a change of a known size rather than a wobble.
    /// </summary>
    private string Divider(double top = 10e3)
    {
        var circuit = new Circuit { Title = "Divider" };

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var upper = circuit.Add(new Resistor(top) { Name = "R1" });
        var lower = circuit.Add(new Resistor(10e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", lower.A, default));

        return Save(circuit);
    }

    [Fact]
    public void ABaselineIsRecordedIntoTheCircuit()
    {
        var path = Divider();

        Assert.Equal(CommandLine.Ok, Run("baseline", path, "--for", "1e-4"));
        Assert.Contains("Recorded 1 trace(s)", Output);

        // Into the file itself, which is where a baseline lives: it travels with the design.
        var back = CircuitSerializer.FromJson(File.ReadAllText(path)).Circuit;

        Assert.False(back.Baseline.IsEmpty);
        Assert.Single(back.Baseline.Traces);
    }

    [Fact]
    public void AnUnchangedCircuitComparesClean()
    {
        var path = Divider();

        Run("baseline", path, "--for", "1e-4");

        Assert.Equal(CommandLine.Ok, Run("compare", path, "--for", "1e-4"));
        Assert.Contains("Nothing moved", Output);
        Assert.Contains("same", Output);
    }

    /// <summary>
    /// The point of the command: a change nobody meant to make fails a build the way a broken test
    /// does. The divider's midpoint goes from 5 V to 3.33 V, which is a third — far past the one
    /// percent that counts as a move.
    /// </summary>
    [Fact]
    public void ACircuitThatHasMovedExitsOne()
    {
        var path = Divider();

        Run("baseline", path, "--for", "1e-4");

        // Reopen it, change a resistor, and write it back — which is what an edit is.
        var circuit = CircuitSerializer.FromJson(File.ReadAllText(path)).Circuit;

        circuit.Components.OfType<Resistor>().First(r => r.Name == "R1").Resistance = 20e3;

        File.WriteAllText(path, CircuitSerializer.ToJson(circuit));

        Assert.Equal(CommandLine.Failed, Run("compare", path, "--for", "1e-4"));

        Assert.Contains("MOVED", Output);
        Assert.Contains("Mid", Output);
        Assert.Contains("changed since the baseline", Output);
    }

    [Fact]
    public void ComparingWithoutABaselineIsAMisuseRatherThanAPass()
    {
        // Exiting zero here would be the dangerous answer: a build would go green because nobody
        // had ever recorded what the circuit does.
        Assert.Equal(CommandLine.Misused, Run("compare", Divider(), "--for", "1e-4"));
        Assert.Contains("no baseline", Error);
    }

    [Fact]
    public void ANewProbeCountsAsAChange()
    {
        var path = Divider();

        Run("baseline", path, "--for", "1e-4");

        var circuit = CircuitSerializer.FromJson(File.ReadAllText(path)).Circuit;
        var supply = circuit.Components.OfType<DcVoltageSource>().Single();

        circuit.Probes.Add(new SignalProbe("Rail", supply.Positive, default));

        File.WriteAllText(path, CircuitSerializer.ToJson(circuit));

        Assert.Equal(CommandLine.Failed, Run("compare", path, "--for", "1e-4"));
        Assert.Contains("new since the baseline", Output);
    }

    [Fact]
    public void QuietComparingSaysOnlyTheVerdict()
    {
        var path = Divider();

        Run("baseline", path, "--for", "1e-4");

        Assert.Equal(CommandLine.Ok, Run("compare", path, "--for", "1e-4", "--quiet"));

        Assert.DoesNotContain("same", Output);
        Assert.Contains("Nothing moved", Output);
    }

    [Fact]
    public void RecordingWithoutProbesHasNothingToRecord()
    {
        Assert.Equal(CommandLine.Misused, Run("baseline", Reference(probed: false)));
        Assert.Contains("no probes", Error);
    }

    [Fact]
    public void BaselineHelpDescribesBothHalves()
    {
        Assert.Equal(CommandLine.Ok, Run("baseline", "--help"));

        Assert.Contains("cirq compare", Output);
        Assert.Contains("one percent", Output);
    }
}
