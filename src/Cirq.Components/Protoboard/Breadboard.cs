using System.Globalization;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>
/// A strip of holes that are joined inside the board: five holes of one column on one side of the centre channel,
/// or a whole power rail. Every hole is in exactly one.
/// </summary>
/// <param name="Board">Which board, from 1.</param>
/// <param name="Row">For a terminal strip the first row of its half (<see cref="Breadboard.A"/> or <see cref="Breadboard.F"/>); for a rail, the rail's row.</param>
/// <param name="Column">The column, for a terminal strip; 0 for a rail, which runs the whole length.</param>
public readonly record struct BoardStrip(int Board, int Row, int Column)
{
    public bool IsRail => Breadboard.IsRail(Row);

    /// <summary>"column 12, top" or "the top + rail", for a sentence.</summary>
    public string Describe(int boards) =>
        (boards > 1 ? $"board {Board}, " : "") + (IsRail
            ? $"the {Breadboard.RailName(Row)} rail"
            : $"column {Column} {(Row == Breadboard.A ? "a–e" : "f–j")}");
}

/// <summary>
/// The geometry of a full-size (830-hole) solderless breadboard: 63 columns of two five-hole strips either side of
/// a channel 0.3 inch wide, and two power rails along each edge.
/// <para>
/// Everything is measured in pitches, the 0.1 inch between neighbouring holes. A row index runs from the top
/// edge: the top + and − rails, rows a to e, the channel, rows f to j, then the bottom − and + rails. The rails
/// run the whole length of the board, as they do on most full-size boards (some are split in the middle; this one
/// is not).
/// </para>
/// </summary>
public static class Breadboard
{
    public const int Columns = 63;

    public const int TopPlus = 0;
    public const int TopMinus = 1;
    public const int A = 2;
    public const int E = 6;
    public const int F = 7;
    public const int J = 11;
    public const int BottomMinus = 12;
    public const int BottomPlus = 13;

    public const int Rows = 14;

    /// <summary>How tall one board is, in pitches, and the gap left between two of them.</summary>
    public const double Height = 19.0;

    public const double Width = 68.0;

    public const double Gap = 2.5;

    /// <summary>Where each row's holes are, down the board, in pitches. e to f is three: the channel a narrow DIP straddles.</summary>
    private static readonly double[] RowY = [1.4, 2.4, 4.4, 5.4, 6.4, 7.4, 8.4, 11.4, 12.4, 13.4, 14.4, 15.4, 16.6, 17.6];

    /// <summary>The columns a rail has holes at: fifty, in ten groups of five, centred on the board.</summary>
    public static IReadOnlyList<int> RailColumns { get; } =
        [.. Enumerable.Range(0, 50).Select(i => 3 + (i / 5 * 6) + (i % 5))];

    private static readonly HashSet<int> RailSet = [.. RailColumns];

    public static bool IsRail(int row) => row is TopPlus or TopMinus or BottomMinus or BottomPlus;

    public static bool IsTerminalRow(int row) => row is >= A and <= J;

    public static bool IsTopHalf(int row) => row is >= A and <= E;

    /// <summary>Whether a rail is a + (red) one.</summary>
    public static bool IsPlusRail(int row) => row is TopPlus or BottomPlus;

    public static string RailName(int row) => row switch
    {
        TopPlus => "top +",
        TopMinus => "top −",
        BottomMinus => "bottom −",
        BottomPlus => "bottom +",
        _ => "",
    };

    /// <summary>Whether a hole exists: a terminal row at any column, a rail only at its fifty.</summary>
    public static bool Exists(BoardHole hole) =>
        hole.Board >= 1 && hole.Row is >= 0 and < Rows && hole.Column is >= 1 and <= Columns
        && (!IsRail(hole.Row) || RailSet.Contains(hole.Column));

    /// <summary>The strip a hole belongs to: what it is joined to inside the board.</summary>
    public static BoardStrip StripOf(BoardHole hole) =>
        IsRail(hole.Row) ? new BoardStrip(hole.Board, hole.Row, 0)
        : new BoardStrip(hole.Board, IsTopHalf(hole.Row) ? A : F, hole.Column);

