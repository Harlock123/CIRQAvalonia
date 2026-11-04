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
/// <param name="HasAdc">
/// A general-purpose pin that is also an ADC input — a Pico's GP26, an ESP32's GPIO32 — so reading
/// it gives a voltage rather than a logic level.
/// </param>
public sealed record BoardPin(
    int Number,
    string Name,
    PinFunction Function,
    double SupplyVoltage = 0.0,
    bool SupportsPwm = false,
    string? Alternate = null,
    bool HasAdc = false)
{
    /// <summary>What the symbol prints against the pin.</summary>
    public string Label => Alternate is null ? Name : $"{Name}/{Alternate}";

    /// <summary>A pin the user can configure — everything except power, ground and reserved pins.</summary>
    public bool IsConfigurable => Function is PinFunction.Gpio or PinFunction.AnalogIn;

    /// <summary>Whether reading the pin reports a voltage: an analog input, or a GPIO with an ADC behind it.</summary>
    public bool ReadsVolts => Function is PinFunction.AnalogIn || HasAdc;
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

    private static BoardPin AdcGpio(int number, string name, string alternate) =>
        new(number, name, PinFunction.Gpio, SupportsPwm: true, Alternate: alternate, HasAdc: true);

    private static BoardPin Analog(int number, string name, string? alternate = null) =>
        new(number, name, PinFunction.AnalogIn, Alternate: alternate);

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

    /// <summary>
    /// Raspberry Pi Pico (RP2040; the Pico W and Pico 2 share the header). A microcontroller board
    /// rather than a computer, but the same 3.3 V and the same rule: <b>not</b> 5 V tolerant.
    /// <para>
    /// Laid out as the board is, pin 1 at the top left by the USB socket counting down to 20, and 21
    /// at the bottom right counting back up to VBUS at 40 — so the right column reads 40 to 21 top
    /// to bottom. Every GPIO can do PWM, and GP26 to GP28 are also the three ADC inputs. The pins can
    /// be set to drive up to 12 mA; 50 mA is the budget for all of them together.
    /// </para>
    /// </summary>
    public static readonly BoardProfile RaspberryPiPico = new(
        "Raspberry Pi Pico",
        LogicLevels.Cmos33V,
        LogicVoltage: 3.3,
        MaxPinCurrent: 0.012,
        MaxTotalGpioCurrent: 0.050,
        IsFiveVoltTolerant: false,
        LeftPins:
        [
            Gpio(1, "GP0", pwm: true, alternate: "TX"),
            Gpio(2, "GP1", pwm: true, alternate: "RX"),
            Gnd(3),
            Gpio(4, "GP2", pwm: true),
            Gpio(5, "GP3", pwm: true),
            Gpio(6, "GP4", pwm: true, alternate: "SDA"),
            Gpio(7, "GP5", pwm: true, alternate: "SCL"),
            Gnd(8),
            Gpio(9, "GP6", pwm: true),
            Gpio(10, "GP7", pwm: true),
            Gpio(11, "GP8", pwm: true),
            Gpio(12, "GP9", pwm: true),
            Gnd(13),
            Gpio(14, "GP10", pwm: true),
            Gpio(15, "GP11", pwm: true),
            Gpio(16, "GP12", pwm: true),
            Gpio(17, "GP13", pwm: true),
            Gnd(18),
            Gpio(19, "GP14", pwm: true),
            Gpio(20, "GP15", pwm: true),
        ],
        RightPins:
        [
            Power(40, "VBUS", 5.0),
            new(39, "VSYS", PinFunction.Power, SupplyVoltage: 4.7),
            Gnd(38),
            Reserved(37, "3V3_EN"),
            Power(36, "3V3", 3.3),
            Reserved(35, "ADC_VREF"),
            AdcGpio(34, "GP28", "ADC2"),
            new(33, "AGND", PinFunction.Ground),
            AdcGpio(32, "GP27", "ADC1"),
            AdcGpio(31, "GP26", "ADC0"),
            Reserved(30, "RUN"),
            Gpio(29, "GP22", pwm: true),
            Gnd(28),
            Gpio(27, "GP21", pwm: true),
            Gpio(26, "GP20", pwm: true),
            Gpio(25, "GP19", pwm: true, alternate: "MOSI"),
            Gpio(24, "GP18", pwm: true, alternate: "SCK"),
            Gnd(23),
            Gpio(22, "GP17", pwm: true, alternate: "CS"),
            Gpio(21, "GP16", pwm: true, alternate: "MISO"),
        ]);

    /// <summary>
    /// ESP32 DevKitC (ESP32-WROOM-32, 38 pins). 3.3 V and not 5 V tolerant, like the Pi.
    /// <para>
    /// Two headers of nineteen, numbered here 1 to 19 down the left (J2) and 20 to 38 down the right
    /// (J3), antenna end at the top. Three things about its pins catch people, and all three are
    /// here. GPIO34 to GPIO39 are <b>input only</b> — no output driver and no pull-ups — so they are
    /// analog inputs that can only be read. GPIO6 to GPIO11 are wired to the module's flash chip and
    /// cannot be used at all. And GPIO0, 2, 12 and 15 are strapping pins, read at reset, which work
    /// as ordinary GPIO afterwards but can stop the board booting if a circuit holds them the wrong
    /// way. Any output pin can do PWM; GPIO32 and GPIO33 are ADC1 inputs as well as GPIO (the ADC2
    /// pins are not marked, since ADC2 cannot be used while the radio is on).
    /// </para>
    /// <para>
    /// The per-pin figure is the 40 mA the datasheet gives at the strongest drive setting and the
    /// total is an allowance rather than a datasheet number: the module's own limit is set by its
    /// 3.3 V regulator more than by the pins.
    /// </para>
    /// </summary>
    public static readonly BoardProfile Esp32DevKit = new(
        "ESP32 DevKit",
        LogicLevels.Cmos33V,
        LogicVoltage: 3.3,
        MaxPinCurrent: 0.040,
        MaxTotalGpioCurrent: 0.200,
        IsFiveVoltTolerant: false,
        LeftPins:
        [
            Power(1, "3V3", 3.3),
            Reserved(2, "EN"),
            Analog(3, "GPIO36", "VP"),
            Analog(4, "GPIO39", "VN"),
            Analog(5, "GPIO34"),
            Analog(6, "GPIO35"),
            AdcGpio(7, "GPIO32", "ADC4"),
            AdcGpio(8, "GPIO33", "ADC5"),
            Gpio(9, "GPIO25", pwm: true, alternate: "DAC1"),
            Gpio(10, "GPIO26", pwm: true, alternate: "DAC2"),
            Gpio(11, "GPIO27", pwm: true),
            Gpio(12, "GPIO14", pwm: true),
            Gpio(13, "GPIO12", pwm: true),
            Gnd(14),
            Gpio(15, "GPIO13", pwm: true),
            Reserved(16, "SD2"),
            Reserved(17, "SD3"),
            Reserved(18, "CMD"),
            Power(19, "5V", 5.0),
        ],
        RightPins:
        [
            Gnd(20),
            Gpio(21, "GPIO23", pwm: true, alternate: "MOSI"),
            Gpio(22, "GPIO22", pwm: true, alternate: "SCL"),
            Gpio(23, "GPIO1", pwm: true, alternate: "TX0"),
            Gpio(24, "GPIO3", pwm: true, alternate: "RX0"),
            Gpio(25, "GPIO21", pwm: true, alternate: "SDA"),
            Gnd(26),
            Gpio(27, "GPIO19", pwm: true, alternate: "MISO"),
            Gpio(28, "GPIO18", pwm: true, alternate: "SCK"),
            Gpio(29, "GPIO5", pwm: true, alternate: "SS"),
            Gpio(30, "GPIO17", pwm: true),
            Gpio(31, "GPIO16", pwm: true),
            Gpio(32, "GPIO4", pwm: true),
            Gpio(33, "GPIO0", pwm: true),
            Gpio(34, "GPIO2", pwm: true),
            Gpio(35, "GPIO15", pwm: true),
            Reserved(36, "SD1"),
            Reserved(37, "SD0"),
            Reserved(38, "CLK"),
        ]);

    public static readonly IReadOnlyList<BoardProfile> Library =
        [RaspberryPi40, ArduinoUno, ArduinoNano, ArduinoMega, RaspberryPiPico, Esp32DevKit];
}
