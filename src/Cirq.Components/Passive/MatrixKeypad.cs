using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A 4×4 matrix keypad — the membrane one with 1 to 9, 0, *, # and A to D on it — and the eight
/// pins it has instead of seventeen.
/// <para>
/// Every key is a plain contact between one row wire and one column wire, and that is all there is
/// inside. Nothing tells you which key is down: whatever reads it has to drive one row at a time
/// and see which column follows. That is scanning, and it is why a keypad needs software where a
/// row of buttons needs none. The model is exactly the sixteen contacts, so everything that goes
/// wrong with a real one goes wrong here: press three keys on the corners of a rectangle and the
/// fourth reads as pressed too (ghosting), and two keys in one column across two driven rows short
/// a high row to a low one, which is why scanners drive with open-drain pins or diodes.
/// </para>
/// <para>
/// Double-click a key on the canvas to press it, and again to let it go — or use its toggle in the
/// CONTROLS panel. Pins are R1 to R4 down the left and C1 to C4 along the bottom, in the order a
/// real module's eight-way connector has them.
/// </para>
/// </summary>
public partial class MatrixKeypad : MechanicalContact, IPointInteractive
{
    /// <summary>The legends, row by row, as printed on the common membrane keypad.</summary>
    public static readonly string[,] Legends =
    {
        { "1", "2", "3", "A" },
        { "4", "5", "6", "B" },
        { "7", "8", "9", "C" },
        { "*", "0", "#", "D" },
    };

    /// <summary>Distance between key centres on the drawing.</summary>
    public const double KeyPitch = 28.0;

    /// <summary>Half the width of a key on the drawing.</summary>
    public const double KeyHalfSize = 12.0;

    private readonly Terminal[] _rows = new Terminal[4];
    private readonly Terminal[] _columns = new Terminal[4];

    public MatrixKeypad()
    {
        for (var i = 0; i < 4; i++)
        {
            _rows[i] = new Terminal($"r{i + 1}", $"R{i + 1}", TerminalType.Passive, new Point(-80, KeyCentre(i)));
            _columns[i] = new Terminal($"c{i + 1}", $"C{i + 1}", TerminalType.Passive, new Point(KeyCentre(i), 80));
        }

        Terminals = [.. _rows, .. _columns];
    }

    /// <summary>The four row wires, R1 at the top.</summary>
    public IReadOnlyList<Terminal> Rows => _rows;

    /// <summary>The four column wires, C1 on the left.</summary>
    public IReadOnlyList<Terminal> Columns => _columns;

    [ObservableProperty] [Operable("1")] public partial bool Key1 { get; set; }
    [ObservableProperty] [Operable("2")] public partial bool Key2 { get; set; }
    [ObservableProperty] [Operable("3")] public partial bool Key3 { get; set; }
    [ObservableProperty] [Operable("A")] public partial bool KeyA { get; set; }
    [ObservableProperty] [Operable("4")] public partial bool Key4 { get; set; }
    [ObservableProperty] [Operable("5")] public partial bool Key5 { get; set; }
    [ObservableProperty] [Operable("6")] public partial bool Key6 { get; set; }
    [ObservableProperty] [Operable("B")] public partial bool KeyB { get; set; }
    [ObservableProperty] [Operable("7")] public partial bool Key7 { get; set; }
    [ObservableProperty] [Operable("8")] public partial bool Key8 { get; set; }
    [ObservableProperty] [Operable("9")] public partial bool Key9 { get; set; }
    [ObservableProperty] [Operable("C")] public partial bool KeyC { get; set; }
    [ObservableProperty] [Operable("*")] public partial bool KeyStar { get; set; }
    [ObservableProperty] [Operable("0")] public partial bool Key0 { get; set; }
    [ObservableProperty] [Operable("#")] public partial bool KeyHash { get; set; }
    [ObservableProperty] [Operable("D")] public partial bool KeyD { get; set; }

    public override string ComponentType => "Keypad 4x4";

    public override string DesignatorPrefix => "KP";