    /// <summary>Every hole in a strip, from the edge of the board inwards.</summary>
    public static IEnumerable<BoardHole> HolesOf(BoardStrip strip)
    {
        if (strip.IsRail)
        {
            foreach (var column in RailColumns) yield return new BoardHole(strip.Board, strip.Row, column);
            yield break;
        }

        for (var row = strip.Row; row < strip.Row + 5; row++) yield return new BoardHole(strip.Board, row, strip.Column);
    }

    /// <summary>Where a hole's centre is, in pitches, boards stacked one above the next.</summary>
    public static (double X, double Y) Position(BoardHole hole) =>
        (2.5 + hole.Column, Top(hole.Board) + RowY[hole.Row]);

    /// <summary>The top edge of a board, in pitches.</summary>
    public static double Top(int board) => (board - 1) * (Height + Gap);

    public static double RowPosition(int row) => RowY[row];

    /// <summary>The hole nearest a point (in pitches) on one of <paramref name="boards"/> boards, when one is within <paramref name="within"/>.</summary>
    public static BoardHole? Nearest(double x, double y, int boards, double within = 0.75)
    {
        var board = (int)Math.Floor(y / (Height + Gap)) + 1;
        if (board < 1 || board > boards) return null;
        var local = y - Top(board);
        var row = Enumerable.Range(0, Rows).MinBy(r => Math.Abs(RowY[r] - local));
        var column = (int)Math.Round(x - 2.5);
        BoardHole? best = null;
        var nearest = double.MaxValue;
        for (var c = column - 1; c <= column + 1; c++)
        {
            var hole = new BoardHole(board, row, c);
            if (!Exists(hole)) continue;
            var (hx, hy) = Position(hole);
            var d = Math.Sqrt(((hx - x) * (hx - x)) + ((hy - y) * (hy - y)));
            if (d < nearest)
            {
                nearest = d;
                best = hole;
            }
        }

        return nearest <= within ? best : null;
    }

    /// <summary>The distance between two holes, in pitches.</summary>
    public static double Distance(BoardHole a, BoardHole b)
    {
        var (ax, ay) = Position(a);
        var (bx, by) = Position(b);
        return Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
    }

    private static readonly string[] RailCodes = ["T+", "T-", "", "", "", "", "", "", "", "", "", "", "B-", "B+"];

    /// <summary>
    /// A hole's name as the board prints it: <c>e12</c>, a rail as <c>T+5</c> (top +) or <c>B-30</c> (bottom −), with
    /// <c>2:</c> in front on the second board of a set.
    /// </summary>
    public static string Name(BoardHole hole)
    {
        var prefix = hole.Board > 1 ? $"{hole.Board}:" : "";
        var row = IsRail(hole.Row) ? RailCodes[hole.Row] : ((char)('a' + hole.Row - A)).ToString();
        return $"{prefix}{row}{hole.Column.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Reads a hole's name back (see <see cref="Name"/>); null when it isn't one.</summary>
    public static BoardHole? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-');
        var board = 1;
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (!int.TryParse(s[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out board)) return null;
            s = s[(colon + 1)..];
        }

        int row;
        string digits;
        if (s.Length >= 3 && (s[0] is 'T' or 't' or 'B' or 'b') && s[1] is '+' or '-')
        {
            var top = s[0] is 'T' or 't';
            row = s[1] == '+' ? (top ? TopPlus : BottomPlus) : (top ? TopMinus : BottomMinus);
            digits = s[2..];
        }
        else if (s.Length >= 2 && char.ToLowerInvariant(s[0]) is >= 'a' and <= 'j')
        {
            row = A + (char.ToLowerInvariant(s[0]) - 'a');
            digits = s[1..];
        }
        else return null;

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var column)) return null;
        var hole = new BoardHole(board, row, column);
        return Exists(hole) ? hole : null;
    }
}
