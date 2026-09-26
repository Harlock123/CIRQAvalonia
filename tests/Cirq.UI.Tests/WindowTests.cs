using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.Tests.Support;
using Cirq.UI.ViewModels;
using Cirq.UI.Views;

namespace Cirq.UI.Tests;

/// <summary>
/// Every window, actually opened.
/// <para>
/// The two dialog bugs this suite failed to catch were both of this shape: the view model was
/// perfect, the XAML compiled, and the thing on the screen could not be used. One had no way out at
/// all; the other had buttons the same colour as the panel behind them. Neither is visible from
/// anywhere except a window that exists.
/// </para>
/// </summary>
[Collection(WindowCollection.Name)]
public class WindowTests(WindowSession session)
{
    private static Circuit Simple()
    {
        var circuit = new Circuit();

        circuit.Add(new Resistor(1e3) { Name = "R1" });
        circuit.Add(new Ground { Name = "GND1" });

        return circuit;
    }

    /// <summary>Every window the menu can open, with a view model it will accept.</summary>
    public static TheoryData<string> Windows() => new(
        "About", "Baseline", "Block", "Compare", "Conditions", "DcSweep", "ExampleBrowser", "Explain",
        "Find", "FrequencyResponse", "Impedance", "MonteCarlo", "Noise", "Parameters",
        "PoleZero", "Ratings",
        "RuleCheck", "Specs", "SpecSweep", "SpectrumAnalyser", "Stability", "TransientStep");

    /// <summary>A circuit with a block in it, for the window that looks inside one.</summary>
    private static BlockViewModel BlockModel()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        var block = Cirq.Components.Hierarchy.Grouping.Group(circuit, [top, bottom], "Divider")!;

