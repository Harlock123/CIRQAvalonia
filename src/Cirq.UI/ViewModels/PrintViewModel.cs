using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// What to print and on what. The page arithmetic lives in <see cref="PageSetup"/>; this is the
/// dialog's own state and the sentence it shows about what will happen.
/// </summary>
public sealed partial class PrintViewModel : ObservableObject
{
    public PrintViewModel(bool hasTraces, bool canSpool)
    {
        HasTraces = hasTraces;
        CanSpool = canSpool;
    }

    /// <summary>False when there is nothing on the scope, so those choices are not offered.</summary>
    public bool HasTraces { get; }

    /// <summary>True when the system has a spooler and the document can go straight to it.</summary>
    public bool CanSpool { get; }

    public static IReadOnlyList<PaperSize> PaperOptions { get; } = Enum.GetValues<PaperSize>();

    public static IReadOnlyList<PageOrientation> OrientationOptions { get; } =
        Enum.GetValues<PageOrientation>();

    [ObservableProperty]
    public partial PaperSize Paper { get; set; } = PaperSize.A4;

    [ObservableProperty]
    public partial PageOrientation Orientation { get; set; } = PageOrientation.Landscape;

    [ObservableProperty]
    public partial bool IncludeSchematic { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeTraces { get; set; }

    [ObservableProperty]
    public partial bool IncludePartsList { get; set; }

    [ObservableProperty]
    public partial bool FitToPage { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeHeader { get; set; } = true;

    /// <summary>The page as the layout code wants it.</summary>
    public PageSetup Setup => new(Paper, Orientation, 36.0, FitToPage, IncludeHeader);

    /// <summary>What to export, worked out from the three checkboxes.</summary>
    public ExportContent Content =>
        IncludeSchematic && IncludeTraces ? ExportContent.Both
        : IncludeTraces ? ExportContent.Traces
        : ExportContent.Schematic;

    /// <summary>True when at least one thing is selected, so the button can be disabled.</summary>
    public bool HasSomethingToPrint => IncludeSchematic || IncludeTraces || IncludePartsList;

    /// <summary>How many sheets this will be.</summary>
    public int PageCount =>
        (IncludeSchematic ? 1 : 0) + (IncludeTraces ? 1 : 0) + (IncludePartsList ? 1 : 0);

    /// <summary>
    /// A sentence about what pressing the button will do — including, where there is no spooler,
    /// that it will open a viewer rather than print. Saying so is better than a menu item that
    /// tells a small lie every time it is used.
    /// </summary>
    public string Summary
    {
        get
        {
            if (!HasSomethingToPrint) return "Nothing selected to print.";

            var sheets = PageCount == 1 ? "One sheet" : $"{PageCount} sheets";
            var paper = Paper.ToString();
            var way = Orientation == PageOrientation.Landscape ? "landscape" : "portrait";

            return CanSpool
                ? $"{sheets} of {paper}, {way}, sent to your printer."
                : $"{sheets} of {paper}, {way}. This system has no print command, so the document " +
                  "will open in your PDF viewer — print it from there.";
        }
    }

    partial void OnPaperChanged(PaperSize value) => Refresh();

    partial void OnOrientationChanged(PageOrientation value) => Refresh();

    partial void OnIncludeSchematicChanged(bool value) => Refresh();

    partial void OnIncludeTracesChanged(bool value) => Refresh();

    partial void OnIncludePartsListChanged(bool value) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasSomethingToPrint));
    }
}
