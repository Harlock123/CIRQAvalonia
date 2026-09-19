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
        viewModel.RequestClose += (_, _) => Close();
        viewModel.RequestSettings += async (_, _) => await ShowSettingsAsync();

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
