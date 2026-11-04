using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The 7485 comparator, the 74181 ALU and 74182 look-ahead, the 74280 parity tree and the 4008 adder:
/// every function against the datasheet's own list, carries, cascading, and sixteen bits added in
/// one step through a 74182.
/// </summary>
public class ArithmeticTests
{
    [Theory]
    [InlineData(typeof(Ic7485), "p1=B3 p2=A<Bi p3=A=Bi p4=A>Bi p5=A>B p6=A=B p7=A<B p8=GND p9=B0 p10=A0 p11=B1 p12=A1 p13=A2 p14=B2 p15=A3 p16=VCC")]
    [InlineData(typeof(Ic74181), "p1=B0 p2=A0 p3=S3 p4=S2 p5=S1 p6=S0 p7=Cn p8=M p9=F0 p10=F1 p11=F2 p12=GND p13=F3 p14=A=B p15=P p16=Cn+4 p17=G p18=B3 p19=A3 p20=B2 p21=A2 p22=B1 p23=A1 p24=VCC")]
    [InlineData(typeof(Ic74182), "p1=G1 p2=P1 p3=G0 p4=P0 p5=G3 p6=P3 p7=P p8=GND p9=Cn+z p10=G p11=Cn+y p12=Cn+x p13=Cn p14=G2 p15=P2 p16=VCC")]
    [InlineData(typeof(Ic74280), "p1=G p2=H p3=NC p4=I p5=EVEN p6=ODD p7=GND p8=A p9=B p10=C p11=D p12=E p13=F p14=VCC")]
    [InlineData(typeof(Ic4008), "p1=A4 p2=B3 p3=A3 p4=B2 p5=A2 p6=B1 p7=A1 p8=VSS p9=C0 p10=S1 p11=S2 p12=S3 p13=S4 p14=C4 p15=B4 p16=VDD")]
    public void EachIsOnItsDatasheetPinout(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=', 2)[0], pair.Split('=', 2)[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }

        Assert.Equal(ic.PinCount, ic.Terminals.Count);
        Assert.Equal(ic.PinCount, ic.Terminals.Select(t => t.Id).Distinct().Count());
    }

    // ---- 7485 -------------------------------------------------------------

