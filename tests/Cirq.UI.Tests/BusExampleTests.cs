using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

public class BusExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return vm;
    }

    /// <summary>
    /// The shipped example, run for real: three bytes written over two wires and read back off
    /// the same two wires, through a bus master and a slave that both decode the protocol.
    /// </summary>
    [Fact]
    public void TheI2cExampleWritesAndReadsBackWhatItWrote()
    {
        using var vm = Load("I2C EEPROM");
        var master = vm.Circuit.Components.OfType<I2cMaster>().Single();
        var eeprom = vm.Circuit.Components.OfType<I2cEeprom>().Single();

        vm.Simulation.Simulator!.Run(20e-3);

        Assert.True(master.IsFinished);
        Assert.True(master.LastTransferAcknowledged);

        Assert.Equal(0x48, eeprom.Read(0));
        Assert.Equal([0x48, 0x49, 0x21], master.ReceivedBytes);
    }

    /// <summary>
    /// The shipped SPI example: a byte clocked into the register and latched onto the LEDs when
    /// the select line comes back up.
    /// </summary>
    [Fact]
    public void TheSpiExampleLandsItsByteOnTheOutputs()
    {
        using var vm = Load("SPI Shift Register");
        var register = vm.Circuit.Components.OfType<Ic74595>().Single();

        vm.Simulation.Simulator!.Run(2e-3);

        Assert.Equal(0x5A, register.Latched);
    }
}
