using Cirq.Core.Digital;

namespace Cirq.Components.Boards;

/// <summary>What a header pin is for, which decides how it is stamped and what may be done to it.</summary>
public enum PinFunction
{
    /// <summary>A general-purpose pin that can drive or be read.</summary>
    Gpio,

    /// <summary>An analog input. High impedance, read as a voltage rather than a logic level.</summary>
    AnalogIn,

    /// <summary>A supply output the board provides to the circuit around it.</summary>
    Power,

    /// <summary>Board ground. Everything else on the board is referenced to it.</summary>
    Ground,

    /// <summary>Present on the header but not available to use — brought out for completeness.</summary>
    Reserved,
}

/// <summary>One pin on a board's header.</summary>
/// <param name="Number">Physical pin number as printed on the board or header.</param>
/// <param name="Name">The name people use for it — <c>GPIO17</c>, <c>D13</c>, <c>A0</c>, <c>5V</c>.</param>
/// <param name="Function">How the pin behaves.</param>
/// <param name="SupplyVoltage">For a <see cref="PinFunction.Power"/> pin, the rail it provides.</param>
/// <param name="SupportsPwm">Whether hardware PWM is available here.</param>
/// <param name="Alternate">Peripheral name shown alongside, such as <c>SDA</c> or <c>TXD</c>.</param>
public sealed record BoardPin(
    int Number,
    string Name,
    PinFunction Function,
    double SupplyVoltage = 0.0,
    bool SupportsPwm = false,
    string? Alternate = null)
{
    /// <summary>What the symbol prints against the pin.</summary>
    public string Label => Alternate is null ? Name : $"{Name}/{Alternate}";

    /// <summary>A pin the user can configure — everything except power, ground and reserved pins.</summary>
    public bool IsConfigurable => Function is PinFunction.Gpio or PinFunction.AnalogIn;
}

