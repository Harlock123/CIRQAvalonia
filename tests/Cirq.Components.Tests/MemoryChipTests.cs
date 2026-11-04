using Cirq.Components.Digital;
using Cirq.Components.Sources;
using Cirq.Components.Serialization;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// A circuit with a 5 V rail and a toggle for every input under test, run a microsecond at a time —
/// the bench the memory and bus-glue tests drive their chips from.
/// </summary>
internal sealed class LogicBench
{
    public Circuit Circuit { get; } = new();

    public DcVoltageSource Rail { get; }

    public Ground Ground { get; }

    private CircuitSimulator? _simulator;

    public LogicBench()
    {
        Rail = Circuit.Add(new DcVoltageSource(5.0));
        Ground = Circuit.Add(new Ground());
        Circuit.Connect(Rail.Negative, Ground.Pin);
    }

    public T Add<T>(T part) where T : DigitalIc
    {
        Circuit.Add(part);
        Circuit.Connect(part.Vcc, Rail.Positive);
        Circuit.Connect(part.Gnd, Ground.Pin);
        return part;
    }

    /// <summary>A toggle wired to a pin, starting at <paramref name="high"/>.</summary>
    public LogicToggle Toggle(Terminal pin, bool high = false)
    {
        var toggle = Circuit.Add(new LogicToggle { State = high });
        Circuit.Connect(toggle.Out, pin);
        return toggle;
    }

    /// <summary>Toggles for a set of pins, set to the bits of a number.</summary>
    public LogicToggle[] Toggles(IReadOnlyList<Terminal> pins, int value = 0)
    {
        var toggles = pins.Select((p, i) => Toggle(p, (value & (1 << i)) != 0)).ToArray();
        return toggles;
    }

    public void Tie(Terminal pin, bool high) => Circuit.Connect(pin, high ? Rail.Positive : Ground.Pin);

    public CircuitSimulator Simulator
    {
        get
        {
            if (_simulator is not null) return _simulator;
            _simulator = new CircuitSimulator(Circuit, new() { TimeStep = 50e-9, MaxTimeStep = 1e-6, UseInitialConditions = true });
            _simulator.Reset();
            _simulator.SolveOperatingPoint();
            return _simulator;
        }
    }

    public void Run(double seconds = 2e-6) => Simulator.Run(seconds);

    public LogicState Level(Terminal pin) => LogicLevels.Ttl.Classify(Simulator.NodeVoltage(pin));

    public bool High(Terminal pin) => Level(pin) == LogicState.High;

    /// <summary>The byte on eight pins, or −1 if any of them is not at a logic level.</summary>
    public int Byte(IReadOnlyList<Terminal> pins)
    {
        var value = 0;
        for (var i = 0; i < pins.Count; i++)
        {
            var level = Level(pins[i]);
            if (!level.IsDriven()) return -1;
            if (level.IsHigh()) value |= 1 << i;
        }

        return value;
    }

    public static void Set(LogicToggle[] toggles, int value)
    {
        for (var i = 0; i < toggles.Length; i++) toggles[i].State = (value & (1 << i)) != 0;
    }
}

/// <summary>
/// The byte-wide memories: the 27xx EPROMs, the 28C256 EEPROM, the 6116 and 62256 RAMs — their
/// pinouts, their reading and writing, their timing — and loading them from binary and Intel HEX.
/// </summary>
public class MemoryChipTests
{
    // ---- pinouts ---------------------------------------------------------

    /// <summary>The pin numbers on each part are the datasheet's, which is what lets a real board be copied.</summary>
    [Theory]
    [InlineData(typeof(Eprom2716), 24, 11, "p8=A0 p1=A7 p23=A8 p22=A9 p19=A10 p9=D0 p17=D7 p18=CE p20=OE p21=VPP p12=GND p24=VCC")]
    [InlineData(typeof(Eprom2732), 24, 12, "p8=A0 p21=A11 p19=A10 p18=CE p20=OE p12=GND p24=VCC")]
    [InlineData(typeof(Eprom2764), 28, 13, "p1=VPP p2=A12 p10=A0 p11=D0 p19=D7 p20=CE p21=A10 p22=OE p23=A11 p27=PGM p14=GND p28=VCC")]
    [InlineData(typeof(Eprom27C256), 28, 15, "p1=VPP p26=A13 p27=A14 p22=OE p20=CE p14=GND p28=VCC")]
    [InlineData(typeof(Eeprom28C256), 28, 15, "p1=A14 p2=A12 p26=A13 p27=WE p20=CE p22=OE p14=GND p28=VCC")]
    [InlineData(typeof(Sram6116), 24, 11, "p18=CE p20=OE p21=WE p19=A10 p12=GND p24=VCC")]
    [InlineData(typeof(Sram62256), 28, 15, "p1=A14 p26=A13 p27=WE p20=CE p22=OE p11=D0 p13=D2 p15=D3 p14=GND p28=VCC")]
    public void EachPartIsOnItsDatasheetPinout(Type type, int pins, int addressBits, string expected)
    {
        var part = (ParallelMemory)Activator.CreateInstance(type)!;

        Assert.Equal(pins, part.PinCount);
        Assert.Equal(addressBits, part.AddressBits);
        Assert.Equal(1 << addressBits, part.Size);

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=')[0], pair.Split('=')[1]);
            Assert.Equal(name, part.Terminals.Single(t => t.Id == id).Name);
        }

