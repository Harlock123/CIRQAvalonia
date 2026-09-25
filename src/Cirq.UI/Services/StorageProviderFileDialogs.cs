using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cirq.Components.Serialization;
using Cirq.UI.Views;

namespace Cirq.UI.Services;

/// <summary>
/// File dialogs backed by Avalonia's storage provider, plus two small modal windows for
/// confirmation and error reporting (Avalonia ships no message box of its own).
/// </summary>
public sealed class StorageProviderFileDialogs : ICircuitFileDialogs
{
    private readonly Window _owner;

    public StorageProviderFileDialogs(Window owner)
    {
        _owner = owner;
    }

    private static FilePickerFileType CircuitFileType => new("Circuit files")
    {
        Patterns = [$"*{CircuitSerializer.FileExtension}"],
        MimeTypes = ["application/json"],
    };

    public async Task<string?> PickOpenPathAsync()
    {
        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open circuit",
            AllowMultiple = false,
            FileTypeFilter = [CircuitFileType, FilePickerFileTypes.All],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickWaveformPathAsync()
    {
        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import waveform",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Waveform data")
                {
                    Patterns = ["*.csv", "*.txt", "*.wav", "*.wave"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save circuit",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = CircuitSerializer.FileExtension.TrimStart('.'),
            FileTypeChoices = [CircuitFileType],
            ShowOverwritePrompt = true,
        });

        var path = file?.TryGetLocalPath();
        if (path is null) return null;

        // Some platforms hand back a name without the extension; make sure it has one.
        return Path.HasExtension(path) ? path : path + CircuitSerializer.FileExtension;
    }

    public async Task<ExportRequest?> PickExportAsync(string suggestedFileName, bool hasTraces)
    {
        var options = await AskExportOptionsAsync(hasTraces);
        if (options is null) return null;

        var storage = TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null) return null;

