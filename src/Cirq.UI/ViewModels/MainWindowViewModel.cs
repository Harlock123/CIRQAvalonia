using System.Collections.Specialized;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Components.Serialization;
using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>Top-level state for the editor window.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public MainWindowViewModel()
    {
        Circuit = new Circuit { Title = "Untitled circuit" };
        Simulation = new SimulationController(Circuit);
        Scope = new ScopeViewModel(Circuit);
        Inspector = new InspectorViewModel();

        Scope.SamplingChanged += (_, _) => ApplyScopeSampling();
        Inspector.ParameterChanged += (_, structural) =>
        {
            if (structural) Simulation.InvalidateTopology();
            RequestRedraw?.Invoke(this, EventArgs.Empty);
        };

        Circuit.Components.CollectionChanged += OnCircuitChanged;
        Circuit.Wires.CollectionChanged += OnCircuitChanged;

        ApplyScopeSampling();

        // Open on a working circuit rather than an empty canvas, already compiled and biased so
        // the transport controls do something the moment the window appears.
        Examples.LoadRcLowPass(this);
        Simulation.Rebuild();
        IsModified = false;
    }

    public Circuit Circuit { get; }

    public SimulationController Simulation { get; }

    public ScopeViewModel Scope { get; }

    public InspectorViewModel Inspector { get; }

    /// <summary>
    /// Palette groups, each collapsible. Only the first opens on startup so the sidebar stays
    /// short; the rest are one click away.
    /// </summary>
    public IReadOnlyList<PaletteCategoryViewModel> Palette { get; } =
        [.. ComponentCatalog.Categories.Select((c, i) => new PaletteCategoryViewModel(c, isExpanded: i == 0))];

    public IReadOnlyList<ExampleCircuit> ExampleCircuits { get; } = Examples.All;

    [ObservableProperty]
    public partial EditorTool ActiveTool { get; set; } = EditorTool.Select;

    [ObservableProperty]
    public partial PaletteItem? PendingItem { get; set; }

    [ObservableProperty]
    public partial CircuitComponent? SelectedComponent { get; set; }

    [ObservableProperty]
    public partial bool SnapToGrid { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowGrid { get; set; } = true;

    /// <summary>
    /// Marks the parts you can operate by double-clicking them.
    /// <para>
    /// There is nothing about a switch, an LDR or a thermistor on the canvas that says it can be
    /// poked, and the only place that is written down is the palette description you have already
    /// scrolled past. On by default for that reason, and switchable off from the View menu once
    /// you know.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool ShowInteractiveMarkers { get; set; } = true;

    [ObservableProperty]
    public partial double GridSize { get; set; } = 10.0;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    /// <summary>Width of a collapsed panel's rail, in pixels.</summary>
    public const double CollapsedRailWidth = 26;

    private const double PaletteWidth = 168;
    private const double InspectorWidth = 280;

    /// <summary>
    /// Either side panel can be collapsed to give the canvas the window. A collapsed panel leaves
    /// a narrow rail at the edge rather than disappearing outright, so there is always something
    /// to click to bring it back.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPaletteExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsInspectorExpanded { get; set; } = true;

    public Avalonia.Controls.GridLength PaletteColumnWidth =>
        new(IsPaletteExpanded ? PaletteWidth : CollapsedRailWidth);

    public Avalonia.Controls.GridLength InspectorColumnWidth =>
        new(IsInspectorExpanded ? InspectorWidth : CollapsedRailWidth);

    /// <summary>A collapsed panel cannot be resized, so its splitter goes away with it.</summary>
    public Avalonia.Controls.GridLength PaletteSplitterWidth =>
        new(IsPaletteExpanded ? 4 : 0);

    public Avalonia.Controls.GridLength InspectorSplitterWidth =>
        new(IsInspectorExpanded ? 4 : 0);

    /// <summary>Chevrons point the way the panel will move when the button is pressed.</summary>
    public string PaletteToggleGlyph => IsPaletteExpanded ? "❮" : "❯";

    public string InspectorToggleGlyph => IsInspectorExpanded ? "❯" : "❮";

    public string PaletteToggleHint =>
        IsPaletteExpanded ? "Collapse the component palette (F9)" : "Show the component palette (F9)";

    public string InspectorToggleHint =>
        IsInspectorExpanded ? "Collapse the properties panel (F10)" : "Show the properties panel (F10)";

    [RelayCommand]
    private void TogglePalette() => IsPaletteExpanded = !IsPaletteExpanded;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorExpanded = !IsInspectorExpanded;

    partial void OnIsPaletteExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(PaletteColumnWidth));
        OnPropertyChanged(nameof(PaletteSplitterWidth));
        OnPropertyChanged(nameof(PaletteToggleGlyph));
        OnPropertyChanged(nameof(PaletteToggleHint));
        StatusMessage = value ? "Component palette shown" : "Component palette collapsed";
    }

    partial void OnIsInspectorExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(InspectorColumnWidth));
        OnPropertyChanged(nameof(InspectorSplitterWidth));
        OnPropertyChanged(nameof(InspectorToggleGlyph));
        OnPropertyChanged(nameof(InspectorToggleHint));
        StatusMessage = value ? "Properties panel shown" : "Properties panel collapsed";
    }

    // ---- file state ------------------------------------------------------

    /// <summary>Dialog provider, supplied by the window. Absent in tests unless one is injected.</summary>
    public ICircuitFileDialogs? FileDialogs { get; set; }

    /// <summary>Path this circuit was last opened from or saved to, if any.</summary>
    [ObservableProperty]
    public partial string? CurrentFilePath { get; private set; }

    /// <summary>True when there are edits that have not been written to disk.</summary>
    [ObservableProperty]
    public partial bool IsModified { get; private set; }

    /// <summary>File name for display, or a placeholder for a circuit that has never been saved.</summary>
    public string DocumentName => CurrentFilePath is null
        ? "Untitled"
        : Path.GetFileNameWithoutExtension(CurrentFilePath);

    /// <summary>Window title: the document, an asterisk when dirty, then the application.</summary>
    public string WindowTitle =>
        $"{DocumentName}{(IsModified ? "*" : string.Empty)} - CirqAvalonia";

    partial void OnCurrentFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnIsModifiedChanged(bool value) => OnPropertyChanged(nameof(WindowTitle));

    /// <summary>
    /// Confirms that unsaved work may be discarded. With no dialog provider the answer is yes,
    /// which is what tests want and what a headless run has to assume.
    /// </summary>
    private async Task<bool> MayDiscardAsync()
    {
        if (!IsModified) return true;
        if (FileDialogs is null) return true;
        return await FileDialogs.ConfirmDiscardChangesAsync(Circuit.Title);
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (FileDialogs is null) return;
        if (!await MayDiscardAsync()) return;

        var path = await FileDialogs.PickOpenPathAsync();
        if (path is null) return;

        await LoadFromAsync(path);
    }

    /// <summary>Loads a circuit from a path, replacing whatever is currently open.</summary>
    public async Task<bool> LoadFromAsync(string path)
    {
        Simulation.Pause();

        CircuitLoadResult result;
        try
        {
            result = CircuitSerializer.Load(path);
        }
        catch (Exception ex) when (ex is CircuitFormatException or IOException or UnauthorizedAccessException)
        {
            if (FileDialogs is not null) await FileDialogs.ReportAsync("Could not open circuit", ex.Message);
            StatusMessage = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }

        ReplaceCircuitWith(result.Circuit);

        CurrentFilePath = path;
        IsModified = false;
        Simulation.InvalidateTopology();
        Simulation.Rebuild();

        StatusMessage = result.IsClean
            ? $"Opened {Path.GetFileName(path)}"
            : $"Opened {Path.GetFileName(path)} with {result.Warnings.Count} warning(s)";

        if (!result.IsClean && FileDialogs is not null)
            await FileDialogs.ReportAsync("Circuit opened with warnings", string.Join("\n", result.Warnings));

        RequestZoomToFit?.Invoke(this, EventArgs.Empty);
        RequestRedraw?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Moves the loaded contents into the live circuit rather than swapping the object, because
    /// the canvas and the scope are already bound to this instance.
    /// </summary>
    private void ReplaceCircuitWith(Circuit loaded)
    {
        SelectedComponent = null;
        Circuit.Clear();
        Circuit.Title = loaded.Title;

        foreach (var component in loaded.Components) Circuit.Components.Add(component);
        foreach (var wire in loaded.Wires) Circuit.Wires.Add(wire);
        foreach (var probe in loaded.Probes) Circuit.Probes.Add(probe);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (CurrentFilePath is null)
        {
            await SaveAsAsync();
            return;
        }

        await WriteToAsync(CurrentFilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (FileDialogs is null) return;

        var suggested = DocumentName + CircuitSerializer.FileExtension;
        var path = await FileDialogs.PickSavePathAsync(suggested);
        if (path is null) return;

        await WriteToAsync(path);
    }

    /// <summary>Writes the circuit to a path and adopts it as the current file.</summary>
    public async Task<bool> WriteToAsync(string path)
    {
        try
        {
            CircuitSerializer.Save(Circuit, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (FileDialogs is not null) await FileDialogs.ReportAsync("Could not save circuit", ex.Message);
            StatusMessage = $"Could not save: {ex.Message}";
            return false;
        }

        CurrentFilePath = path;
        IsModified = false;
        StatusMessage = $"Saved {Path.GetFileName(path)}";
        return true;
    }

    /// <summary>Raised when the canvas needs to repaint because of a change it did not originate.</summary>
    public event EventHandler? RequestRedraw;

    /// <summary>Raised when the canvas should recentre on the circuit.</summary>
    public event EventHandler? RequestZoomToFit;

    /// <summary>Raised when the selected component should be rotated.</summary>
    public event EventHandler? RequestRotateSelection;

    /// <summary>Raised when the selection should be deleted.</summary>
    public event EventHandler? RequestDeleteSelection;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Raised when the settings dialog should open.</summary>
    public event EventHandler? RequestSettings;

    [RelayCommand]
    private void ShowSettings() => RequestSettings?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RotateSelection() => RequestRotateSelection?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void DeleteSelection() => RequestDeleteSelection?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ZoomToFit() => RequestZoomToFit?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Exit() => RequestClose?.Invoke(this, EventArgs.Empty);

    // ---- simulation speed ------------------------------------------------

    /// <summary>Speed choices offered by the Simulate menu, replacing the old toolbar slider.</summary>
    public IReadOnlyList<SpeedOption> SpeedOptions { get; } =
    [
        new("Real time (1x)", 1.0),
        new("1/10 speed", 0.1),
        new("1/100 speed", 0.01),
        new("1/1000 speed", 1e-3),
        new("1/10000 speed", 1e-4),
        new("Maximum throughput", 0, isMaximum: true),
    ];

    [RelayCommand]
    private void SetSpeed(SpeedOption? option)
    {
        if (option is null) return;

        Simulation.IsMaximumThroughput = option.IsMaximum;
        if (!option.IsMaximum) Simulation.SpeedFactor = option.Factor;

        RefreshSpeedSelection();
        StatusMessage = $"Simulation speed: {option.Label}";
    }

    /// <summary>Marks whichever speed entry matches the controller's current setting.</summary>
    public void RefreshSpeedSelection()
    {
        foreach (var option in SpeedOptions)
        {
            option.IsSelected = option.IsMaximum
                ? Simulation.IsMaximumThroughput
                : !Simulation.IsMaximumThroughput &&
                  Math.Abs(Simulation.SpeedFactor - option.Factor) < option.Factor * 1e-6;
        }

        OnPropertyChanged(nameof(SpeedLabel));
    }

    /// <summary>Current speed for the status bar.</summary>
    public string SpeedLabel => Simulation.IsMaximumThroughput
        ? "max speed"
        : SpeedOptions.FirstOrDefault(o => o.IsSelected)?.Label
          ?? $"{Simulation.SpeedFactor:g2}x real";

    /// <summary>Keyboard reference, shown from the Help menu now that commands live in menus.</summary>
    public const string ShortcutReference = """
        Tools
          V            Select
          W            Wire
          P            Probe
          R            Rotate selection
          Delete       Delete selection
          Esc          Cancel the current wire / clear the selection

        View
          F            Zoom to fit
          Wheel        Zoom at the cursor
          Middle drag  Pan (or hold Space and drag)
          F9 / F10     Collapse the palette / properties panel

        Simulation
          F5           Run or pause
          F6           Single step
          F8           Reset

        File
          Ctrl+N       New circuit
          Ctrl+O       Open
          Ctrl+S       Save
          Ctrl+Shift+S Save as

        Canvas
          Double-click a switch, button or logic toggle to operate it.
        """;

    [RelayCommand]
    private async Task ShowShortcutsAsync()
    {
        if (FileDialogs is null) return;
        await FileDialogs.ReportAsync("Keyboard shortcuts", ShortcutReference);
    }

    partial void OnSelectedComponentChanged(CircuitComponent? value) => Inspector.Component = value;

    partial void OnPendingItemChanged(PaletteItem? value)
    {
        if (value is not null)
        {
            ActiveTool = EditorTool.Select;
            StatusMessage = $"Click the canvas to place a {value.Name}";
        }
    }

    /// <summary>
    /// Radio state for the Tools menu. One property per tool keeps the menu bindings simple and
    /// keeps the selection testable without a converter.
    /// </summary>
    public bool IsSelectTool
    {
        get => ActiveTool == EditorTool.Select;
        set { if (value) ActiveTool = EditorTool.Select; }
    }

    public bool IsWireTool
    {
        get => ActiveTool == EditorTool.Wire;
        set { if (value) ActiveTool = EditorTool.Wire; }
    }

    public bool IsProbeTool
    {
        get => ActiveTool == EditorTool.Probe;
        set { if (value) ActiveTool = EditorTool.Probe; }
    }

    public bool IsDeleteTool
    {
        get => ActiveTool == EditorTool.Delete;
        set { if (value) ActiveTool = EditorTool.Delete; }
    }

    /// <summary>Active tool name, shown in the status bar now that there is no tool toolbar.</summary>
    public string ActiveToolName => ActiveTool.ToString();

    partial void OnActiveToolChanged(EditorTool value)
    {
        foreach (var name in new[]
                 {
                     nameof(IsSelectTool), nameof(IsWireTool),
                     nameof(IsProbeTool), nameof(IsDeleteTool), nameof(ActiveToolName),
                 })
        {
            OnPropertyChanged(name);
        }

        if (value != EditorTool.Select) PendingItem = null;
        StatusMessage = value switch
        {
            EditorTool.Wire => "Wire tool: click a terminal, click empty space for corners, click a terminal to finish",
            EditorTool.Probe => "Probe tool: click a terminal to attach a scope probe",
            EditorTool.Delete => "Delete tool: click a component or wire to remove it",
            _ => "Select tool: drag to move, R to rotate, Delete to remove",
        };
    }

    private void OnCircuitChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Watch each component's own properties too, so editing a resistance marks the file dirty
        // and not just adding or removing parts.
        foreach (var component in e.OldItems?.OfType<CircuitComponent>() ?? [])
            component.PropertyChanged -= OnComponentChanged;

        foreach (var component in e.NewItems?.OfType<CircuitComponent>() ?? [])
            component.PropertyChanged += OnComponentChanged;

        Simulation.InvalidateTopology();
        IsModified = true;
    }

    private void OnComponentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CircuitComponent.IsSelected)) return;
        IsModified = true;
    }

    /// <summary>Pushes the scope's timebase into the engine so probe recording is decimated to match.</summary>
    private void ApplyScopeSampling()
    {
        Simulation.Settings.ProbeSampleInterval = Scope.SuggestedSampleInterval;

        // Keep the solver step comfortably finer than the sample interval so edges are resolved.
        var suggested = Math.Clamp(Scope.SuggestedSampleInterval / 4.0, 1e-12, 1e-4);
        Simulation.Settings.TimeStep = suggested;
        Simulation.Settings.MaxTimeStep = Math.Max(suggested, Scope.SuggestedSampleInterval);
    }

    /// <summary>Attaches a probe, called by the canvas when the probe tool is used.</summary>
    public void AttachProbe(Terminal terminal)
    {
        var probe = Scope.AddProbe(terminal);
        Simulation.Simulator?.ResolveProbes();
        StatusMessage = $"Probing {probe.Label}";
        RequestRedraw?.Invoke(this, EventArgs.Empty);
    }

    // ---- commands --------------------------------------------------------

    [RelayCommand]
    private void SelectTool(string tool) =>
        ActiveTool = Enum.TryParse<EditorTool>(tool, true, out var parsed) ? parsed : EditorTool.Select;

    [RelayCommand]
    private void PlayPause() => Simulation.TogglePlayPause();

    [RelayCommand]
    private void Step() => Simulation.StepOnce();

    [RelayCommand]
    private void ResetSimulation()
    {
        Simulation.ResetSimulation();
        Scope.ClearTraces();
        StatusMessage = "Simulation reset";
        RequestRedraw?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task NewCircuitAsync()
    {
        if (!await MayDiscardAsync()) return;

        Simulation.Pause();
        Circuit.Clear();
        SelectedComponent = null;
        Circuit.Title = "Untitled circuit";
        CurrentFilePath = null;
        IsModified = false;
        Simulation.InvalidateTopology();
        StatusMessage = "New circuit";
        RequestZoomToFit?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void LoadExample(ExampleCircuit? example)
    {
        if (example is null) return;

        Simulation.Pause();
        Circuit.Clear();
        SelectedComponent = null;
        example.Build(this);

        CurrentFilePath = null;
        Simulation.InvalidateTopology();
        Simulation.Rebuild();
        IsModified = false;
        StatusMessage = $"Loaded example: {example.Name}";
        RequestZoomToFit?.Invoke(this, EventArgs.Empty);
        RequestRedraw?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ChoosePaletteItem(PaletteItem? item) => PendingItem = item;

    /// <summary>Opens every palette group, or closes them all when they are already open.</summary>
    [RelayCommand]
    private void TogglePaletteGroups()
    {
        var expand = Palette.Any(c => !c.IsExpanded);
        foreach (var category in Palette) category.IsExpanded = expand;
    }

    [RelayCommand]
    private void SetIntegration(string method)
    {
        Simulation.Settings.Integration = method.Equals("BackwardEuler", StringComparison.OrdinalIgnoreCase)
            ? IntegrationMethod.BackwardEuler
            : IntegrationMethod.Trapezoidal;
        Simulation.InvalidateTopology();
        OnPropertyChanged(nameof(IntegrationName));
    }

    public string IntegrationName => Simulation.Settings.Integration.ToString();

    public void Dispose() => Simulation.Dispose();
}