        // Every address and data line exists, and nothing has the same pin twice.
        Assert.Equal(addressBits, part.Address.Count(a => a is not null));
        Assert.Equal(8, part.Data.Count(d => d is not null));
        Assert.Equal(part.Terminals.Count, part.Terminals.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void AnEpromHasNoWriteEnableAndAcceptsNoWrites()
    {
        foreach (var part in new ParallelMemory[] { new Eprom2716(), new Eprom2732(), new Eprom2764(), new Eprom27C256() })
        {
            Assert.Null(part.WriteEnablePin);
            Assert.False(part.AcceptsWrites);
        }

        Assert.True(new Sram62256().AcceptsWrites);
    }

    // ---- reading ----------------------------------------------------------

    /// <summary>Selected and enabled, the addressed byte comes out, an access time after the address settles.</summary>
    [Fact]
    public void AnEpromAnswersAfterItsAccessTime()
    {
        var bench = new LogicBench();
        var rom = bench.Add(new Eprom27C256());
        rom.Contents = "@7FFE: 00 80 @1234: A5";

        var address = bench.Toggles(rom.Address, 0x1234);
        bench.Tie(rom.ChipEnable, false);
        bench.Tie(rom.OutputEnable, false);
        bench.Tie(rom.Terminals.Single(t => t.Name == "VPP"), true);

        bench.Run(1e-6);
        Assert.Equal(0xA5, bench.Byte(rom.Data));

        // The toggles move at the next time point, 50 ns on, and the data 150 ns after that.
        LogicBench.Set(address, 0x7FFF);
        bench.Run(150e-9);
        Assert.Equal(0xA5, bench.Byte(rom.Data));
        bench.Run(100e-9);
        Assert.Equal(0x80, bench.Byte(rom.Data));
    }

    /// <summary>Either control high, and the data lines let go — which is what makes them a bus.</summary>
    [Fact]
    public void DeselectedOrDisabledItLetsGoOfTheBus()
    {
        var bench = new LogicBench();
        var rom = bench.Add(new Eprom2764());
        rom.Contents = "FF";

        bench.Toggles(rom.Address);
        var ce = bench.Toggle(rom.ChipEnable);
        var oe = bench.Toggle(rom.OutputEnable);
        bench.Tie(rom.Terminals.Single(t => t.Name == "VPP"), true);
        bench.Tie(rom.Terminals.Single(t => t.Name == "PGM"), true);

        bench.Run();
        Assert.Equal(0xFF, bench.Byte(rom.Data));
        Assert.True(rom.IsReading);

        oe.State = true;
        bench.Run();
        Assert.Equal(LogicState.HighImpedance, rom.GetOutputState(0));

        oe.State = false;
        ce.State = true;
        bench.Run();
        Assert.Equal(LogicState.HighImpedance, rom.GetOutputState(7));
        Assert.False(rom.IsReading);
    }

    // ---- writing ----------------------------------------------------------

    /// <summary>A RAM takes the bus while /CE and /WE are low, and gives it back when read.</summary>
    [Fact]
    public void A62256IsWrittenAndReadBack()
    {
        var bench = new LogicBench();
        var ram = bench.Add(new Sram62256());

        var address = bench.Toggles(ram.Address, 0x4321);
        var ce = bench.Toggle(ram.ChipEnable, true);
        var oe = bench.Toggle(ram.OutputEnable, true);
        var we = bench.Toggle(ram.WriteEnable, true);

        var buffer = bench.Add(new Ic74244());
        bench.Tie(buffer.Enable2, true);
        var bufferEnable = bench.Toggle(buffer.Enable1, true);
        var data = bench.Toggles(buffer.Inputs, 0);
        for (var i = 0; i < 4; i++) bench.Circuit.Connect(buffer.Outputs[i], ram.Data[i]);

        // Drive 0x0C onto the low nibble and pulse /WE with the chip selected.
        LogicBench.Set(data, 0x0C);
        bufferEnable.State = false;
        ce.State = false;
        we.State = false;
        bench.Run();
        we.State = true;
        bench.Run();
        bufferEnable.State = true;
        bench.Run();

        Assert.Equal(0x0C, ram.ReadByte(0x4321));

        oe.State = false;
        bench.Run();
        Assert.Equal(0x0C, bench.Byte(ram.Data.Take(4).ToList()));

        // Another address reads its own byte.
        LogicBench.Set(address, 0x0001);
        bench.Run();
        Assert.Equal(0, bench.Byte(ram.Data.Take(4).ToList()));
    }

    /// <summary>
    /// A 28C256 write is a load and then a long internal cycle, polled through bit 7: the complement
    /// of what was written until the cells have it, then the byte itself.
    /// </summary>
    [Fact]
    public void A28C256WritesSlowlyAndCanBePolled()
    {
        var bench = new LogicBench();
        var rom = bench.Add(new Eeprom28C256 { WriteCycleTime = 1e-3 });

        bench.Toggles(rom.Address, 0x0010);
        bench.Tie(rom.ChipEnable, false);
        var oe = bench.Toggle(rom.OutputEnable, true);
        var we = bench.Toggle(rom.WriteEnable, true);

        var buffer = bench.Add(new Ic74244());
        var bufferEnable = bench.Toggle(buffer.Enable1, true);
        bench.Circuit.Connect(buffer.Enable2, buffer.Enable1);
        bench.Toggles(buffer.Inputs, 0x5A);
        for (var i = 0; i < 8; i++) bench.Circuit.Connect(buffer.Outputs[i], rom.Data[i]);

        bufferEnable.State = false;
        bench.Run();
        we.State = false;
        bench.Run();
        we.State = true;
        bench.Run();
        bufferEnable.State = true;
        bench.Run();

        // Not in the cells yet, and reading says so: bit 7 inverted, bit 6 toggling between reads.
        Assert.Equal(0, rom.ReadByte(0x10));
        oe.State = false;
        bench.Run();
        var first = bench.Byte(rom.Data);
        Assert.Equal(0x80, first & 0x80);   // 0x5A has bit 7 clear
        oe.State = true;
        bench.Run();
        oe.State = false;
        bench.Run();
        var second = bench.Byte(rom.Data);
        Assert.NotEqual(first & 0x40, second & 0x40);
        Assert.True(rom.IsBusy || bench.Simulator.Time < Eeprom28C256.ByteLoadWindow);

        // After the load window and the write cycle, the byte is there and reads back.
        bench.Run(Eeprom28C256.ByteLoadWindow + 1.1e-3);
        Assert.False(rom.IsBusy);
        Assert.Equal(1, rom.WritesCompleted);
        Assert.Equal(0x5A, rom.ReadByte(0x10));
        Assert.Equal(0x5A, bench.Byte(rom.Data));
    }

    // ---- files -------------------------------------------------------------

    [Fact]
    public void IntelHexIsReadWithItsAddresses()
    {
        var hex = MemoryImage.ToIntelHex(0x8000, [0xA9, 0x42, 0x8D, 0x00, 0x60]);
        var image = MemoryImage.FromIntelHex(hex);

        Assert.Equal(5, image.ByteCount);
        Assert.Equal(0x8000, image.Blocks[0].Address);

        // A 32K ROM sees $8000 as its own address zero, as it would with A15 not wired to it.
        var rom = new Eprom27C256();
        var note = rom.Load(image);
        Assert.Equal(0xA9, rom.ReadByte(0));
        Assert.Equal(0x60, rom.ReadByte(4));
        Assert.Contains("wrapped", note);
    }

    [Fact]
    public void ExtendedAddressRecordsMoveTheBase()
    {
        const string hex = """
            :020000021000EC
            :0100000055AA
            :02000004000AF0
            :01000000AA55
            :0400000300003800C1
            :00000001FF
            """;

        var image = MemoryImage.FromIntelHex(hex);

        Assert.Equal([0x10000, 0xA0000], image.Blocks.Select(b => b.Address));
        Assert.Equal(0x55, image.Blocks[0].Bytes[0]);
    }

    [Theory]
    [InlineData(":0100000055AB", "Line 1", "checksum")]
    [InlineData(":10000000\n", "Line 1", "too short")]
    [InlineData("0100000055AA", "Line 1", "':'")]
    [InlineData(":0100000055AA\n:0200000055AA", "Line 2", "says 2 data bytes")]
    [InlineData(":0100000655A4", "Line 1", "record type 06")]
    [InlineData(":0100000055AA\n:00000001FF\n:0100000055AA", "Line 3", "after the end-of-file")]
    public void ABadHexFileIsRefusedNamingTheLine(string hex, string line, string why)
    {
        var problem = Assert.Throws<InvalidDataException>(() => MemoryImage.FromIntelHex(hex));
        Assert.Contains(line, problem.Message);
        Assert.Contains(why, problem.Message);
    }

    [Fact]
    public void ABinaryGoesWhereTheOffsetSays()
    {
        var rom = new Eprom2716();
        rom.Load(MemoryImage.FromBinary([1, 2, 3], offset: 0x700));

        Assert.Equal(1, rom.ReadByte(0x700));
        Assert.Equal(3, rom.ReadByte(0x702));
        Assert.Equal(3, rom.UsedBytes);
    }

    [Fact]
    public void AnImageBiggerThanTheChipIsRefused()
    {
        var rom = new Eprom2716();
        var problem = Assert.Throws<InvalidDataException>(() => rom.Load(MemoryImage.FromBinary(new byte[4096])));
        Assert.Contains("bigger than this 2K part", problem.Message);
    }

    [Fact]
    public void FilesAreReadByTheirExtension()
    {
        var folder = Directory.CreateTempSubdirectory("cirq-image-");
        try
        {
            var bin = Path.Combine(folder.FullName, "rom.bin");
            File.WriteAllBytes(bin, [0xEA, 0xEA, 0x4C]);
            var hex = Path.Combine(folder.FullName, "rom.hex");
            File.WriteAllText(hex, MemoryImage.ToIntelHex(0x10, [7, 8]));

            Assert.Equal("binary", MemoryImage.Read(bin).Format);
            Assert.Equal(0x4C, MemoryImage.Read(bin, 0x100).Blocks[0].Bytes[2]);
            Assert.Equal(0x100, MemoryImage.Read(bin, 0x100).Blocks[0].Address);
            Assert.Equal("Intel HEX", MemoryImage.Read(hex).Format);
            Assert.Throws<FileNotFoundException>(() => MemoryImage.Read(Path.Combine(folder.FullName, "missing.bin")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A loaded image is the preset, so it is saved inside the circuit: the circuit opens complete,
    /// with no file to find, and a reset after a run that scribbled on a RAM puts the image back.
    /// </summary>
    [Fact]
    public void ALoadedImageTravelsInsideTheCircuitFile()
    {
        var bytes = Enumerable.Range(0, 32768).Select(i => (byte)(i * 7 + 3)).ToArray();
        var circuit = new Circuit();
        var rom = circuit.Add(new Eeprom28C256());
        rom.Load(MemoryImage.FromBinary(bytes));

        var json = CircuitSerializer.ToJson(circuit);
        var reopened = CircuitSerializer.FromJson(json).Circuit.Components.OfType<Eeprom28C256>().Single();

        Assert.Equal(bytes, reopened.Snapshot());

        rom.WriteByte(5, 0);
        rom.ResetLogic();
        Assert.Equal(bytes[5], rom.ReadByte(5));
    }

    /// <summary>The 2K part that came first keeps its name, its file form and its behaviour.</summary>
    [Fact]
    public void TheOriginal2KPartStillRoundTrips()
    {
        var circuit = new Circuit();
        circuit.Add(new MemoryDevice { IsReadOnly = true, Contents = "48 45 4C 4C 4F" });

        var json = CircuitSerializer.ToJson(circuit);
        Assert.Contains("\"MemoryDevice\"", json);

        var reopened = CircuitSerializer.FromJson(json).Circuit.Components.OfType<MemoryDevice>().Single();
        Assert.True(reopened.IsReadOnly);
        Assert.Equal("@000: 48 45 4C 4C 4F 00 00 00 00 00 00 00 00 00 00 00", reopened.Contents);
        Assert.Equal("ROM 2Kx8", reopened.PartNumber);
    }

    [Fact]
    public void AWideDumpUsesFourDigitAddresses()
    {
        var ram = new Sram62256 { Contents = "@7FF0: 01" };
        Assert.StartsWith("@7FF0:", ram.Contents);
        Assert.Equal(1, ram.ReadByte(0x7FF0));
    }
}