    /// <summary>The keys held down, in reading order, or "none".</summary>
    public override string ValueLabel
    {
        get
        {
            var down = string.Concat(Enumerable.Range(0, 16)
                .Where(i => IsPressed(i / 4, i % 4))
                .Select(i => Legends[i / 4, i % 4]));

            return down.Length == 0 ? "none" : down;
        }
    }

    /// <summary>Where a key's row or column sits on the drawing.</summary>
    public static double KeyCentre(int index) => (index - 1.5) * KeyPitch;

    /// <summary>Whether the key at a row and column (from 0, top left) is held down.</summary>
    public bool IsPressed(int row, int column) => (row * 4 + column) switch
    {
        0 => Key1, 1 => Key2, 2 => Key3, 3 => KeyA,
        4 => Key4, 5 => Key5, 6 => Key6, 7 => KeyB,
        8 => Key7, 9 => Key8, 10 => Key9, 11 => KeyC,
        12 => KeyStar, 13 => Key0, 14 => KeyHash, _ => KeyD,
    };

    /// <summary>Presses or releases the key at a row and column (from 0, top left).</summary>
    public void SetPressed(int row, int column, bool pressed)
    {
        switch (row * 4 + column)
        {
            case 0: Key1 = pressed; break;
            case 1: Key2 = pressed; break;
            case 2: Key3 = pressed; break;
            case 3: KeyA = pressed; break;
            case 4: Key4 = pressed; break;
            case 5: Key5 = pressed; break;
            case 6: Key6 = pressed; break;
            case 7: KeyB = pressed; break;
            case 8: Key7 = pressed; break;
            case 9: Key8 = pressed; break;
            case 10: Key9 = pressed; break;
            case 11: KeyC = pressed; break;
            case 12: KeyStar = pressed; break;
            case 13: Key0 = pressed; break;
            case 14: KeyHash = pressed; break;
            default: KeyD = pressed; break;
        }
    }

    /// <summary>Presses or releases a key by its legend — "5", "*", "D".</summary>
    public void SetPressed(string legend, bool pressed)
    {
        var (row, column) = Find(legend);
        SetPressed(row, column, pressed);
    }

    /// <summary>The row and column of a legend.</summary>
    public static (int Row, int Column) Find(string legend)
    {
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                if (string.Equals(Legends[r, c], legend, StringComparison.OrdinalIgnoreCase)) return (r, c);

        throw new ArgumentException($"A 4x4 keypad has no key '{legend}'.", nameof(legend));
    }

    /// <summary>The key under a point on the drawing, or null between keys.</summary>
    public static (int Row, int Column)? KeyAt(Point local)
    {
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                if (Math.Abs(local.X - KeyCentre(c)) <= KeyHalfSize && Math.Abs(local.Y - KeyCentre(r)) <= KeyHalfSize)
                    return (r, c);
            }
        }

        return null;
    }

    public string InteractionHint => "Double-click a key to press it";

    /// <summary>Lets go of every key — what the panel's one-click operation means with no key to aim at.</summary>
    public void Interact()
    {
        for (var i = 0; i < 16; i++) SetPressed(i / 4, i % 4, false);
    }

    public bool InteractAt(Point local)
    {
        if (KeyAt(local) is not var (row, column)) return false;

        SetPressed(row, column, !IsPressed(row, column));
        return true;
    }

    public string? InteractionHintAt(Point local) => KeyAt(local) is var (row, column)
        ? $"{(IsPressed(row, column) ? "Release" : "Press")} {Legends[row, column]}"
        : null;

    /// <summary>Any key moving changes what the caption says is held down.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName?.StartsWith("Key", StringComparison.Ordinal) == true) NotifyValueChanged();
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                StampContact(system, _rows[r], _columns[c], IsPressed(r, c));
    }

    public override double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var total = 0.0;

        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                var current = ContactCurrent(system, _rows[r], _columns[c], IsPressed(r, c));

                if (ReferenceEquals(terminal, _rows[r])) total += current;
                else if (ReferenceEquals(terminal, _columns[c])) total -= current;
            }
        }

        return total;
    }
}
