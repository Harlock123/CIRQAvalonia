using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// Reading an HD44780: the busy flag and the address counter with RS low, and the display memory
/// with RS high — what polling driver code, like the 6502 example's, depends on.
/// </summary>
public class CharacterLcdReadTests
{
    private sealed class Rig
    {
        public LogicBench Bench { get; } = new();
        public CharacterLcd Lcd { get; }
        public LogicToggle Rs { get; }
        public LogicToggle Rw { get; }
        public LogicToggle E { get; }
        public LogicToggle BufferOff { get; }
        public LogicToggle[] Data { get; }

        public Rig()
        {
            Lcd = Bench.Add(new CharacterLcd());
            Rs = Bench.Toggle(Lcd.RegisterSelect);
            Rw = Bench.Toggle(Lcd.ReadWrite);
            E = Bench.Toggle(Lcd.Enable);

            // The data lines through a 74244, so they can be let go of for a read.
            var buffer = Bench.Add(new Ic74244());
            BufferOff = Bench.Toggle(buffer.Enable1);
            Bench.Circuit.Connect(buffer.Enable2, buffer.Enable1);
            Data = Bench.Toggles(buffer.Inputs);
            for (var i = 0; i < 8; i++) Bench.Circuit.Connect(buffer.Outputs[i], Lcd.Data[i]);
        }

        public void Write(bool isData, int value)
        {
            Rw.State = false;
            BufferOff.State = false;
            Rs.State = isData;
            LogicBench.Set(Data, value);
            Bench.Run(1e-6);
            E.State = true;
            Bench.Run(1e-6);
            E.State = false;
            Bench.Run(1e-6);
            BufferOff.State = true;
            Bench.Run(1e-6);
        }

        public int Read(bool isData)
        {
            BufferOff.State = true;
            Rs.State = isData;
            Rw.State = true;
            Bench.Run(1e-6);
            E.State = true;
            Bench.Run(1e-6);
            var value = Bench.Byte(Lcd.Data);
            E.State = false;
            Bench.Run(1e-6);
            Rw.State = false;
            Bench.Run(1e-6);
            return value;
        }
    }

    [Fact]
    public void TheBusyFlagIsSetWhileACommandRunsAndTheAddressCounterFollowsTheCursor()
    {
        var rig = new Rig();

        rig.Write(false, 0x38);          // function set, eight bits
        Assert.Equal(0x80, rig.Read(false) & 0x80);

        rig.Bench.Run(40e-6);
        Assert.Equal(0x00, rig.Read(false));

        rig.Write(true, 'H');
        rig.Bench.Run(40e-6);
        rig.Write(true, 'i');
        rig.Bench.Run(40e-6);
        Assert.Equal(0x02, rig.Read(false));

        // A clear keeps it busy for a millisecond and a half.
        rig.Write(false, 0x01);
        rig.Bench.Run(1.0e-3);
        Assert.Equal(0x80, rig.Read(false) & 0x80);
        rig.Bench.Run(0.6e-3);
        Assert.Equal(0x00, rig.Read(false));
    }

    [Fact]
    public void ADataReadGivesTheCharacterAtTheCursorAndMovesOn()
    {
        var rig = new Rig();
        rig.Write(false, 0x38);
        rig.Bench.Run(40e-6);
        rig.Write(true, 'O');
        rig.Bench.Run(40e-6);
        rig.Write(true, 'K');
        rig.Bench.Run(40e-6);

        rig.Write(false, 0x80);          // back to address 0
        rig.Bench.Run(40e-6);
        Assert.Equal('O', rig.Read(true));
        Assert.Equal('K', rig.Read(true));
        Assert.Equal(0x02, rig.Read(false));

        // Not reading, it lets go of the data lines.
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(LogicState.HighImpedance, rig.Lcd.GetOutputState(i)));
    }
}
