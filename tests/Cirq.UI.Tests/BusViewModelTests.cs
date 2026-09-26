using Cirq.Components.Annotations;
using Cirq.Components.Buses;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Putting a bus on the drawing. The point of the dialog is bulk: eight taps, numbered, in one
/// gesture — because D3 typed as D4 on one of sixteen is a mistake that looks right.
/// </summary>
public class BusViewModelTests
{
    [Fact]
    public void ItPlacesATapPerSignal()
    {
        var circuit = new Circuit();
        var vm = new BusViewModel(circuit) { Name = "D", First = 0, Width = 8, X = 100, Y = 200 };

        vm.PlaceCommand.Execute(null);

        var taps = circuit.Components.OfType<BusTap>().ToList();

        Assert.Equal(8, taps.Count);
        Assert.Equal(["D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7"], taps.Select(t => t.NetName));

        // In a column on the twenty-unit pitch every package here puts its pins on, so a tap lands
        // opposite the pin it is for.
        Assert.All(taps, t => Assert.Equal(100, t.X));
        Assert.Equal([200, 220, 240, 260, 280, 300, 320, 340], taps.Select(t => t.Y));
    }

    [Fact]
    public void TheLineIsLabelledWithTheRange()
    {
        var circuit = new Circuit();
        var vm = new BusViewModel(circuit) { Name = "ADDR", First = 0, Width = 16 };

        Assert.Equal("ADDR[0..15]", vm.Range);

        vm.PlaceCommand.Execute(null);

        var line = Assert.Single(circuit.Components.OfType<BusLine>());

        Assert.Equal("ADDR[0..15]", line.Label);

        // Standing on end beside the column, and long enough to run past both ends of it.
        Assert.Equal(90, line.RotationDegrees);
        Assert.True(line.Length > 16 * 20, $"the line is only {line.Length} long");
    }

    [Fact]
    public void TheLineCanBeLeftOut()
    {
        var circuit = new Circuit();
        var vm = new BusViewModel(circuit) { Width = 4, DrawTheLine = false };

        vm.PlaceCommand.Execute(null);

        Assert.Equal(4, circuit.Components.Count);
        Assert.Empty(circuit.Components.OfType<BusLine>());
    }

    [Fact]
    public void ABusCanStartSomewhereOtherThanZero()
    {
        var circuit = new Circuit();
        var vm = new BusViewModel(circuit) { Name = "A", First = 8, Width = 4 };

        Assert.Equal("A[8..11]", vm.Range);

        vm.PlaceCommand.Execute(null);

        Assert.Equal(["A8", "A9", "A10", "A11"],
            circuit.Components.OfType<BusTap>().Select(t => t.NetName));
    }

    [Fact]
    public void TheSummarySaysWhatWillBeMade()
    {
        var vm = new BusViewModel(new Circuit()) { Name = "D", Width = 8 };

        Assert.Contains("D[0..7]", vm.Summary);
        Assert.Contains("D0, D1, D2", vm.Summary);
        Assert.Contains("D7", vm.Summary);

        // A narrow bus is listed in full rather than elided to three and a last one.
        vm.Width = 3;

        Assert.Equal("D[0..2] — D0, D1, D2", vm.Summary);
    }

    [Fact]
    public void WhatWasPlacedIsHandedBackSoItCanBeSelected()
    {
        var circuit = new Circuit();
        var vm = new BusViewModel(circuit) { Width = 4 };

        var announced = 0;
        vm.Added += (_, _) => announced++;

        vm.PlaceCommand.Execute(null);

        Assert.Equal(1, announced);
        Assert.Equal(5, vm.Placed.Count);
        Assert.All(vm.Placed, p => Assert.Contains(p, circuit.Components));
    }

    [Fact]
    public void AnEmptyNameFallsBackRatherThanMakingNetsCalledNothing()
    {
        var circuit = new Circuit();
        var vm = new BusViewModel(circuit) { Name = "   ", Width = 2 };

        vm.PlaceCommand.Execute(null);

        Assert.Equal(["D0", "D1"], circuit.Components.OfType<BusTap>().Select(t => t.NetName));
    }
}