    /// <summary>All 256 pairs with the cascade set as the first of a chain.</summary>
    [Fact]
    public void The7485ComparesEveryPair()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7485());
        var a = bench.Toggles(ic.A);
        var b = bench.Toggles(ic.B);
        bench.Tie(ic.GreaterIn, false);
        bench.Tie(ic.EqualIn, true);
        bench.Tie(ic.LessIn, false);

        for (var x = 0; x < 16; x++)
        {
            for (var y = 0; y < 16; y++)
            {
                LogicBench.Set(a, x);
                LogicBench.Set(b, y);
                bench.Run(200e-9);

                Assert.Equal(x > y, bench.High(ic.Greater));
                Assert.Equal(x == y, bench.High(ic.Equal));
                Assert.Equal(x < y, bench.High(ic.Less));
            }
        }
    }

    /// <summary>With A and B equal, the cascade inputs decide — the datasheet's five rows.</summary>
    [Theory]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(false, true, false, false, true, false)]
    [InlineData(true, true, true, false, true, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, true)]
    public void WithEqualWordsTheCascadeDecides(bool gt, bool eq, bool lt, bool outGt, bool outEq, bool outLt)
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7485());
        bench.Toggles(ic.A, 9);
        bench.Toggles(ic.B, 9);
        bench.Toggle(ic.GreaterIn, gt);
        bench.Toggle(ic.EqualIn, eq);
        bench.Toggle(ic.LessIn, lt);

        bench.Run();
        Assert.Equal(outGt, bench.High(ic.Greater));
        Assert.Equal(outEq, bench.High(ic.Equal));
        Assert.Equal(outLt, bench.High(ic.Less));
    }

    /// <summary>Two cascaded compare eight bits: the high nibble decides unless it is equal.</summary>
    [Theory]
    [InlineData(0x37, 0x37)]
    [InlineData(0x38, 0x37)]
    [InlineData(0x27, 0x37)]
    [InlineData(0xA0, 0x0F)]
    public void Two7485sCompareAByte(int x, int y)
    {
        var bench = new LogicBench();
        var low = bench.Add(new Ic7485());
        var high = bench.Add(new Ic7485());
        bench.Toggles(low.A, x & 0xF);
        bench.Toggles(low.B, y & 0xF);
        bench.Toggles(high.A, x >> 4);
        bench.Toggles(high.B, y >> 4);
        bench.Tie(low.GreaterIn, false);
        bench.Tie(low.EqualIn, true);
        bench.Tie(low.LessIn, false);
        bench.Circuit.Connect(low.Greater, high.GreaterIn);
        bench.Circuit.Connect(low.Equal, high.EqualIn);
        bench.Circuit.Connect(low.Less, high.LessIn);

        bench.Run();
        Assert.Equal(x > y, bench.High(high.Greater));
        Assert.Equal(x == y, bench.High(high.Equal));
        Assert.Equal(x < y, bench.High(high.Less));
    }

    // ---- 74181 ------------------------------------------------------------

    /// <summary>The datasheet's logic functions, active-high data, M high, by S.</summary>
    private static readonly Func<int, int, int>[] LogicFunctions =
    [
        (a, b) => ~a,           // 0: A'
        (a, b) => ~(a | b),     // 1: (A + B)'
        (a, b) => ~a & b,       // 2: A'B
        (a, b) => 0,            // 3: 0
        (a, b) => ~(a & b),     // 4: (AB)'
        (a, b) => ~b,           // 5: B'
        (a, b) => a ^ b,        // 6: A xor B
        (a, b) => a & ~b,       // 7: AB'
        (a, b) => ~a | b,       // 8: A' + B
        (a, b) => ~(a ^ b),     // 9: A xnor B
        (a, b) => b,            // 10: B
        (a, b) => a & b,        // 11: AB
        (a, b) => 0xF,          // 12: 1
        (a, b) => a | ~b,       // 13: A + B'
        (a, b) => a | b,        // 14: A + B
        (a, b) => a,            // 15: A
    ];

    /// <summary>
    /// The datasheet's arithmetic functions, M low, as the two operands each one adds: "A plus AB'" is
    /// (A, AB'), "A minus B minus 1" is A plus B', "minus 1" is fifteen. The carry in (Cn low) adds one.
    /// </summary>
    private static readonly Func<int, int, (int, int)>[] ArithmeticFunctions =
    [
        (a, b) => (a, 0),                   // 0: A
        (a, b) => (a | b, 0),               // 1: A + B
        (a, b) => (a | (~b & 0xF), 0),      // 2: A + B'
        (a, b) => (0xF, 0),                 // 3: minus 1
        (a, b) => (a, a & ~b & 0xF),        // 4: A plus AB'
        (a, b) => (a | b, a & ~b & 0xF),    // 5: (A + B) plus AB'
        (a, b) => (a, ~b & 0xF),            // 6: A minus B minus 1
        (a, b) => (a & ~b & 0xF, 0xF),      // 7: AB' minus 1
        (a, b) => (a, a & b),               // 8: A plus AB
        (a, b) => (a, b),                   // 9: A plus B
        (a, b) => (a | (~b & 0xF), a & b),  // 10: (A + B') plus AB
        (a, b) => (a & b, 0xF),             // 11: AB minus 1
        (a, b) => (a, a),                   // 12: A plus A
        (a, b) => (a | b, a),               // 13: (A + B) plus A
        (a, b) => (a | (~b & 0xF), a),      // 14: (A + B') plus A
        (a, b) => (a, 0xF),                 // 15: A minus 1
    ];

    /// <summary>All 16 logic and 16 arithmetic functions, both carries, every A and B: 16,384 rows.</summary>
    [Fact]
    public void Every74181FunctionIsTheDatasheets()
    {
        for (var s = 0; s < 16; s++)
        {
            for (var a = 0; a < 16; a++)
            {
                for (var b = 0; b < 16; b++)
                {
                    Assert.Equal(LogicFunctions[s](a, b) & 0xF, Ic74181.Evaluate(a, b, s, logic: true, carryInPin: true).F);
                    Assert.Equal(LogicFunctions[s](a, b) & 0xF, Ic74181.Evaluate(a, b, s, logic: true, carryInPin: false).F);

                    foreach (var carry in new[] { 0, 1 })
                    {
                        var (p, q) = ArithmeticFunctions[s](a, b);
                        var sum = p + q + carry;
                        var (f, carryOutPin, _, _) = Ic74181.Evaluate(a, b, s, logic: false, carryInPin: carry == 0);

                        Assert.True((sum & 0xF) == f, $"S={s} A={a} B={b} Cn={1 - carry}: F={f}, want {sum & 0xF}");
                        Assert.Equal(sum < 16, carryOutPin);
                    }
                }
            }
        }
    }

    /// <summary>Through the analog solve: A plus B with and without the carry, and the pins that say so.</summary>
    [Theory]
    [InlineData(9, 4, true, 13, true)]
    [InlineData(9, 4, false, 14, true)]
    [InlineData(12, 7, true, 3, false)]
    [InlineData(15, 0, false, 0, false)]
    public void The74181AddsOnItsPins(int a, int b, bool carryInHigh, int f, bool carryOutHigh)
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74181());
        bench.Toggles(ic.A, a);
        bench.Toggles(ic.B, b);
        bench.Toggles(ic.Select, 0b1001);
        bench.Toggle(ic.CarryIn, carryInHigh);
        bench.Tie(ic.Mode, false);

        bench.Run();
        Assert.Equal(f, bench.Byte(ic.F));
        Assert.Equal(carryOutHigh, bench.High(ic.CarryOut));
        Assert.Equal(f, ic.Result);
    }

    /// <summary>Subtracting with no carry in, F is 1111 exactly when A equals B, and the open-collector A=B pin lets go.</summary>
    [Fact]
    public void The74181EqualityPinIsOpenCollector()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74181());
        var a = bench.Toggles(ic.A, 6);
        bench.Toggles(ic.B, 6);
        bench.Toggles(ic.Select, 0b0110);
        bench.Tie(ic.CarryIn, true);
        bench.Tie(ic.Mode, false);

        var pullUp = bench.Circuit.Add(new Resistor(4.7e3));
        bench.Circuit.Connect(pullUp.A, bench.Rail.Positive);
        bench.Circuit.Connect(pullUp.B, ic.Equal);

        bench.Run();
        Assert.Equal(0xF, bench.Byte(ic.F));
        Assert.True(bench.High(ic.Equal));
        Assert.Equal(TerminalType.Bidirectional, ic.Equal.Type);

        LogicBench.Set(a, 7);
        bench.Run();
        Assert.False(bench.High(ic.Equal));
    }

    /// <summary>Two 74181s with Cn+4 into the next Cn add eight bits — the carry ripples, active low the whole way.</summary>
    [Theory]
    [InlineData(0x5A, 0x3C)]
    [InlineData(0xFF, 0x01)]
    [InlineData(0x80, 0x80)]
    public void Two74181sRippleEightBits(int x, int y)
    {
        var bench = new LogicBench();
        var low = bench.Add(new Ic74181());
        var high = bench.Add(new Ic74181());

        foreach (var (alu, shift) in new[] { (low, 0), (high, 4) })
        {
            bench.Toggles(alu.A, (x >> shift) & 0xF);
            bench.Toggles(alu.B, (y >> shift) & 0xF);
            bench.Toggles(alu.Select, 0b1001);
            bench.Tie(alu.Mode, false);
        }

        bench.Tie(low.CarryIn, true);
        bench.Circuit.Connect(low.CarryOut, high.CarryIn);

        bench.Run();
        var sum = x + y;
        Assert.Equal(sum & 0xFF, bench.Byte(low.F) | (bench.Byte(high.F) << 4));
        Assert.Equal(sum < 256, bench.High(high.CarryOut));
    }

    /// <summary>
    /// Four 74181s and a 74182, wired straight together as the datasheet draws it — G and P of each ALU
    /// into the 182, its three carries into the second, third and fourth ALUs — add sixteen bits in the
    /// active-high data convention with no inverters anywhere.
    /// </summary>
    [Theory]
    [InlineData(0x1234, 0x4321, false)]
    [InlineData(0x0FFF, 0x0001, false)]
    [InlineData(0x00FF, 0x0F01, true)]
    [InlineData(0xFFFF, 0x0001, false)]
    [InlineData(0x8000, 0x8000, true)]
    [InlineData(0x7FFF, 0x7FFF, true)]
    public void FourAlusAndA74182AddSixteenBits(int x, int y, bool carryIn)
    {
        var bench = new LogicBench();
        var alus = Enumerable.Range(0, 4).Select(_ => bench.Add(new Ic74181())).ToArray();
        var lookAhead = bench.Add(new Ic74182());

        for (var i = 0; i < 4; i++)
        {
            bench.Toggles(alus[i].A, (x >> (4 * i)) & 0xF);
            bench.Toggles(alus[i].B, (y >> (4 * i)) & 0xF);
            bench.Toggles(alus[i].Select, 0b1001);
            bench.Tie(alus[i].Mode, false);
            bench.Circuit.Connect(alus[i].Generate, lookAhead.GenerateIn[i]);
            bench.Circuit.Connect(alus[i].Propagate, lookAhead.PropagateIn[i]);
        }

        // Active low in this convention: low is a carry in.
        bench.Toggle(alus[0].CarryIn, !carryIn);
        bench.Circuit.Connect(alus[0].CarryIn, lookAhead.CarryIn);
        bench.Circuit.Connect(lookAhead.CarryX, alus[1].CarryIn);
        bench.Circuit.Connect(lookAhead.CarryY, alus[2].CarryIn);
        bench.Circuit.Connect(lookAhead.CarryZ, alus[3].CarryIn);

        bench.Run();

        var sum = x + y + (carryIn ? 1 : 0);
        var f = Enumerable.Range(0, 4).Sum(i => bench.Byte(alus[i].F) << (4 * i));
        Assert.Equal(sum & 0xFFFF, f);
        Assert.Equal(sum < 0x10000, bench.High(alus[3].CarryOut));
    }

    /// <summary>The 74182's equations, every combination of its nine inputs.</summary>
    [Fact]
    public void The74182IsItsEquations()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74182());
        var g = bench.Toggles(ic.GenerateIn);
        var p = bench.Toggles(ic.PropagateIn);
        var c = bench.Toggle(ic.CarryIn);

        for (var row = 0; row < 512; row += 7)
        {
            LogicBench.Set(g, row & 0xF);
            LogicBench.Set(p, (row >> 4) & 0xF);
            c.State = (row & 0x100) != 0;
            bench.Run(200e-9);

            bool G(int i) => (row & (1 << i)) == 0;          // active low
            bool P(int i) => (row & (1 << (4 + i))) == 0;
            var cn = c.State;

            var x = G(0) || (P(0) && cn);
            var y = G(1) || (P(1) && G(0)) || (P(1) && P(0) && cn);
            var z = G(2) || (P(2) && G(1)) || (P(2) && P(1) && G(0)) || (P(2) && P(1) && P(0) && cn);
            var gOut = G(3) || (P(3) && G(2)) || (P(3) && P(2) && G(1)) || (P(3) && P(2) && P(1) && G(0));

            Assert.Equal(x, bench.High(ic.CarryX));
            Assert.Equal(y, bench.High(ic.CarryY));
            Assert.Equal(z, bench.High(ic.CarryZ));
            Assert.Equal(!gOut, bench.High(ic.GenerateOut));
            Assert.Equal(!(P(0) && P(1) && P(2) && P(3)), bench.High(ic.PropagateOut));
        }
    }

    // ---- 74280 and 4008 ---------------------------------------------------

    [Fact]
    public void The74280CountsOnesOverNineBits()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74280());
        var inputs = bench.Toggles(ic.Inputs);

        for (var word = 0; word < 512; word += 3)
        {
            LogicBench.Set(inputs, word);
            bench.Run(200e-9);

            var even = int.PopCount(word) % 2 == 0;
            Assert.Equal(even, bench.High(ic.Even));
            Assert.Equal(!even, bench.High(ic.Odd));
        }
    }

    [Fact]
    public void The4008AddsTwoNibblesAndACarry()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4008());
        var a = bench.Toggles(ic.A);
        var b = bench.Toggles(ic.B);
        var c = bench.Toggle(ic.CarryIn);

        for (var row = 0; row < 512; row += 5)
        {
            LogicBench.Set(a, row & 0xF);
            LogicBench.Set(b, (row >> 4) & 0xF);
            c.State = (row & 0x100) != 0;
            bench.Run(1e-6);

            var sum = (row & 0xF) + ((row >> 4) & 0xF) + (c.State ? 1 : 0);
            Assert.Equal(sum & 0xF, bench.Byte(ic.Sum));
            Assert.Equal(sum > 15, bench.High(ic.CarryOut));
        }
    }
}
