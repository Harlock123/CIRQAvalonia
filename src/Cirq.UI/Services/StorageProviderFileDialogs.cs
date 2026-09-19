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
