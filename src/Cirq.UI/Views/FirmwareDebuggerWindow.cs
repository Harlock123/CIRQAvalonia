using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Views;

/// <summary>The ATmega328P's debugger: controls along the top, the listing on the left, the registers and I/O on the right.</summary>
public sealed class FirmwareDebuggerWindow : Window
{
    private readonly DispatcherTimer _timer;

    public FirmwareDebuggerWindow(FirmwareDebuggerViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;
        Title = model.Title;
        Width = 860;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));

        var mono = new FontFamily("Cascadia Mono, DejaVu Sans Mono, Consolas, Menlo, monospace");
        Button Button(string text, System.Windows.Input.ICommand command, string tip)
        {
            var button = new Button { Content = text, Command = command, Padding = new Thickness(12, 4) };
            ToolTip.SetTip(button, tip);
            return button;
        }

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(FirmwareDebuggerViewModel.Status)));

        var address = new TextBox { Width = 110, Watermark = "0x0034" };
        address.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(FirmwareDebuggerViewModel.BreakpointText)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) model.AddBreakpointCommand.Execute(null); };

        var controls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in new Control[]
                 {
                     Button("Continue", model.ContinueCommand, "Run on from where it stopped"),
                     Button("Pause", model.PauseCommand, "Stop the firmware where it is"),
                     Button("Step", model.StepCommand, "Run one instruction (or take one interrupt), then stop"),
                     new TextBlock { Text = "   Break at", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                     address,
                     Button("Add", model.AddBreakpointCommand, "Stop before the instruction at this byte address runs"),
                     Button("Clear All", model.ClearBreakpointsCommand, "Take every breakpoint away"),
                 })
        {
            c.Margin = new Thickness(0, 0, 6, 4);
            controls.Children.Add(c);
        }

        var listing = new ListBox { FontFamily = mono, FontSize = 13 };
        listing.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(FirmwareDebuggerViewModel.Lines)));
        listing.DoubleTapped += (_, _) => { if (listing.SelectedItem is DisassemblyLine line) model.ToggleBreakpoint(line.Address); };
        ToolTip.SetTip(listing, "Double-click a line to break there (●), or stop breaking there; ▶ is where the PC is");

        var registers = new TextBlock { FontFamily = mono, FontSize = 12.5 };
        registers.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(FirmwareDebuggerViewModel.Registers)));
        var io = new TextBlock { FontFamily = mono, FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0) };
        io.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(FirmwareDebuggerViewModel.Io)));

        var close = new Button { Content = "Close", MinWidth = 88, Padding = new Thickness(14, 5), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        body.Children.Add(listing);
        var right = new ScrollViewer { Content = new StackPanel { Children = { registers, io }, Margin = new Thickness(14, 0, 0, 0) } };
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var dock = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(status);
        dock.Children.Add(controls);
        dock.Children.Add(close);
        dock.Children.Add(body);
        Content = dock;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.F10) model.StepCommand.Execute(null);
        };

        model.Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => model.Refresh());
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
