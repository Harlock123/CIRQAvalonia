using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
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

        RebuildPalette();

        Scope.SamplingChanged += (_, _) => ApplyScopeSampling();
        Inspector.ParameterChanged += (_, structural) =>
        {
            if (structural) Simulation.InvalidateTopology();
            RequestRedraw?.Invoke(this, EventArgs.Empty);
        };

        ControlPanel = new ControlPanelViewModel(Circuit);
        ControlPanel.ControlChanged += (_, structural) =>
        {
            // Exactly what the inspector does when a parameter is edited: a value can be picked up
            // on the next time point, but a change of shape means the engine has to rebuild.
            if (structural) Simulation.InvalidateTopology();

            IsModified = true;
            RequestRedraw?.Invoke(this, EventArgs.Empty);
        };

        Circuit.Components.CollectionChanged += OnCircuitChanged;
        Circuit.Wires.CollectionChanged += OnCircuitChanged;

        // Probes are part of the saved document too, so attaching one is an edit like any other.
        Circuit.Probes.CollectionChanged += OnCircuitChanged;

        ApplyScopeSampling();

        // Open on a working circuit rather than an empty canvas, already compiled and biased so
        // the transport controls do something the moment the window appears.
        Examples.LoadRcLowPass(this);
        Simulation.Rebuild();
        IsModified = false;
        History.Reset(Circuit);
    }

    /// <summary>Undo and redo for schematic edits.</summary>
    public UndoHistory History { get; } = new();

    public Circuit Circuit { get; }

    public SimulationController Simulation { get; }

    public ScopeViewModel Scope { get; }

    public InspectorViewModel Inspector { get; }

    /// <summary>The circuit's controls, gathered into one panel so they can be worked while it runs.</summary>
    public ControlPanelViewModel ControlPanel { get; }

    /// <summary>
    /// Palette groups, each collapsible, narrowing to what a search matches. They all start closed
    /// so every heading is on screen at once; the rest are one click away.
    /// </summary>
    public ObservableCollection<PaletteCategoryViewModel> Palette { get; } = [];

    /// <summary>
    /// What is typed in the palette's search box. Empty shows everything.
    /// <para>
    /// There are 183 parts in sixteen groups, and browsing only works if you already know which
    /// group a part lives in — that a 4017 is under "40xx Series" and an optocoupler under
    /// "Switching &amp; Isolation". Somebody who knows they want a 555 should not have to guess.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string PaletteSearch { get; set; } = string.Empty;

    /// <summary>How the palette describes what it is showing, under the search box.</summary>
    public string PaletteSummary => PaletteSearch.Trim().Length == 0
        ? $"{PaletteTotalCount} parts in {ComponentCatalog.Categories.Count} groups"
        : $"{PaletteMatchCount} of {PaletteTotalCount} match";

    /// <summary>True when a search has hidden everything, so the panel can say so.</summary>
    public bool IsPaletteEmpty => Palette.Count == 0;

    /// <summary>True while a search is narrowing the list, which shows the clear button.</summary>
    public bool IsPaletteSearching => PaletteSearch.Trim().Length > 0;

    private int PaletteMatchCount => Palette.Sum(c => c.Count);

    private static int PaletteTotalCount { get; } = ComponentCatalog.Categories.Sum(c => c.Items.Count);

    /// <summary>Keeps one palette group open at a time.</summary>
    private readonly Accordion _paletteAccordion = new();

    /// <summary>
    /// The group that was open before a search started. It cannot be read off the list while one
    /// is running, because a search opens every group it matched and that is the search's doing
    /// rather than the person's.
    /// </summary>
    private string? _openPaletteGroup;

    private bool _wasSearchingPalette;

    partial void OnPaletteSearchChanged(string value) => RebuildPalette();

    /// <summary>
    /// Rebuilds the palette for the current search.
    /// <para>
    /// A search opens every group that still has something in it, the way the example browser
    /// does: leaving a match folded inside a closed group is the same as not matching at all.
    /// Clearing the search puts back the one group that was open before it started.
    /// </para>
    /// </summary>
    private void RebuildPalette()
    {
        var term = PaletteSearch.Trim();
        var searching = term.Length > 0;

        // Only believe what is on screen when the person put it there.
        if (!_wasSearchingPalette) _openPaletteGroup = Palette.FirstOrDefault(c => c.IsExpanded)?.Name;

        _wasSearchingPalette = searching;

        Palette.Clear();

        foreach (var category in ComponentCatalog.Categories)
        {
            IReadOnlyList<PaletteItem> matching = !searching
                ? category.Items
                : [.. category.Items.Where(i => Matches(i, category.Name, term))];

            if (matching.Count == 0) continue;

            Palette.Add(new PaletteCategoryViewModel(
                category.Name, matching, isExpanded: searching || category.Name == _openPaletteGroup));
        }

        // The groups are new objects each time round, so the accordion is pointed at the new ones.
        _paletteAccordion.Track(Palette);

        OnPropertyChanged(nameof(IsPaletteEmpty));
        OnPropertyChanged(nameof(IsPaletteSearching));
        OnPropertyChanged(nameof(PaletteSummary));
    }

    /// <summary>
    /// Matches on the name, the description and the group.
    /// <para>
    /// The description matters as much as the name: people look for what a part <i>does</i> — a
    /// "shift register", something "optical", a "crystal" — at least as often as they look for
    /// the number printed on it. And the group counts, so typing "logic" gives you the logic
    /// gates even though not one of them has the word in its name.
    /// </para>
    /// </summary>
    private static bool Matches(PaletteItem item, string category, string term) =>
        item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || category.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts the search back to nothing, which is what the little cross does.</summary>
    [RelayCommand]
    private void ClearPaletteSearch() => PaletteSearch = string.Empty;

    /// <summary>
    /// Arms the first part the search matched, so a part can be found and placed without the mouse
    /// ever leaving the keyboard: Ctrl+F, type, Enter, click where it goes.
    /// </summary>
    [RelayCommand]
    private void ChooseFirstMatch()
    {
        if (Palette.FirstOrDefault()?.Items.FirstOrDefault() is { } first) ChoosePaletteItem(first);
    }

    /// <summary>Raised when the palette's search box should take the keyboard.</summary>
    public event EventHandler? FocusPaletteSearchRequested;

    /// <summary>Opens the palette if it is collapsed and puts the caret in the search box.</summary>
    [RelayCommand]
    private void FocusPaletteSearch()
    {
        IsPaletteExpanded = true;
        FocusPaletteSearchRequested?.Invoke(this, EventArgs.Empty);
    }


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

    /// <summary>
    /// Whether resting the pointer on a part describes it where it sits.
    /// <para>
    /// The question it answers is "what is this one set to?", which otherwise costs a click to
    /// select the part and a look across at the properties panel — eight clicks to find which of
    /// eight resistors is the 4k7. On for anyone who has not turned it off, because the cost of it
    /// being wrong is a card you did not want for as long as the pointer is still.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool ShowHoverDetails { get; set; } = true;

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
        History.Reset(Circuit);
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

    /// <summary>
    /// Plays what the speakers in this circuit have recorded, in whatever the desktop plays audio
    /// with.
    /// <para>
    /// A waveform and a sound are different evidence about the same circuit, and the second one is
    /// the one an amplifier is judged by. Set <c>Recording Path</c> on a speaker, run it, and this
    /// is how you hear the result.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void PlayRecording()
    {
        var speakers = Circuit.Components.OfType<Speaker>().Where(s => s.IsRecording).ToList();

        if (speakers.Count == 0)
        {
            StatusMessage =
                "No speaker here is recording. Select one and set its Recording Path to a .wav file.";
            return;
        }

        // Everything captured since the last flush, so what plays is what is on the scope rather
        // than what was there a quarter of a second ago.
        foreach (var speaker in speakers) speaker.Flush();

        var recorded = speakers.Where(s => s.RecordedSeconds > 0).ToList();

        if (recorded.Count == 0)
        {
            StatusMessage = "Nothing has been recorded yet — run the circuit first.";
            return;
        }

        // The loudest, when there is more than one, because that is the one worth hearing.
        var chosen = recorded.MaxBy(s => s.RecordedPeak)!;

        StatusMessage = AudioPlayback.Play(chosen.RecordingPath);
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

    /// <summary>
    /// Draws the scope for an export. Set by the window once the panel exists, because the plot
    /// belongs to a control and a view model has no business reaching for one.
    /// </summary>
    public IScopeSource? ScopeSource { get; set; }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (FileDialogs is null) return;

        var request = await FileDialogs.PickExportAsync(DocumentName, ScopeSource?.HasTraces == true);
        if (request is null) return;

        try
        {
            var written = CircuitExporter.Export(Circuit, ScopeSource, request.Path, request.Options);

            StatusMessage = written.Count == 1
                ? $"Exported {Path.GetFileName(written[0])}"
                : $"Exported {written.Count} files to {Path.GetDirectoryName(written[0])}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await FileDialogs.ReportAsync("Export failed", ex.Message);
        }
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

    /// <summary>Raised with the factor to multiply the zoom by.</summary>
    public event EventHandler<double>? RequestZoomBy;

    /// <summary>Raised when the selected component should be rotated.</summary>
    public event EventHandler? RequestRotateSelection;

    /// <summary>Raised when the selection should be deleted.</summary>
    public event EventHandler? RequestDeleteSelection;

    /// <summary>Raised when the pasted component should be selected on the canvas.</summary>
    public event EventHandler<IReadOnlyList<CircuitComponent>>? RequestSelect;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Raised when the print dialog should open.</summary>
    public event EventHandler? RequestPrint;

    [RelayCommand]
    private void Print() => RequestPrint?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when the settings dialog should open.</summary>
    public event EventHandler? RequestSettings;

    /// <summary>Raised when the About dialog should be shown.</summary>
    public event EventHandler? RequestAbout;

    /// <summary>Raised when the frequency-response window should be opened.</summary>
    public event EventHandler? RequestFrequencyResponse;

    /// <summary>Raised when the DC sweep window should be opened.</summary>
    public event EventHandler? RequestDcSweep;

    /// <summary>Raised when the parameter-step window should be opened.</summary>
    public event EventHandler? RequestTransientStep;

    /// <summary>Raised when the noise window should be opened.</summary>
    public event EventHandler? RequestNoise;

    /// <summary>Raised when the stability window should be opened.</summary>
    public event EventHandler? RequestStability;

    /// <summary>Raised when the rule-check window should be opened.</summary>
    public event EventHandler? RequestRuleCheck;

    /// <summary>Raised when the spectrum window should be opened.</summary>
    public event EventHandler? RequestSpectrum;

    /// <summary>Raised when the bus decode window should be opened.</summary>
    public event EventHandler? RequestBusDecode;

    /// <summary>Raised when the tolerance analysis window should be opened.</summary>
    public event EventHandler? RequestMonteCarlo;

    /// <summary>Raised when the run-conditions window should be opened.</summary>
    public event EventHandler? RequestConditions;

    /// <summary>Raised when the example browser should be opened.</summary>
    public event EventHandler? RequestExamples;

    [RelayCommand]
    private void ShowSettings() => RequestSettings?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Undo()
    {
        var label = History.UndoLabel;
        if (Restore(History.Undo())) StatusMessage = $"Undid: {label}";
    }

    [RelayCommand]
    private void Redo()
    {
        var label = History.RedoLabel;
        if (Restore(History.Redo())) StatusMessage = $"Redid: {label}";
    }

    /// <summary>
    /// Puts a snapshot back. The same path a file open takes, because a snapshot is the same
    /// thing a file is — and recording is suspended throughout, or restoring would itself be
    /// recorded as an edit.
    /// </summary>
    private bool Restore(string? json)
    {
        if (json is null) return false;

        Simulation.Pause();
        History.Suspend();

        try
        {
            var result = CircuitSerializer.FromJson(json);
            ReplaceCircuitWith(result.Circuit);
        }
        finally
        {
            History.Resume(Circuit);
        }

        IsModified = true;
        Simulation.InvalidateTopology();
        Simulation.Rebuild();

        RequestRedraw?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Suspends recording for the span of a gesture, so a drag across the canvas costs one undo
    /// step rather than one per pointer movement.
    /// <para>
    /// The same scope grouping uses, held open between the two calls instead of wrapped round a
    /// block, because a drag begins and ends on separate input events. It records on <i>exit</i>,
    /// which is what makes the step's before-state the one from before the gesture.
    /// </para>
    /// </summary>
    public void BeginInteractiveEdit(string label)
    {
        // A gesture already in progress stays in charge; a pointer-down during one would
        // otherwise end it early and split it in two.
        _gesture ??= History.Gesture(Circuit, label);
    }

    /// <summary>Ends a gesture begun with <see cref="BeginInteractiveEdit"/>.</summary>
    public void EndInteractiveEdit()
    {
        _gesture?.Dispose();
        _gesture = null;
    }

    private IDisposable? _gesture;

    [RelayCommand]
    private void RotateSelection() => RequestRotateSelection?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void DeleteSelection() => RequestDeleteSelection?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Groups the selection into a block: one symbol on the sheet, with a pin wherever a wire
    /// crossed the boundary.
    /// <para>
    /// The parts are moved inside rather than copied, so probes on them keep reading them and
    /// ungrouping gives back the same parts. Nothing about the circuit changes — the hierarchy is
    /// flattened before anything is solved.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void GroupSelection()
    {
        var components = Selection();

        if (components.Count < 2)
        {
            StatusMessage = "Select two or more parts to group into a block.";
            return;
        }

        // One undo step for the whole gesture. Grouping removes each part and then adds the
        // block, and recorded as separate steps a single undo lands between them — the parts
        // removed, the block not yet added, and the parts gone.
        Subcircuit? block;

        using (History.Gesture(Circuit, "Group into Block"))
        {
            block = Grouping.Group(Circuit, components, "Block");
        }

        if (block is null)
        {
            StatusMessage = "Those parts cannot be grouped.";
            return;
        }

        SelectedComponent = block;
        foreach (var component in Circuit.Components) component.IsSelected = false;

        Simulation.InvalidateTopology();
        IsModified = true;
        RequestRedraw?.Invoke(this, EventArgs.Empty);

        StatusMessage =
            $"Grouped {Describe(block.InnerComponents.Count)} into {block.Name}, " +
            $"with {Describe(block.Ports.Count, "pin")}";
    }

    /// <summary>
    /// Puts a block's contents back on the sheet. This is also how you edit what is inside one:
    /// ungroup it, change it, group it again.
    /// </summary>
    [RelayCommand]
    private void UngroupSelection()
    {
        var block = Selection().OfType<Subcircuit>().FirstOrDefault()
                    ?? SelectedComponent as Subcircuit;

        if (block is null)
        {
            StatusMessage = "Select a block to ungroup.";
            return;
        }

        IReadOnlyList<CircuitComponent> released;

        using (History.Gesture(Circuit, "Ungroup Block"))
        {
            released = Grouping.Ungroup(Circuit, block);
        }

        SelectedComponent = null;
        foreach (var component in Circuit.Components)
            component.IsSelected = released.Contains(component);

        Simulation.InvalidateTopology();
        IsModified = true;
        RequestRedraw?.Invoke(this, EventArgs.Empty);

        StatusMessage = $"Ungrouped {Describe(released.Count)} back onto the sheet";
    }

    /// <summary>
    /// Blocks saved for reuse, shared across circuits and across runs.
    /// <para>
    /// Settable so a test can point it at a temporary file. It is one of the few things here that
    /// writes outside the document, and a test suite that quietly edited the person's own library
    /// would be a poor trade for the convenience.
    /// </para>
    /// </summary>
    public BlockLibrary Blocks { get; set; } = new();

    /// <summary>Raised when the block library window should be opened.</summary>
    public event EventHandler? RequestBlockLibrary;

    [RelayCommand]
    private void ShowBlockLibrary() => RequestBlockLibrary?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Models imported from SPICE cards, registered at startup so a circuit naming one finds it.
    /// Settable so a test can point it at a temporary file rather than the person's own library.
    /// </summary>
    public UserModelStore UserModels { get; set; } = new();

    /// <summary>Raised when the SPICE import window should be opened.</summary>
    public event EventHandler? RequestSpiceImport;

    [RelayCommand]
    private void ShowSpiceImport() => RequestSpiceImport?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Saves the selected block to the library under a name, so it can be placed again — here or
    /// in another circuit.
    /// </summary>
    public bool SaveBlock(string name)
    {
        var block = Selection().OfType<Subcircuit>().FirstOrDefault()
                    ?? SelectedComponent as Subcircuit;

        if (block is null)
        {
            StatusMessage = "Select a block to save to the library.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "A saved block needs a name.";
            return false;
        }

        var saved = Blocks.Save(name, block);

        StatusMessage = $"Saved '{saved.Name}' to the block library — {saved.Summary}";
        return true;
    }

    /// <summary>
    /// Places a copy of a saved block on the canvas. A copy, not a reference: two instances are
    /// independent, and editing one leaves the other alone.
    /// </summary>
    public Subcircuit? PlaceBlock(string name)
    {
        var block = Blocks.Create(name, Circuit);

        if (block is null)
        {
            StatusMessage = $"The library has nothing called '{name}'.";
            return null;
        }

        // Somewhere clear of what is already there, as a paste does.
        var bounds = Circuit.Components.Count == 0
            ? (X: 0.0, Y: 0.0)
            : (X: Circuit.Components.Max(c => c.X) + 160, Y: Circuit.Components.Average(c => c.Y));

        block.X = bounds.X;
        block.Y = bounds.Y;

        // One step, as for grouping: a placed block brings its contents with it, and the parts
        // inside it are named as it is added.
        using (History.Gesture(Circuit, $"Place {name}"))
        {
            Circuit.Components.Add(block);
        }

        SelectedComponent = block;
        foreach (var component in Circuit.Components) component.IsSelected = false;

        Simulation.InvalidateTopology();
        IsModified = true;
        RequestRedraw?.Invoke(this, EventArgs.Empty);

        StatusMessage = $"Placed {block.Name} from the library";
        return block;
    }

    /// <summary>
    /// What is selected: the band's catch if there is one, otherwise the single part the inspector
    /// is on. The same rule copy uses.
    /// </summary>
    private List<CircuitComponent> Selection()
    {
        var components = Circuit.Components.Where(c => c.IsSelected).ToList();

        if (components.Count == 0 && SelectedComponent is { } single) components.Add(single);

        return components;
    }

    /// <summary>The clipboard, which holds one part between a copy and a paste.</summary>
    public ComponentClipboard Clipboard { get; } = new();

    /// <summary>What the Edit menu's paste entry says, so it names what is waiting.</summary>
    public string PasteMenuText =>
        Clipboard.HasContent ? $"_Paste {Clipboard.HeldDescription}" : "_Paste";

    [RelayCommand]
    private void CopySelection()
    {
        // The selection is whatever carries the flag, which is one part after a click and as many
        // as the band caught after a drag.
        var components = Circuit.Components.Where(c => c.IsSelected).ToList();

        if (components.Count == 0 && SelectedComponent is { } single) components.Add(single);

        if (components.Count == 0)
        {
            StatusMessage = "Nothing selected to copy.";
            return;
        }

        Clipboard.Copy(components, Circuit.Wires);

        OnPropertyChanged(nameof(PasteMenuText));
        PasteCommand.NotifyCanExecuteChanged();

        StatusMessage = Clipboard.WireCount > 0
            ? $"Copied {Describe(Clipboard.Count)} and {Describe(Clipboard.WireCount, "wire")}"
            : $"Copied {Describe(Clipboard.Count)}";
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        List<string> warnings = [];

        var pasted = Clipboard.PasteInto(Circuit, warnings);

        if (pasted.Count == 0)
        {
            StatusMessage = "Nothing on the clipboard.";
            return;
        }

        // Selecting the copy is the point of pasting it: it lands offset from the original and is
        // almost always about to be dragged somewhere.
        foreach (var component in Circuit.Components) component.IsSelected = pasted.Contains(component);
        foreach (var wire in Circuit.Wires) wire.IsSelected = false;

        SelectedComponent = pasted.Count == 1 ? pasted[0] : null;
        RequestSelect?.Invoke(this, pasted);

        StatusMessage = warnings.Count == 0
            ? $"Pasted {Describe(pasted.Count)}"
            : $"Pasted {Describe(pasted.Count)} — {warnings[0]}";
    }

    /// <summary>"1 part" rather than "1 parts", which is worth four lines to get right.</summary>
    private static string Describe(int count, string noun = "part") =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private bool CanPaste() => Clipboard.HasContent;

    [RelayCommand]
    private void ZoomToFit() => RequestZoomToFit?.Invoke(this, EventArgs.Empty);

    /// <summary>One step of zoom, matching what a wheel notch does.</summary>
    private const double ZoomStep = 1.25;

    [RelayCommand]
    private void ZoomIn() => RequestZoomBy?.Invoke(this, ZoomStep);

    [RelayCommand]
    private void ZoomOut() => RequestZoomBy?.Invoke(this, 1.0 / ZoomStep);

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
        Edit
          Ctrl+Z       Undo
          Ctrl+Y       Redo (Ctrl+Shift+Z works too)
          Ctrl+G       Group the selection into a block
          Ctrl+Shift+G Ungroup a block back onto the sheet

        Tools
          V            Select
          W            Wire
          P            Probe
          R            Rotate selection
          Delete       Delete selection
          Esc          Cancel the current wire / clear the selection

        View
          Ctrl + / -   Zoom in / out (the numeric keypad works too)
          F            Zoom to fit
          Wheel        Zoom at the cursor
          Middle drag  Pan (or hold Space and drag)
          F9 / F10     Collapse the palette / properties panel
          Ctrl+F       Find a part in the palette

        Simulation
          F5           Run or pause
          F6           Single step
          F7           Frequency response
          Shift+F7     DC sweep
          Ctrl+Shift+F7 Step a parameter across a transient
          Shift+F5     Noise
          Ctrl+F7      Stability — loop gain and phase margin
          F3           Spectrum of the traces
          Shift+F3     Decode the traces as a bus
          F4           Check circuit
          Shift+F4     Tolerance analysis
          F8           Reset

        File
          Ctrl+N       New circuit
          Ctrl+Shift+E Browse examples
          Ctrl+O       Open
          Ctrl+S       Save
          Ctrl+Shift+S Save as
          Ctrl+P       Print

        Canvas
          Double-click a switch, button or logic toggle to operate it.
        """;

    [RelayCommand]
    private async Task ShowShortcutsAsync()
    {
        if (FileDialogs is null) return;
        await FileDialogs.ReportAsync("Keyboard shortcuts", ShortcutReference);
    }

    [RelayCommand]
    private void ShowAbout() => RequestAbout?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the frequency-response window, which sweeps the circuit as it stands. It runs its own
    /// solve from the bias point rather than borrowing the transport's, so it can be opened while
    /// a transient is running without disturbing it.
    /// </summary>
    [RelayCommand]
    private void ShowFrequencyResponse() => RequestFrequencyResponse?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the DC sweep window, which steps a parameter and solves the operating point at each
    /// value. Like the frequency response it runs its own solve, so it can be opened mid-transient
    /// without disturbing what is on the scope.
    /// </summary>
    [RelayCommand]
    private void ShowDcSweep() => RequestDcSweep?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the parameter-step window, which runs the whole transient once per value of
    /// something and lays the results on top of each other. Where the DC sweep answers what a
    /// circuit settles at, this answers how it gets there.
    /// </summary>
    [RelayCommand]
    private void ShowTransientStep() => RequestTransientStep?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the noise window, which measures the floor under everything — how much noise the
    /// circuit makes at a node, and which part is making it.
    /// </summary>
    [RelayCommand]
    private void ShowNoise() => RequestNoise?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the stability window, which measures how much gain goes round a feedback loop and
    /// how close it is to going round it the wrong way.
    /// </summary>
    [RelayCommand]
    private void ShowStability() => RequestStability?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the rule check, which looks for the mistakes no component can report about itself
    /// because they are about how the parts are joined rather than about any one of them.
    /// </summary>
    [RelayCommand]
    private void ShowRuleCheck() => RequestRuleCheck?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the spectrum window, which transforms the traces the scope has already recorded. It
    /// is a measurement of the signal rather than of the circuit — the frequency response is the
    /// other one.
    /// </summary>
    [RelayCommand]
    private void ShowSpectrum() => RequestSpectrum?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the bus decode window, which reads the recorded traces as a protocol. It works on
    /// what the scope has already captured, so run the circuit first.
    /// </summary>
    [RelayCommand]
    private void ShowBusDecode() => RequestBusDecode?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the tolerance analysis, which rebuilds the circuit many times with its parts drawn
    /// from their tolerance bands. It is the only analysis here that asks whether the circuit
    /// works with the parts you can buy rather than the ones in the drawing.
    /// </summary>
    [RelayCommand]
    private void ShowMonteCarlo() => RequestMonteCarlo?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the run conditions, which at present means the temperature the whole circuit is at.
    /// </summary>
    [RelayCommand]
    private void ShowConditions() => RequestConditions?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Called when a run condition changes. It is a change to the document as much as a change to
    /// the engine — a circuit saved at 85 °C should reopen at 85 °C — so it marks the file dirty.
    /// </summary>
    public void OnConditionsChanged()
    {
        Simulation.InvalidateTopology();
        IsModified = true;
    }

    /// <summary>
    /// Selects the parts a rule-check finding is about. The inspector follows a single one, as it
    /// does for any selection, so the offending pin's settings are there to look at.
    /// </summary>
    public void Reveal(IReadOnlyList<CircuitComponent> components)
    {
        foreach (var component in Circuit.Components)
            component.IsSelected = components.Contains(component);

        foreach (var wire in Circuit.Wires) wire.IsSelected = false;

        SelectedComponent = components.Count == 1 ? components[0] : null;
        RequestRedraw?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the example browser. The examples used to be a submenu, which worked while there
    /// were a dozen of them and had become a list to scroll past by the time there were forty.
    /// </summary>
    [RelayCommand]
    private void ShowExamples() => RequestExamples?.Invoke(this, EventArgs.Empty);

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

        History.Capture(Circuit, DescribeChange(e));
    }

    /// <summary>A short name for a collection change, for the Edit menu's "Undo ..." text.</summary>
    private static string DescribeChange(NotifyCollectionChangedEventArgs e)
    {
        var added = e.NewItems?.Count ?? 0;
        var removed = e.OldItems?.Count ?? 0;

        var item = (e.NewItems ?? e.OldItems)?.OfType<object>().FirstOrDefault() switch
        {
            CircuitComponent c => c.ComponentType,
            WireSegment => "Wire",
            SignalProbe => "Probe",
            _ => "Item",
        };

        if (added > 0 && removed == 0) return $"Add {item}";
        if (removed > 0 && added == 0) return $"Delete {item}";
        return "Edit";
    }

    private void OnComponentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CircuitComponent component) return;
        if (!IsDocumentProperty(component, e.PropertyName)) return;

        IsModified = true;
        History.Capture(Circuit, $"Edit {e.PropertyName}");
    }

    /// <summary>
    /// Whether a property change is an edit to the document or just the device describing itself.
    /// <para>
    /// Components raise change notification for derived display properties as they run — a triac
    /// says so when it fires, a battery as it discharges — and treating those as edits meant that
    /// simply running a circuit marked the file modified and put an asterisk in the title bar. The
    /// test is the one the serializer uses: if the property would not be written to the file, it
    /// is not part of the document.
    /// </para>
    /// </summary>
    private static bool IsDocumentProperty(CircuitComponent component, string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return false;
        if (propertyName == nameof(CircuitComponent.IsSelected)) return false;

        return PersistedProperties.GetOrAdd(
            component.GetType(),
            static type => [.. ComponentReflection.EditableProperties(type).Select(p => p.Name)])
            .Contains(propertyName);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, HashSet<string>>
        PersistedProperties = new();

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

    /// <summary>
    /// Makes a terminal the selected probe's reference point, called by the canvas when a
    /// terminal is shift-clicked with the probe tool.
    /// <para>
    /// A differential or power probe measures between two points, and only one of them can be the
    /// one you clicked to create it. Shift-clicking the second is the whole of the interaction;
    /// shift-clicking the same terminal again clears it back to ground.
    /// </para>
    /// </summary>
    public void SetProbeReference(Terminal terminal)
    {
        if (Scope.SelectedProbe is not { } probe)
        {
            StatusMessage = "Select a trace first, then shift-click its reference point.";
            return;
        }

        var clearing = ReferenceEquals(probe.ReferenceTerminal, terminal);

        probe.ReferenceTerminal = clearing ? null : terminal;

        // A reference is only meaningful for the two-point kinds, so setting one says what was
        // meant rather than being silently ignored.
        if (!clearing && !probe.IsDerived) probe.Kind = ProbeKind.Differential;

        Simulation.Simulator?.ResolveProbes();

        StatusMessage = clearing
            ? $"{probe.Label} now measures against ground"
            : $"{probe.Label} now measures against {terminal}";

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
        History.Reset(Circuit);
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
        History.Reset(Circuit);
        StatusMessage = $"Loaded example: {example.Name}";
        RequestZoomToFit?.Invoke(this, EventArgs.Empty);
        RequestRedraw?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ChoosePaletteItem(PaletteItem? item) => PendingItem = item;

    /// <summary>
    /// Opens every palette group, or closes them all when they are already open — the way out of
    /// the one-at-a-time rule for anyone who would rather scroll one long list.
    /// </summary>
    [RelayCommand]
    private void TogglePaletteGroups() => _paletteAccordion.SetAll(Palette.Any(c => !c.IsExpanded));

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

    public void Dispose()
    {
        ControlPanel.Dispose();
        Simulation.Dispose();
    }
}