        var extension = CircuitExporter.Extension(options.Format);

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export",
            SuggestedFileName = suggestedFileName + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [ExportFileType(options.Format)],
            ShowOverwritePrompt = true,
        });

        var path = file?.TryGetLocalPath();
        if (path is null) return null;

        // As with saving, some platforms hand back a bare name.
        if (!Path.HasExtension(path)) path += extension;

        return new ExportRequest(path, options);
    }

    private static FilePickerFileType ExportFileType(ExportFormat format) => format switch
    {
        ExportFormat.Png => new FilePickerFileType("PNG image")
            { Patterns = ["*.png"], MimeTypes = ["image/png"] },
        ExportFormat.Jpeg => new FilePickerFileType("JPEG image")
            { Patterns = ["*.jpg", "*.jpeg"], MimeTypes = ["image/jpeg"] },
        ExportFormat.Bmp => new FilePickerFileType("Bitmap image")
            { Patterns = ["*.bmp"], MimeTypes = ["image/bmp"] },
        ExportFormat.Svg => new FilePickerFileType("SVG drawing")
            { Patterns = ["*.svg"], MimeTypes = ["image/svg+xml"] },
        ExportFormat.Netlist => new FilePickerFileType("SPICE netlist")
            { Patterns = ["*.cir", "*.net", "*.sp"], MimeTypes = ["text/plain"] },
        ExportFormat.Csv => new FilePickerFileType("Comma-separated values")
            { Patterns = ["*.csv"], MimeTypes = ["text/csv"] },
        ExportFormat.Bom => new FilePickerFileType("Parts list")
            { Patterns = ["*.csv"], MimeTypes = ["text/csv"] },
        _ => new FilePickerFileType("PDF document")
            { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] },
    };

    /// <summary>
    /// The export dialog. Built in code like the other two, which keeps the whole file-dialog
    /// surface in one place rather than spreading it across another XAML file.
    /// </summary>
    private async Task<ExportOptions?> AskExportOptionsAsync(bool hasTraces)
    {
        ExportOptions? chosen = null;

        var schematicOnly = Radio("group-content", "Schematic", isChecked: true);
        var tracesOnly = Radio("group-content", "Oscilloscope traces", isEnabled: hasTraces);
        var both = Radio("group-content", "Both", isEnabled: hasTraces);

        var oneFile = Radio("group-layout", "One file, schematic above the traces", isChecked: true);
        var separate = Radio("group-layout", "A file each, plus a combined PDF");

        var format = new ComboBox
        {
            ItemsSource = new[]
            {
                "PNG  —  image, good for anything",
                "JPEG  —  image, smaller and lossy",
                "BMP  —  image, uncompressed",
                "SVG  —  vector, scales and stays editable",
                "PDF  —  vector, for documents and printing",
                "SPICE netlist  —  the circuit as text, for ngspice or LTspice",
                "CSV  —  the recorded traces as numbers, for a spreadsheet",
                "Parts list  —  a bill of materials, as CSV",
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var scale = new ComboBox
        {
            ItemsSource = new[] { "1× — screen size", "2× — crisp", "3× — large" },
            SelectedIndex = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var transparent = new CheckBox { Content = "Transparent background" };

        var partsList = new CheckBox
        {
            Content = "Include a parts list",
            [ToolTip.TipProperty] =
                "A table of what the circuit is made of: quantity, designators, part and value",
        };

        var contentPanel = new StackPanel
        {
            Spacing = 4,
            Children = { Caption("What to export"), schematicOnly, tracesOnly, both },
        };

        var layoutPanel = new StackPanel
        {
            Spacing = 4,
            IsEnabled = false,
            Children = { Caption("When exporting both"), oneFile, separate },
        };

        // A sentence about whichever format is chosen, for the two whose behaviour is not obvious
        // from the name.
        var note = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
            IsVisible = false,
        };

        var scalePanel = new StackPanel
        {
            Spacing = 4,
            Children = { Caption("Resolution"), scale },
        };

        void Refresh()
        {
            var chosenFormat = (ExportFormat)format.SelectedIndex;
            var text = CircuitExporter.IsText(chosenFormat);

            // A text export has no layout, no resolution and no background, and what it contains
            // is decided by which of the two it is rather than by the content buttons: there is no
            // such thing as a netlist of an oscilloscope. So the rest of the dialog goes quiet
            // rather than offering settings that would be ignored.
            contentPanel.IsEnabled = !text;
            layoutPanel.IsEnabled = !text && both.IsChecked == true;
            scalePanel.IsEnabled = !text && CircuitExporter.IsRaster(chosenFormat);

            partsList.IsEnabled = !text;

            // JPEG has no alpha channel at all, so offering the option would be a lie.
            transparent.IsEnabled = !text && chosenFormat != ExportFormat.Jpeg;
            if (!transparent.IsEnabled) transparent.IsChecked = false;

            note.Text = chosenFormat switch
            {
                ExportFormat.Netlist =>
                    "Writes the circuit as a SPICE deck. Parts with no SPICE equivalent — logic, " +
                    "buses, sensors — are named in the file as comments rather than left out.",
                ExportFormat.Csv =>
                    "Writes everything the probes have recorded, not just the window on screen.",
                ExportFormat.Bom =>
                    "A bill of materials: one line per part and value, with the designators that " +
                    "share it. The circuit already knows all of this.",
                _ => string.Empty,
            };

            note.IsVisible = note.Text.Length > 0;
        }

        foreach (var button in new[] { schematicOnly, tracesOnly, both })
            button.IsCheckedChanged += (_, _) => Refresh();

        format.SelectionChanged += (_, _) => Refresh();
        Refresh();

        var dialog = new Window
        {
            Title = "Export",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,

        };

        // Bound rather than fetched, and that is the whole of the bug these dialogs had.
        //
        // Both of them are built at startup, before the theme variant has settled: asked at that
        // moment, the application says Light, and it becomes Dark a moment later once the windows
        // are realised. Fetching a brush there froze the light one into a dialog the rest of the
        // application then painted dark around — pale buttons on a pale panel, which is how it was
        // reported: "the only thing I can see is the text". Binding follows.
        dialog[!Window.BackgroundProperty] = new DynamicResourceExtension("PanelBackground");

        var ok = new Button
        {
            Content = "Export...",
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };

        ok.Click += (_, _) =>
        {
            var content = tracesOnly.IsChecked == true ? ExportContent.Traces
                : both.IsChecked == true ? ExportContent.Both
                : ExportContent.Schematic;

            chosen = new ExportOptions(
                (ExportFormat)format.SelectedIndex,
                content,
                AsSingleFile: oneFile.IsChecked == true,
                RasterScale: scale.SelectedIndex + 1,
                TransparentBackground: transparent.IsChecked == true,
                IncludePartsList: partsList.IsChecked == true);

            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                contentPanel,
                layoutPanel,
                new StackPanel
                {
                    Spacing = 4,
                    Children = { Caption("Format"), format },
                },
                note,
                scalePanel,
                transparent,
                partsList,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        await dialog.ShowModal(_owner);
        return chosen;
    }

    private static RadioButton Radio(string group, string text, bool isChecked = false, bool isEnabled = true) =>
        new()
        {
            GroupName = group,
            Content = text,
            IsChecked = isChecked,
            IsEnabled = isEnabled,
        };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.7,
    };

    public Task<bool> ConfirmDiscardChangesAsync(string circuitTitle) =>
        ShowDialogAsync(
            "Unsaved changes",
            $"'{circuitTitle}' has changes that have not been saved.\n\nDiscard them?",
            confirmText: "Discard",
            cancelText: "Cancel");

    public Task<bool> ConfirmRecoveryAsync(string name, string age) =>
        ShowDialogAsync(
            "Recover unsaved work",
            $"CirqAvalonia was working on {name} when it last stopped, and kept a copy from " +
            $"{age}.",
            confirmText: "Recover",
            cancelText: "Discard");

    public async Task ReportAsync(string title, string message) =>
        await ShowDialogAsync(title, message, confirmText: "OK", cancelText: null);

    /// <summary>
    /// A small modal built in code. Two buttons at most, which is all the file flows need, and it
    /// saves carrying another XAML file around for it.
    /// </summary>
    private async Task<bool> ShowDialogAsync(string title, string message, string confirmText, string? cancelText)
    {
        var result = false;

        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,

        };

        // Bound rather than fetched, and that is the whole of the bug these dialogs had.
        //
        // Both of them are built at startup, before the theme variant has settled: asked at that
        // moment, the application says Light, and it becomes Dark a moment later once the windows
        // are realised. Fetching a brush there froze the light one into a dialog the rest of the
        // application then painted dark around — pale buttons on a pale panel, which is how it was
        // reported: "the only thing I can see is the text". Binding follows.
        dialog[!Window.BackgroundProperty] = new DynamicResourceExtension("PanelBackground");

        confirm.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        if (cancelText is not null)
        {
            var cancel = new Button
            {
                Content = cancelText,
                MinWidth = 88,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsCancel = true,
            };
            cancel.Click += (_, _) => dialog.Close();
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(confirm);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                buttons,
            },
        };

        await dialog.ShowModal(_owner);
        return result;
    }
}
