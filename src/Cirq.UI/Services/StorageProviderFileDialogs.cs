using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cirq.Components.Serialization;

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

        var layoutPanel = new StackPanel
        {
            Spacing = 4,
            IsEnabled = false,
            Children = { Caption("When exporting both"), oneFile, separate },
        };

        var scalePanel = new StackPanel
        {
            Spacing = 4,
            Children = { Caption("Resolution"), scale },
        };

        void Refresh()
        {
            layoutPanel.IsEnabled = both.IsChecked == true;

            var raster = CircuitExporter.IsRaster((ExportFormat)format.SelectedIndex);
            scalePanel.IsEnabled = raster;

            // JPEG has no alpha channel at all, so offering the option would be a lie.
            transparent.IsEnabled = (ExportFormat)format.SelectedIndex != ExportFormat.Jpeg;
            if (!transparent.IsEnabled) transparent.IsChecked = false;
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
            Background = ThemeManager.Brush("PanelBackground"),
            ShowInTaskbar = false,
        };

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
                new StackPanel
                {
                    Spacing = 4,
                    Children = { Caption("What to export"), schematicOnly, tracesOnly, both },
                },
                layoutPanel,
                new StackPanel
                {
                    Spacing = 4,
                    Children = { Caption("Format"), format },
                },
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

        await dialog.ShowDialog(_owner);
        return chosen;
    }

    private static RadioButton Radio(string group, string text, bool isChecked = false, bool isEnabled = true) =>
        new()
        {
            GroupName = group,
            Content = text,
            IsChecked = isChecked,
            IsEnabled = isEnabled,
            Foreground = ThemeManager.Brush("TextPrimary"),
        };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.7,
        Foreground = ThemeManager.Brush("TextPrimary"),
    };

    public Task<bool> ConfirmDiscardChangesAsync(string circuitTitle) =>
        ShowDialogAsync(
            "Unsaved changes",
            $"'{circuitTitle}' has changes that have not been saved.\n\nDiscard them?",
            confirmText: "Discard",
            cancelText: "Cancel");

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
            Background = ThemeManager.Brush("PanelBackground"),
            ShowInTaskbar = false,
        };

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
                    Foreground = ThemeManager.Brush("TextPrimary"),
                    FontSize = 13,
                },
                buttons,
            },
        };

        await dialog.ShowDialog(_owner);
        return result;
    }
}