        return new BlockViewModel(block, circuit);
    }

    internal static Window BuildForTest(string name) => Build(name);

    private static Window Build(string name)
    {
        var circuit = Simple();

        return name switch
        {
            "About" => new AboutWindow { DataContext = new AboutViewModel() },
            "Block" => new BlockWindow { DataContext = BlockModel() },
            "Baseline" => new BaselineWindow { DataContext = new BaselineViewModel(circuit) },
            "Compare" => new CompareWindow { DataContext = new CompareViewModel("saved.cirq", []) },
            "Conditions" => new ConditionsWindow { DataContext = new ConditionsViewModel(circuit) },
            "DcSweep" => new DcSweepWindow { DataContext = new DcSweepViewModel(circuit) },
            "ExampleBrowser" => new ExampleBrowserWindow { DataContext = new ExampleBrowserViewModel() },
            "Explain" => new ExplainWindow { DataContext = new ExplainViewModel(circuit) },
            "Find" => new FindWindow { DataContext = new FindViewModel(circuit) },
            "FrequencyResponse" => new FrequencyResponseWindow
            {
                DataContext = new FrequencyResponseViewModel(circuit),
            },
            "Impedance" => new ImpedanceWindow { DataContext = new ImpedanceViewModel(circuit) },
            "MonteCarlo" => new MonteCarloWindow { DataContext = new MonteCarloViewModel(circuit) },
            "Noise" => new NoiseWindow { DataContext = new NoiseViewModel(circuit) },
            "Parameters" => new ParametersWindow { DataContext = new ParametersViewModel(circuit) },
            "PoleZero" => new PoleZeroWindow { DataContext = new PoleZeroViewModel(circuit) },
            "Ratings" => new RatingsWindow
            {
                DataContext = new RatingsViewModel(new SimulationController(circuit)),
            },
            "RuleCheck" => new RuleCheckWindow { DataContext = new RuleCheckViewModel(circuit) },
            "Specs" => new SpecsWindow { DataContext = new SpecsViewModel(circuit) },
            "SpecSweep" => new SpecSweepWindow { DataContext = new SpecSweepViewModel(circuit) },
            "SpectrumAnalyser" => new SpectrumAnalyserWindow
            {
                DataContext = new SpectrumViewModel(circuit),
            },
            "Stability" => new StabilityWindow { DataContext = new StabilityViewModel(circuit) },
            _ => new TransientStepWindow { DataContext = new TransientStepViewModel(circuit) },
        };
    }

    /// <summary>
    /// Every window opens, lays itself out and has something in it.
    /// <para>
    /// Which sounds trivial and is not: this is the step that runs the XAML, applies the styles,
    /// resolves every dynamic resource and builds every template. A resource key that is not there,
    /// a converter that throws, a template bound against the wrong type — all of them land here and
    /// nowhere earlier.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Windows))]
    public void EveryWindowOpensAndLaysOut(string name) => session.Run(() =>
    {
        var window = Build(name);

        window.Show();

        Assert.NotNull(window.Content);
        Assert.True(window.IsVisible, $"{name} opened invisible");
        Assert.True(window.Bounds.Width > 0 && window.Bounds.Height > 0,
            $"{name} laid out to {window.Bounds.Width}×{window.Bounds.Height}");

        window.Close();
    });

    /// <summary>
    /// Every window can be dismissed by pressing the thing that dismisses it.
    /// <para>
    /// This is the Conditions dialog's bug, which shipped: a window with settings in it, no close
    /// button, and a tiling window manager drawing no title bar to close it from. The check written
    /// afterwards looked through the XAML for a control <i>named</i> CloseButton — this one presses
    /// it and watches the window go.
    /// </para>
    /// <para>
    /// Two shapes are allowed, because the application has two. Most windows carry a button named
    /// <c>CloseButton</c> that the shared helper wires up. A window with a result — the example
    /// browser, the export options — has a Cancel marked <c>IsCancel</c> instead, which Avalonia
    /// closes the window from itself. Either is a way out; having neither is not.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Windows))]
    public void EveryWindowCanBeDismissed(string name) => session.Run(() =>
    {
        var window = Build(name);

        // The same wiring every dialog is shown through, applied without showing it modally: a
        // test cannot await a modal window, because the call does not return until it is gone.
        Dialog.Wire(window);
        window.Show();

        var button = window.GetLogicalDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "CloseButton" || b.IsCancel);

        Assert.True(button is not null,
            $"{name} has no way out: no button named CloseButton and none marked IsCancel.");

        Assert.True(button.IsVisible, $"{name}'s way out is not visible");
        Assert.True(button.IsEffectivelyEnabled, $"{name}'s way out is disabled");

        window.Click(button);

        Assert.False(window.IsVisible,
            $"{name} is still open after the button that closes it was clicked");
    });

    /// <summary>
    /// And Escape closes it too, which is the half nobody can see.
    /// </summary>
    [Theory]
    [MemberData(nameof(Windows))]
    public void EveryWindowClosesOnEscape(string name) => session.Run(() =>
    {
        var window = Build(name);

        Dialog.Wire(window);
        window.Show();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible, $"{name} is still open after Escape");
    });

    /// <summary>
    /// Nothing is drawn in a colour so close to what is behind it that it cannot be read.
    /// <para>
    /// This is the recovery prompt's bug, which also shipped: the dialog came up in the light
    /// palette while the application around it was dark, so its buttons were pale grey on pale grey
    /// and the only legible thing in the window was the message. Nobody can see that from a view
    /// model, and the XAML is innocent — the colours were right, they were just resolved against
    /// the wrong theme at the wrong moment.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Windows))]
    public void NothingIsDrawnInvisiblyAgainstItsOwnBackground(string name) => session.Run(() =>
    {
        var window = Build(name);

        window.Show();

        var background = Colour(window.Background);

        Assert.NotNull(background);

        foreach (var text in window.GetLogicalDescendants().OfType<TextBlock>())
        {
            if (!text.IsVisible || string.IsNullOrWhiteSpace(text.Text)) continue;

            var ink = Colour(text.Foreground);
            if (ink is null) continue;

            // Ignore anything deliberately faded: an opacity of a half is a choice about
            // emphasis, and this is about a colour that was never meant to disappear.
            if (text.Opacity < 0.95) continue;

            Assert.True(Contrast(ink.Value, background.Value) > 0.12,
                $"{name}: \"{Shorten(text.Text!)}\" is {ink} on {background}, which cannot be read");
        }

        window.Close();
    });

    private static Avalonia.Media.Color? Colour(Avalonia.Media.IBrush? brush) =>
        brush is Avalonia.Media.ISolidColorBrush solid ? solid.Color : null;

    /// <summary>
    /// The gap in relative luminance between two colours. Rough, and enough: this is looking for
    /// text that has vanished into its background rather than grading a palette.
    /// </summary>
    private static double Contrast(Avalonia.Media.Color ink, Avalonia.Media.Color ground) =>
        Math.Abs(Luminance(ink) - Luminance(ground));

    private static double Luminance(Avalonia.Media.Color c) =>
        ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    private static string Shorten(string text) =>
        text.Length <= 40 ? text.ReplaceLineEndings(" ") : text.ReplaceLineEndings(" ")[..39] + "…";
}
