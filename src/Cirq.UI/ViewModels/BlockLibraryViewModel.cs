using System.Collections.ObjectModel;
using Cirq.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The block library window: what is saved, putting the selection in, and taking a copy out.
/// </summary>
public sealed partial class BlockLibraryViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public BlockLibraryViewModel(MainWindowViewModel main)
    {
        _main = main;
        Refresh();
    }

    /// <summary>What is in the library, newest first.</summary>
    public ObservableCollection<SavedBlock> Blocks { get; } = [];

    [ObservableProperty]
    public partial SavedBlock? Selected { get; set; }

    /// <summary>The name the selection will be saved under.</summary>
    [ObservableProperty]
    public partial string NameToSave { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    public bool IsEmpty => Blocks.Count == 0;

    /// <summary>Where the library file lives, so it can be found or backed up.</summary>
    public string Location => _main.Blocks.Location;

    [RelayCommand]
    public void Save()
    {
        if (!_main.SaveBlock(NameToSave))
        {
            Status = _main.StatusMessage;
            return;
        }

        NameToSave = string.Empty;
        Refresh();
        Status = _main.StatusMessage;
    }

    [RelayCommand]
    public void Place()
    {
        if (Selected is null)
        {
            Status = "Choose a block to place.";
            return;
        }

        _main.PlaceBlock(Selected.Name);
        Status = _main.StatusMessage;
    }

    [RelayCommand]
    public void Remove()
    {
        if (Selected is null)
        {
            Status = "Choose a block to remove.";
            return;
        }

        var name = Selected.Name;

        _main.Blocks.Remove(name);
        Refresh();

        Status = $"Removed '{name}' from the library";
    }

    private void Refresh()
    {
        Blocks.Clear();

        foreach (var block in _main.Blocks.Blocks) Blocks.Add(block);

        Selected = Blocks.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