/// <summary>
/// A development board's electrical personality: its logic family, its header, and the limits
/// that make a circuit around it either work or release smoke.
/// </summary>
public sealed record BoardProfile(
    string Name,
    LogicLevels Levels,
    double LogicVoltage,
    double MaxPinCurrent,
    double MaxTotalGpioCurrent,
    bool IsFiveVoltTolerant,
    IReadOnlyList<BoardPin> LeftPins,
    IReadOnlyList<BoardPin> RightPins)
{
    public IReadOnlyList<BoardPin> AllPins { get; } = [.. LeftPins, .. RightPins];

    public int PinCount => AllPins.Count;

    /// <summary>Rows in the rendered symbol — the taller of the two columns.</summary>
    public int Rows => Math.Max(LeftPins.Count, RightPins.Count);

    // ---- helpers for building the boards below ---------------------------

    private static BoardPin Gpio(int number, string name, bool pwm = false, string? alternate = null) =>
        new(number, name, PinFunction.Gpio, SupportsPwm: pwm, Alternate: alternate);

    private static BoardPin Analog(int number, string name) =>
        new(number, name, PinFunction.AnalogIn);

    private static BoardPin Power(int number, string name, double volts) =>
        new(number, name, PinFunction.Power, SupplyVoltage: volts);

    private static BoardPin Gnd(int number) => new(number, "GND", PinFunction.Ground);

    private static BoardPin Reserved(int number, string name) => new(number, name, PinFunction.Reserved);

    // ---- the boards ------------------------------------------------------

    /// <summary>
    /// The 40-pin header shared by the Pi 2, 3, 4, 5 and Zero. Pins are laid out as they are on the
    /// board itself — odd numbers down one side, even down the other — so counting pins on screen
    /// matches counting them on the hardware.
    /// <para>
    /// 3.3V logic and <b>not</b> 5V tolerant: a GPIO pin tied to a 5V output is the single most
    /// common way to destroy a Pi, which is why <see cref="IsFiveVoltTolerant"/> exists.
    /// </para>
    /// </summary>
    public static readonly BoardProfile RaspberryPi40 = new(
        "Raspberry Pi (40-pin)",
        LogicLevels.Cmos33V,
        LogicVoltage: 3.3,
        MaxPinCurrent: 0.016,
        MaxTotalGpioCurrent: 0.050,
        IsFiveVoltTolerant: false,
        LeftPins:
        [
            Power(1, "3V3", 3.3),
            Gpio(3, "GPIO2", alternate: "SDA"),
            Gpio(5, "GPIO3", alternate: "SCL"),
            Gpio(7, "GPIO4"),
            Gnd(9),
            Gpio(11, "GPIO17"),
            Gpio(13, "GPIO27"),
            Gpio(15, "GPIO22"),
            Power(17, "3V3", 3.3),
            Gpio(19, "GPIO10", alternate: "MOSI"),
            Gpio(21, "GPIO9", alternate: "MISO"),
            Gpio(23, "GPIO11", alternate: "SCLK"),
            Gnd(25),
            Reserved(27, "ID_SD"),
            Gpio(29, "GPIO5"),
            Gpio(31, "GPIO6"),
            Gpio(33, "GPIO13", pwm: true),
            Gpio(35, "GPIO19", pwm: true),
            Gpio(37, "GPIO26"),
            Gnd(39),
        ],
        RightPins:
        [
            Power(2, "5V", 5.0),
            Power(4, "5V", 5.0),
            Gnd(6),
            Gpio(8, "GPIO14", alternate: "TXD"),
            Gpio(10, "GPIO15", alternate: "RXD"),
            Gpio(12, "GPIO18", pwm: true),
            Gnd(14),
            Gpio(16, "GPIO23"),
            Gpio(18, "GPIO24"),
            Gnd(20),
            Gpio(22, "GPIO25"),
            Gpio(24, "GPIO8", alternate: "CE0"),
            Gpio(26, "GPIO7", alternate: "CE1"),
            Reserved(28, "ID_SC"),
            Gnd(30),
            Gpio(32, "GPIO12", pwm: true),
            Gnd(34),
            Gpio(36, "GPIO16"),
            Gpio(38, "GPIO20"),
            Gpio(40, "GPIO21"),
        ]);

    /// <summary>
    /// Arduino Uno R3. 5V logic on an ATmega328P: the reference board most tutorials assume.
    /// Power and analog pins down one side, the digital header down the other, which is how the
    /// physical board is arranged.
    /// </summary>
    public static readonly BoardProfile ArduinoUno = new(
        "Arduino Uno R3",
        LogicLevels.Cmos5V,
        LogicVoltage: 5.0,
        MaxPinCurrent: 0.020,
        MaxTotalGpioCurrent: 0.200,
        IsFiveVoltTolerant: true,
        LeftPins:
        [
            new(1, "IOREF", PinFunction.Power, SupplyVoltage: 5.0),
            Reserved(2, "RESET"),
            Power(3, "3V3", 3.3),
            Power(4, "5V", 5.0),
            Gnd(5),
            Gnd(6),
            new(7, "VIN", PinFunction.Power, SupplyVoltage: 5.0),
            Analog(8, "A0"),
            Analog(9, "A1"),
            Analog(10, "A2"),
            Analog(11, "A3"),
            Analog(12, "A4"),
            Analog(13, "A5"),
        ],
        RightPins:
        [
            Gpio(14, "D0", alternate: "RX"),
            Gpio(15, "D1", alternate: "TX"),
            Gpio(16, "D2"),
            Gpio(17, "D3", pwm: true),
            Gpio(18, "D4"),
            Gpio(19, "D5", pwm: true),
            Gpio(20, "D6", pwm: true),
            Gpio(21, "D7"),
            Gpio(22, "D8"),
            Gpio(23, "D9", pwm: true),
            Gpio(24, "D10", pwm: true),
            Gpio(25, "D11", pwm: true),
            Gpio(26, "D12"),
            Gpio(27, "D13", alternate: "LED"),
            Gnd(28),
            Reserved(29, "AREF"),
        ]);

    /// <summary>
    /// Arduino Nano. The same ATmega328P as the Uno in a breadboard package, with two extra
    /// analog inputs (A6 and A7) that are analog-only — they have no digital buffer, so they
    /// cannot be driven or read as logic.
    /// </summary>
    public static readonly BoardProfile ArduinoNano = new(
        "Arduino Nano",
        LogicLevels.Cmos5V,
        LogicVoltage: 5.0,
        MaxPinCurrent: 0.020,
        MaxTotalGpioCurrent: 0.200,
        IsFiveVoltTolerant: true,
        LeftPins:
        [
            Power(1, "5V", 5.0),
            Power(2, "3V3", 3.3),
            new(3, "VIN", PinFunction.Power, SupplyVoltage: 5.0),
            Gnd(4),
            Gnd(5),
            Reserved(6, "RESET"),
            Reserved(7, "AREF"),
            Analog(8, "A0"),
            Analog(9, "A1"),
            Analog(10, "A2"),
            Analog(11, "A3"),
            Analog(12, "A4"),
            Analog(13, "A5"),
            Analog(14, "A6"),
            Analog(15, "A7"),
        ],
        RightPins:
        [
            Gpio(16, "D0", alternate: "RX"),
            Gpio(17, "D1", alternate: "TX"),
            Gpio(18, "D2"),
            Gpio(19, "D3", pwm: true),
            Gpio(20, "D4"),
            Gpio(21, "D5", pwm: true),
            Gpio(22, "D6", pwm: true),
            Gpio(23, "D7"),
            Gpio(24, "D8"),
            Gpio(25, "D9", pwm: true),
            Gpio(26, "D10", pwm: true),
            Gpio(27, "D11", pwm: true),
            Gpio(28, "D12"),
            Gpio(29, "D13", alternate: "LED"),
        ]);

    /// <summary>
    /// Arduino Mega 2560. 54 digital pins and 16 analog inputs, so the symbol is tall — the pins
    /// are split evenly between the two columns rather than following the physical headers, which
    /// would otherwise put 54 pins down one side.
    /// </summary>
    public static readonly BoardProfile ArduinoMega = Build();

    private static BoardProfile Build()
    {
        var number = 1;
        List<BoardPin> left =
        [
            new(number++, "IOREF", PinFunction.Power, SupplyVoltage: 5.0),
            Reserved(number++, "RESET"),
            Power(number++, "3V3", 3.3),
            Power(number++, "5V", 5.0),
            Gnd(number++),
            Gnd(number++),
            new(number++, "VIN", PinFunction.Power, SupplyVoltage: 5.0),
            Reserved(number++, "AREF"),
        ];

        // A0-A15.
        for (var a = 0; a <= 15; a++) left.Add(Analog(number++, $"A{a}"));

        // D0-D53. PWM is on 2-13 and 44-46 on this board.
        List<BoardPin> digital = [];
        for (var d = 0; d <= 53; d++)
        {
            var pwm = d is >= 2 and <= 13 or >= 44 and <= 46;
            var alternate = d switch { 0 => "RX", 1 => "TX", 13 => "LED", _ => null };
            digital.Add(Gpio(number++, $"D{d}", pwm, alternate));
        }

        // Balance the columns so the symbol is roughly square rather than a 54-row strip.
        var toLeft = Math.Max(0, (digital.Count + left.Count) / 2 - left.Count);
        left.AddRange(digital.Take(toLeft));

        return new BoardProfile(
            "Arduino Mega 2560",
            LogicLevels.Cmos5V,
            LogicVoltage: 5.0,
            MaxPinCurrent: 0.020,
            MaxTotalGpioCurrent: 0.200,
            IsFiveVoltTolerant: true,
            LeftPins: left,
            RightPins: [.. digital.Skip(toLeft)]);
    }

    public static readonly IReadOnlyList<BoardProfile> Library =
        [RaspberryPi40, ArduinoUno, ArduinoNano, ArduinoMega];
}
