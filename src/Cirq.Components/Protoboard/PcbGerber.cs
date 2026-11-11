using System.Globalization;
using System.IO.Compression;
using System.Text;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>
/// A printed circuit board's manufacturing files, as a board house takes them: Gerber RS-274X (with X2 attributes) for
/// the copper on the back, its solder mask, the component side's silkscreen and the board's outline, and an Excellon
/// drill file for the holes. Millimetres throughout, the origin at the board's bottom left corner, seen from the
/// component side, as Gerber viewers show a board.
/// <para>
/// The copper is the layout's: a round pad at every pin (pin 1 of a chip or a header square), the traces at their width
/// (<see cref="PcbRouter.SignalWidth"/>, wider for supplies and ground). The silkscreen has each part's outline and its
/// designator, and a line for each wire jumper to be fitted on the component side.
/// </para>
/// </summary>
public static class PcbGerber
{
    private const double SilkWidth = 0.15, EdgeWidth = 0.1, MaskExpansion = 0.1, TextHeight = 1.2;

    /// <summary>The files, by name, for a board <paramref name="name"/>: name-B_Cu.gbl, name-B_Mask.gbs, name-F_SilkS.gto, name-Edge_Cuts.gm1 and name.drl.</summary>
    public static IReadOnlyDictionary<string, string> Files(ProtoboardLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Kind != BoardKind.Pcb) throw new ArgumentException("Gerber files are made from a printed circuit board's layout.", nameof(layout));
        var stem = Safe(name);
        var board = new Board(layout);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{stem}-B_Cu.gbl"] = board.Copper(),
            [$"{stem}-B_Mask.gbs"] = board.Mask(),
            [$"{stem}-F_SilkS.gto"] = board.Silkscreen(),
            [$"{stem}-Edge_Cuts.gm1"] = board.Outline(),
            [$"{stem}.drl"] = board.Drill(),
        };
    }

    /// <summary>The files in a zip, as a board house's order form takes them.</summary>
    public static void WriteZip(ProtoboardLayout layout, Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (file, text) in Files(layout, name))
        {
            var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    private static string Safe(string name)
    {
        var text = new string([.. (string.IsNullOrWhiteSpace(name) ? "board" : name.Trim()).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return text.Length == 0 ? "board" : text;
    }

    private sealed class Board(ProtoboardLayout layout)
    {
        private readonly double _height = SolderedLayout.Height(layout.Rows);
        private readonly double _width = SolderedLayout.Width(layout.Columns);

        private (double X, double Y) Mm((double X, double Y) pitches) => (pitches.X * PcbRouter.Pitch, (_height - pitches.Y) * PcbRouter.Pitch);

        private (double X, double Y) Mm(BoardHole hole) => Mm(SolderedLayout.Position(hole));

        private static string C(double mm) => ((long)Math.Round(mm * 1e6)).ToString(CultureInfo.InvariantCulture);

        private static string F(double mm) => mm.ToString("0.000", CultureInfo.InvariantCulture);

        private static StringBuilder Header(string function, string polarity)
        {
            var g = new StringBuilder();
            g.Append("%TF.GenerationSoftware,CirqAvalonia*%\n");
            g.Append(CultureInfo.InvariantCulture, $"%TF.FileFunction,{function}*%\n");
            g.Append(CultureInfo.InvariantCulture, $"%TF.FilePolarity,{polarity}*%\n");
            g.Append("%FSLAX46Y46*%\n%MOMM*%\n%LPD*%\nG01*\n");
            return g;
        }

        private static void Flash(StringBuilder g, (double X, double Y) at) => g.Append(CultureInfo.InvariantCulture, $"X{C(at.X)}Y{C(at.Y)}D03*\n");

        private static void Line(StringBuilder g, IEnumerable<(double X, double Y)> points)
        {
            var first = true;
            foreach (var (x, y) in points)
            {
                g.Append(CultureInfo.InvariantCulture, $"X{C(x)}Y{C(y)}{(first ? "D02" : "D01")}*\n");
                first = false;
            }
        }

        /// <summary>Every pin's hole, the square ones (pin 1 of a chip or header) apart.</summary>
        private (List<(double X, double Y)> Round, List<(double X, double Y)> Square) Pads()
        {
            var round = new List<(double, double)>();
            var square = new List<(double, double)>();
            var seen = new HashSet<BoardHole>();
            foreach (var part in layout.Parts)
                for (var i = 0; i < part.Holes.Count; i++)
                {
                    if (!seen.Add(part.Holes[i])) continue;
                    (i == 0 && part.Package.Form is BoardForm.Dip or BoardForm.Inline ? square : round).Add(Mm(part.Holes[i]));
                }

            foreach (var lead in layout.Bench.SelectMany(b => b.Leads))
                if (seen.Add(lead.To)) round.Add(Mm(lead.To));
            foreach (var jumper in layout.Wires.Where(w => !w.IsTrace))
                foreach (var end in new[] { jumper.From, jumper.To })
                    if (seen.Add(end)) round.Add(Mm(end));
            return (round, square);
        }

        public string Copper()
        {
            var g = Header("Copper,L1,Bot", "Positive");
            g.Append(CultureInfo.InvariantCulture, $"%ADD10C,{F(PcbRouter.PadDiameter)}*%\n%ADD11R,{F(PcbRouter.PadDiameter)}X{F(PcbRouter.PadDiameter)}*%\n");
            g.Append(CultureInfo.InvariantCulture, $"%ADD12C,{F(PcbRouter.SignalWidth)}*%\n%ADD13C,{F(PcbRouter.PowerWidth)}*%\n");
            foreach (var (width, aperture) in new[] { (false, "D12"), (true, "D13") })
            {
                g.Append(aperture).Append("*\n");
                foreach (var trace in layout.Wires.Where(w => w.IsTrace && (w.Role != WireRole.Signal) == width)) Line(g, trace.Route.Select(Mm));
            }

            var (round, square) = Pads();
            g.Append("D10*\n");
            foreach (var pad in round) Flash(g, pad);
            g.Append("D11*\n");
            foreach (var pad in square) Flash(g, pad);
            return g.Append("M02*\n").ToString();
        }

        public string Mask()
        {
            var g = Header("Soldermask,Bot", "Negative");
            var size = PcbRouter.PadDiameter + (2 * MaskExpansion);
            g.Append(CultureInfo.InvariantCulture, $"%ADD10C,{F(size)}*%\n%ADD11R,{F(size)}X{F(size)}*%\n");
            var (round, square) = Pads();
            g.Append("D10*\n");
            foreach (var pad in round) Flash(g, pad);
            g.Append("D11*\n");
            foreach (var pad in square) Flash(g, pad);
            return g.Append("M02*\n").ToString();
        }

        public string Silkscreen()
        {
            var g = Header("Legend,Top", "Positive");
            g.Append(CultureInfo.InvariantCulture, $"%ADD10C,{F(SilkWidth)}*%\nD10*\n");
            foreach (var part in layout.Parts)
            {
                var points = part.Holes.Select(SolderedLayout.Position).ToList();
                var (left, top, right, bottom) = (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
                switch (part.Package.Form)
                {
                    case BoardForm.Dip:
                        Box(g, left - 0.5, top + 0.45, right + 0.5, bottom - 0.45);

                        // The notch at the pin 1 end.
                        var middle = (top + bottom) / 2;
                        Line(g, Enumerable.Range(0, 9).Select(i => Mm((left - 0.5 + (0.3 * Math.Sin(i * Math.PI / 8)), middle - (0.3 * Math.Cos(i * Math.PI / 8))))));
                        break;
                    case BoardForm.Inline:
                        Box(g, left - 0.6, top - 0.6, right + 0.6, bottom + 0.6);
                        break;
                    default:
                        // A body between the leads, lying the way they do, with a lead out to each pad.
                        var (a, b) = (points[0], points[1]);
                        var length = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
                        if (length < 1e-9) break;
                        var (ux, uy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
                        var (nx, ny) = (-uy * 0.4, ux * 0.4);
                        var body = Math.Max(0.4, (length - 1.4) / 2);
                        var (cx, cy) = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                        var (sx, sy, ex, ey) = (cx - (ux * body), cy - (uy * body), cx + (ux * body), cy + (uy * body));
                        Line(g, new[] { (sx + nx, sy + ny), (ex + nx, ey + ny), (ex - nx, ey - ny), (sx - nx, sy - ny), (sx + nx, sy + ny) }.Select(Mm));
                        Line(g, new[] { (a.X + (ux * 0.5), a.Y + (uy * 0.5)), (sx, sy) }.Select(Mm));
                        Line(g, new[] { (ex, ey), (b.X - (ux * 0.5), b.Y - (uy * 0.5)) }.Select(Mm));
                        break;
                }

                var at = Mm((left - 0.5, top - (part.Package.Form == BoardForm.Dip ? 0.75 : 0.9)));
                Text(g, part.Part.Name, at.X, at.Y);
            }

            foreach (var jumper in layout.Wires.Where(w => !w.IsTrace)) Line(g, new[] { Mm(jumper.From), Mm(jumper.To) });
            return g.Append("M02*\n").ToString();
        }

        private void Box(StringBuilder g, double left, double top, double right, double bottom) =>
            Line(g, new[] { (left, top), (right, top), (right, bottom), (left, bottom), (left, top) }.Select(Mm));

        public string Outline()
        {
            var g = Header("Profile,NP", "Positive");
            g.Append(CultureInfo.InvariantCulture, $"%ADD10C,{F(EdgeWidth)}*%\nD10*\n");
            var (w, h) = (_width * PcbRouter.Pitch, _height * PcbRouter.Pitch);
            Line(g, [(0, 0), (w, 0), (w, h), (0, h), (0, 0)]);
            return g.Append("M02*\n").ToString();
        }

        public string Drill()
        {
            var text = new StringBuilder();
            text.Append("M48\n; DRILL file from CirqAvalonia\nFMAT,2\nMETRIC\n");
            text.Append(CultureInfo.InvariantCulture, $"T1C{F(PcbRouter.DrillDiameter)}\n%\nG90\nG05\nT1\n");
            var (round, square) = Pads();
            foreach (var (x, y) in round.Concat(square)) text.Append(CultureInfo.InvariantCulture, $"X{F(x)}Y{F(y)}\n");
            return text.Append("M30\n").ToString();
        }

        /// <summary>Text in a stroke font, as silkscreen is printed: its baseline's left end at (x, y), in millimetres.</summary>
        private static void Text(StringBuilder g, string text, double x, double y)
        {
            var scale = TextHeight / 6;
            foreach (var c in text.ToUpperInvariant())
            {
                if (Glyphs.TryGetValue(c, out var strokes))
                    foreach (var stroke in strokes.Split('|'))
                        Line(g, stroke.Split(' ').Select(p => p.Split(','))
                            .Select(p => (x + (int.Parse(p[0], CultureInfo.InvariantCulture) * scale), y + (int.Parse(p[1], CultureInfo.InvariantCulture) * scale))));
                x += 5.5 * scale;
            }
        }

        /// <summary>Strokes on a 4 by 6 grid, y up, each a polyline of x,y points; strokes split by |.</summary>
        private static readonly Dictionary<char, string> Glyphs = new()
        {
            ['A'] = "0,0 0,4 2,6 4,4 4,0|0,3 4,3",
            ['B'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3|3,3 4,2 4,1 3,0 0,0",
            ['C'] = "4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5",
            ['D'] = "0,0 0,6 3,6 4,5 4,1 3,0 0,0",
            ['E'] = "4,0 0,0 0,6 4,6|0,3 3,3",
            ['F'] = "0,0 0,6 4,6|0,3 3,3",
            ['G'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1 4,3 2,3",
            ['H'] = "0,0 0,6|4,0 4,6|0,3 4,3",
            ['I'] = "1,0 3,0|2,0 2,6|1,6 3,6",
            ['J'] = "0,1 1,0 3,0 4,1 4,6",
            ['K'] = "0,0 0,6|4,6 0,2|1,3 4,0",
            ['L'] = "0,6 0,0 4,0",
            ['M'] = "0,0 0,6 2,3 4,6 4,0",
            ['N'] = "0,0 0,6 4,0 4,6",
            ['O'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0",
            ['P'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3",
            ['Q'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0|2,2 4,0",
            ['R'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3|2,3 4,0",
            ['S'] = "0,1 1,0 3,0 4,1 4,2 3,3 1,3 0,4 0,5 1,6 3,6 4,5",
            ['T'] = "0,6 4,6|2,6 2,0",
            ['U'] = "0,6 0,1 1,0 3,0 4,1 4,6",
            ['V'] = "0,6 2,0 4,6",
            ['W'] = "0,6 1,0 2,3 3,0 4,6",
            ['X'] = "0,0 4,6|0,6 4,0",
            ['Y'] = "0,6 2,3 4,6|2,3 2,0",
            ['Z'] = "0,6 4,6 0,0 4,0",
            ['0'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0|0,1 4,5",
            ['1'] = "1,5 2,6 2,0|1,0 3,0",
            ['2'] = "0,5 1,6 3,6 4,5 4,4 0,0 4,0",
            ['3'] = "0,5 1,6 3,6 4,5 4,4 3,3 4,2 4,1 3,0 1,0 0,1|1,3 3,3",
            ['4'] = "3,0 3,6 0,2 4,2",
            ['5'] = "4,6 0,6 0,3 3,3 4,2 4,1 3,0 0,0",
            ['6'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1 4,2 3,3 0,3",
            ['7'] = "0,6 4,6 1,0",
            ['8'] = "1,3 0,4 0,5 1,6 3,6 4,5 4,4 3,3 1,3 0,2 0,1 1,0 3,0 4,1 4,2 3,3",
            ['9'] = "0,1 1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,4 1,3 4,3",
            ['-'] = "1,3 3,3",
            ['+'] = "1,3 3,3|2,2 2,4",
            ['.'] = "2,0 2,1",
        };
    }
}
