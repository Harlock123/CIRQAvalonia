using Cirq.Components.Boards;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// What a part says about itself when the pointer rests on it. The card exists so that "what is
/// this one set to" costs a look rather than a click and a glance across at the properties panel.
/// </summary>
public class ComponentSummaryTests
{
    [Fact]
    public void ItNamesThePartAndItsType()
    {
        var resistor = new Resistor(4.7e3) { Name = "R7" };

        var summary = ComponentSummary.For(resistor);

        Assert.Equal("R7 · Resistor", summary.Title);
    }

    /// <summary>
    /// The subtitle is whatever the part puts on the canvas, which for most is its value and for
    /// some is what it is doing at this instant.
    /// </summary>
    [Fact]
    public void AndSaysWhatItIsDoing()
    {
        var resistor = new Resistor(4.7e3);
        Assert.Equal(resistor.ValueLabel, ComponentSummary.For(resistor).Subtitle);

        var relay = new Relay();
        Assert.Equal("at rest", ComponentSummary.For(relay).Subtitle);
    }

    /// <summary>
    /// The settings come out with the same names and units the properties panel uses, because
    /// both read the same component through the same helper.
    /// </summary>
    [Fact]
    public void TheSettingsReadTheWayThePropertiesPanelWritesThem()
    {
        var summary = ComponentSummary.For(new Resistor(4.7e3));

        var row = Assert.Single(summary.Rows, r => r.Label == "Resistance");

        // The same formatter the properties panel uses, unit and all, rather than a second
        // opinion about how a resistance should read.
        Assert.Equal(Cirq.Core.Units.SiPrefix.Format(4.7e3, "Ω"), row.Value);
        Assert.Equal("4.7kΩ", row.Value);
    }

    /// <summary>A multi-word property name is broken up rather than left in camel case.</summary>
    [Fact]
    public void AndAreNamedInWords()
    {
        var summary = ComponentSummary.For(new FunctionGenerator(Waveform.Sine, 1e3, 5.0));

        Assert.Contains(summary.Rows, r => r.Label == "Amplitude Peak To Peak");
        Assert.Contains(summary.Rows, r => r.Label == "Dc Offset");
    }

    /// <summary>
    /// The settings a part marks as operable come first. Alphabetically, a generator's card led
    /// with its small-signal analysis magnitude and ran out of room before the waveform.
    /// </summary>
    [Fact]
    public void TheSettingsYouWorkComeFirst()
    {
        var summary = ComponentSummary.For(new FunctionGenerator(Waveform.Square, 500, 4.0));

        var labels = summary.Rows.Select(r => r.Label).ToList();

        var frequency = labels.IndexOf("Frequency");
        var acMagnitude = labels.IndexOf("Ac Magnitude");

        Assert.True(frequency >= 0, "the frequency did not make the card at all");
        Assert.True(frequency < acMagnitude || acMagnitude < 0,
            $"the AC analysis settings came before the waveform: {string.Join(", ", labels)}");
    }

    /// <summary>
    /// A temperature is not measured in kilodegrees, and a duty cycle of a half reads as "500m"
    /// under an SI prefix — correct, and useless.
    /// </summary>
    [Fact]
    public void TemperaturesAndBareRatiosGoOutWithoutAnSiPrefix()
    {
        var thermocouple = ComponentSummary.For(
            new Thermocouple(ThermocoupleType.K) { Temperature = 1400.0 });

        Assert.Equal("1400 °C", Assert.Single(thermocouple.Rows, r => r.Label == "Temperature").Value);

        var generator = ComponentSummary.For(
            new FunctionGenerator(Waveform.Square, 500, 4.0) { DutyCycle = 0.5 });

        Assert.Equal("0.5", Assert.Single(generator.Rows, r => r.Label == "Duty Cycle").Value);
    }

    /// <summary>But a unit that does take prefixes still gets them.</summary>
    [Fact]
    public void AndEverythingElseStillDoes()
    {
        var summary = ComponentSummary.For(new Capacitor(100e-9));

        Assert.Equal("100nF", Assert.Single(summary.Rows, r => r.Label == "Capacitance").Value);
    }

    [Fact]
    public void BooleansReadAsYesOrNo()
    {
        var summary = ComponentSummary.For(new ToggleSwitch(closed: true));

        Assert.Equal("yes", Assert.Single(summary.Rows, r => r.Label == "Is Closed").Value);
    }

    /// <summary>A model is a setting like any other, and the one most worth seeing at a glance.</summary>
    [Fact]
    public void AModelIsShownByName()
    {
        var summary = ComponentSummary.For(new OperationalAmplifier(OpAmpModel.Mcp6002));

        Assert.Equal("MCP6002", Assert.Single(summary.Rows, r => r.Label == "Model").Value);
    }

    /// <summary>
    /// A card taller than the part it describes stops being a glance, so a part with many settings
    /// says how many it left out rather than listing them all.
    /// </summary>
    [Fact]
    public void ALongListIsCutWithACountOfWhatIsLeft()
    {
        var board = new ArduinoUnoBoard();

        var summary = ComponentSummary.For(board);

        Assert.True(summary.Rows.Count <= ComponentSummary.MaximumRows + 1,
            $"{summary.Rows.Count} rows is not a glance");

        if (summary.Rows.Count > ComponentSummary.MaximumRows)
            Assert.StartsWith("+", summary.Rows[^1].Value);
    }

    /// <summary>
    /// A board's pin script runs to hundreds of characters. The card shows that there is one
    /// rather than trying to print it.
    /// </summary>
    [Fact]
    public void AVeryLongValueIsCutRatherThanPrinted()
    {
        var board = new ArduinoUnoBoard { Pins = new string('x', 400) };

        var summary = ComponentSummary.For(board);

        Assert.All(summary.Rows, r => Assert.True(r.Value.Length <= 41, $"'{r.Value}' is too long"));
    }

    /// <summary>
    /// The card is where a part explains the red ring round it, which until now only said that
    /// something was wrong and not what.
    /// </summary>
    [Fact]
    public void ItCarriesWhateverThePartIsComplainingAbout()
    {
        var probe = new Thermocouple(ThermocoupleType.K) { Temperature = 300.0 };

        Assert.Empty(ComponentSummary.For(probe).Warnings);

        // Past what the alloys will take, which the part reports rather than tolerating.
        probe.Temperature = 1400.0;

        var summary = ComponentSummary.For(probe);

        Assert.NotEmpty(summary.Warnings);
        Assert.Equal(probe.Violations, summary.Warnings);
    }

    /// <summary>And a part with nothing to complain about has an empty list rather than a null.</summary>
    [Fact]
    public void APartWithNothingToSayHasNoWarnings()
    {
        Assert.Empty(ComponentSummary.For(new Resistor(1e3)).Warnings);
        Assert.Empty(ComponentSummary.For(new Ground()).Warnings);
    }

    /// <summary>
    /// Every part in the palette can be described. A card that threw on one of them would take the
    /// canvas down with it, since it is built during a render.
    /// </summary>
    [Fact]
    public void EveryPaletteEntryCanBeDescribed()
    {
        foreach (var item in ComponentCatalog.AllItems)
        {
            var component = item.Create();
            component.Name = $"{component.DesignatorPrefix}1";

            var summary = ComponentSummary.For(component);

            Assert.False(string.IsNullOrWhiteSpace(summary.Title));
            Assert.Contains(component.ComponentType, summary.Title);
            Assert.NotNull(summary.Rows);
            Assert.NotNull(summary.Warnings);
        }
    }
}
