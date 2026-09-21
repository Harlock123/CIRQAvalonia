using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cirq.UI.Controls;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _repaintTimer;
    private CircuitCanvas? _canvas;
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // The canvas shows live state (LED brightness, logic levels, probe readouts), so it is
        // repainted on a timer rather than only when the user interacts with it.
        _repaintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _repaintTimer.Tick += (_, _) => RefreshLiveState();
        _repaintTimer.Start();

        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        _viewModel = viewModel;
        _canvas = this.FindControl<CircuitCanvas>("Canvas");

        // The view model asks for files through this rather than reaching for a window itself.
        viewModel.FileDialogs = new StorageProviderFileDialogs(this);

        // The scope's plot lives in its panel, so the export is given a way to reach it.
        viewModel.ScopeSource = this.FindControl<ScopePanel>("Scope");

        WireUpCanvas(viewModel);
        WireUpMenu(viewModel);

        viewModel.RequestRedraw += (_, _) => Dispatcher.UIThread.Post(() => _canvas?.InvalidateVisual());
        viewModel.RequestZoomToFit += (_, _) => Dispatcher.UIThread.Post(() => _canvas?.RequestFit());
        viewModel.RequestZoomBy += (_, factor) => Dispatcher.UIThread.Post(() => _canvas?.ZoomBy(factor));

        Dispatcher.UIThread.Post(() =>
        {
            _canvas?.RequestFit();
            _canvas?.Focus();
        });
    }

    private void WireUpCanvas(MainWindowViewModel viewModel)
    {
        if (_canvas is null) return;

        _canvas.TopologyChanged += (_, _) => viewModel.Simulation.InvalidateTopology();
        _canvas.InteractiveEditBegan += (_, label) => viewModel.BeginInteractiveEdit(label);
        _canvas.InteractiveEditEnded += (_, _) => viewModel.EndInteractiveEdit();
        _canvas.ProbeRequested += (_, terminal) => viewModel.AttachProbe(terminal);
        _canvas.ProbeReferenceRequested += (_, terminal) => viewModel.SetProbeReference(terminal);
        _canvas.StatusChanged += (_, message) => viewModel.StatusMessage = message;
    }

    /// <summary>
    /// Connects the menu commands that need the canvas. The view model raises these rather than
    /// holding a reference to a control.
    /// </summary>
    private void WireUpMenu(MainWindowViewModel viewModel)
    {
        viewModel.RequestRotateSelection += (_, _) => _canvas?.RotateSelection();
        viewModel.RequestDeleteSelection += (_, _) => _canvas?.DeleteSelection();
        viewModel.RequestSelect += (_, components) => _canvas?.BringIntoView(components);
        viewModel.RequestClose += (_, _) => Close();
        viewModel.RequestSettings += async (_, _) => await ShowSettingsAsync();
        viewModel.RequestAbout += async (_, _) => await ShowAboutAsync();
        viewModel.RequestFrequencyResponse += async (_, _) => await ShowFrequencyResponseAsync();
        viewModel.RequestDcSweep += async (_, _) => await ShowDcSweepAsync();
        viewModel.RequestRuleCheck += async (_, _) => await ShowRuleCheckAsync();
        viewModel.RequestSpectrum += async (_, _) => await ShowSpectrumAsync();
        viewModel.RequestBusDecode += async (_, _) => await ShowBusDecodeAsync();
        viewModel.RequestMonteCarlo += async (_, _) => await ShowMonteCarloAsync();
        viewModel.RequestConditions += async (_, _) => await ShowConditionsAsync();
        viewModel.RequestBlockLibrary += async (_, _) => await ShowBlockLibraryAsync();
        viewModel.RequestExamples += async (_, _) => await ShowExamplesAsync();

        // Code-drawn surfaces cannot bind to a resource, so they are told to repaint.
        ThemeManager.ThemeChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Rendering.CanvasTheme.Invalidate();
            _canvas?.InvalidateVisual();
        });

        // Reflect the controller's starting speed in the Simulate menu.
        viewModel.RefreshSpeedSelection();
        viewModel.Simulation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(viewModel.Simulation.SpeedFactor)
                or nameof(viewModel.Simulation.IsMaximumThroughput))
            {
                Dispatcher.UIThread.Post(viewModel.RefreshSpeedSelection);
            }
        };
    }

    private async Task ShowAboutAsync()
    {
        var dialog = new AboutWindow { DataContext = new AboutViewModel() };
        await dialog.ShowDialog(this);
    }

    private async Task ShowExamplesAsync()
    {
        if (_viewModel is null) return;

        var browser = new ExampleBrowserViewModel();
        var dialog = new ExampleBrowserWindow { DataContext = browser };

        await dialog.ShowDialog(this);

        if (browser.Chosen is { } example) _viewModel.LoadExampleCommand.Execute(example);
    }

    private async Task ShowFrequencyResponseAsync()
    {
        if (_viewModel is null) return;

        var dialog = new FrequencyResponseWindow
        {
            DataContext = new FrequencyResponseViewModel(_viewModel.Circuit),
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowDcSweepAsync()
    {
        if (_viewModel is null) return;

        var dialog = new DcSweepWindow
        {
            DataContext = new DcSweepViewModel(_viewModel.Circuit),
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowSpectrumAsync()
    {
        if (_viewModel is null) return;

        var dialog = new SpectrumAnalyserWindow
        {
            DataContext = new SpectrumViewModel(_viewModel.Circuit),
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowBlockLibraryAsync()
    {
        if (_viewModel is null) return;

        var dialog = new BlockLibraryWindow
        {
            DataContext = new BlockLibraryViewModel(_viewModel),
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowConditionsAsync()
    {
        if (_viewModel is null) return;

        var model = new ConditionsViewModel(_viewModel.Circuit);

        // Temperature is baked into the engine when it is built, so a change is a rebuild rather
        // than something the next time point picks up.
        model.Changed += (_, _) => _viewModel.OnConditionsChanged();

        var dialog = new ConditionsWindow { DataContext = model };

        await dialog.ShowDialog(this);
    }

    private async Task ShowMonteCarloAsync()
    {
        if (_viewModel is null) return;

        var dialog = new MonteCarloWindow
        {
            DataContext = new MonteCarloViewModel(_viewModel.Circuit),
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowBusDecodeAsync()
    {
        if (_viewModel is null) return;

        var dialog = new BusDecodeWindow
        {
            DataContext = new BusDecodeViewModel(_viewModel.Circuit),
        };

        await dialog.ShowDialog(this);
    }

    private async Task ShowRuleCheckAsync()
    {
        if (_viewModel is null) return;

        var model = new RuleCheckViewModel(_viewModel.Circuit);

        // Revealing a part has to reach the canvas behind the dialog, so the selection is made
        // and repainted while the window is still open — which is the point: read the finding,
        // press the button, see which part it means.
        model.RevealRequested += (_, components) => _viewModel.Reveal(components);

        var dialog = new RuleCheckWindow { DataContext = model };

        await dialog.ShowDialog(this);
    }

    private async Task ShowSettingsAsync()
    {
        if (Application.Current is not App app) return;

        var dialog = new SettingsWindow
        {
            DataContext = new SettingsViewModel(app.SettingsStore, app.Settings),
        };

        await dialog.ShowDialog(this);
    }

    private void RefreshLiveState()
    {
        if (_viewModel is null) return;

        _canvas?.InvalidateVisual();

        // The parts can be worked from the canvas too — double-clicking a switch still flips it —
        // so the panel follows the components rather than assuming it is the only thing moving them.
        _viewModel.ControlPanel.Refresh();

        var simTime = this.FindControl<TextBlock>("SimTimeLabel");
        if (simTime is not null)
            simTime.Text = $"t = {Cirq.Core.Units.SiPrefix.Format(_viewModel.Simulation.SimulationTime, "s")}";

        var rate = this.FindControl<TextBlock>("RateLabel");
        if (rate is not null)
            rate.Text = _viewModel.Simulation.IsRunning
                ? $"{_viewModel.Simulation.StepsPerSecond / 1000.0:0.#}k steps/s"
                : string.Empty;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    _viewModel?.OpenCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.S:
                    _viewModel?.SaveCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.N:
                    _viewModel?.NewCircuitCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Z:
                    _viewModel?.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Y:
                    _viewModel?.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.E:
                    _viewModel?.ExportCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.C:
                    _viewModel?.CopySelectionCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.V:
                    _viewModel?.PasteCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        // Zoom, on every spelling of plus and minus a keyboard offers. The unshifted key is
        // OemPlus on most layouts, the numeric keypad reports Add and Subtract, and typing an
        // actual "+" means holding shift as well — so all of them count rather than only the one
        // the menu has room to print.
        if (e.KeyModifiers is KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift))
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    _viewModel?.ZoomInCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemMinus or Key.Subtract:
                    _viewModel?.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.S)
        {
            _viewModel?.SaveAsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Z redoes as well, which is what the rest of the world does and what anyone
        // who has not found Ctrl+Y will try.
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z)
        {
            _viewModel?.RedoCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                // Transport shortcuts work wherever focus sits. Space is deliberately not one of
                // them: on the canvas it is the pan modifier.
                case Key.F5:
                    _viewModel?.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.F6:
                    _viewModel?.StepCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.F8:
                    _viewModel?.ResetSimulationCommand.Execute(null);
                    e.Handled = true;
                    return;

                // Panel collapse, so the canvas can be given the whole window without reaching
                // for the mouse.
                case Key.F9:
                    _viewModel?.TogglePaletteCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.F10:
                    _viewModel?.ToggleInspectorCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }
}
