using System.Net;
using System.Text;
using Cirq.Components.Protoboard;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>
/// The circuit as one web page to share: its schematic (as SVG, so it stays sharp at any size), the board it's built on,
/// the parts to buy, the steps to build it, and the circuit file itself, embedded, to download and open in CirqAvalonia.
/// <para>
/// It is one self-contained file with nothing to fetch, so it can go anywhere a page can: attached to a forum post,
/// put on a class's site or a GitHub Pages repository, or opened straight from disk. Wherever it's put is the link.
/// </para>
/// </summary>
public static class SharePage
{
    public static string Html(string title, Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        var layout = ProtoboardLayout.Arrange(circuit);
        var steps = BuildGuide.Steps(layout);
        var schematic = Drawing(circuit, ExportFormat.Svg, ExportContent.Schematic);
        var board = Drawing(circuit, ExportFormat.Png, ExportContent.Protoboard);
        var file = CircuitSerializer.ToJson(circuit);
        var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(E(title)).Append("</title><style>")
            .Append(":root{--ink:#1f2328;--muted:#59636e;--line:#d8dee4;--ground:#ffffff;--accent:#0b6b4a}")
            .Append("@media (prefers-color-scheme: dark){:root{--ink:#e6edf3;--muted:#9aa5b1;--line:#30363d;--ground:#0d1117;--accent:#3fb98a}}")
            .Append("body{font:15px/1.5 system-ui,sans-serif;color:var(--ink);background:var(--ground);max-width:62rem;margin:2rem auto;padding:0 1rem}")
            .Append("h1{font-size:1.7rem;margin:0 0 .25rem}h2{font-size:1.15rem;margin:2rem 0 .6rem}p.sub{color:var(--muted);margin:0 0 1rem}")
            .Append(".figure{border:1px solid var(--line);border-radius:8px;padding:.6rem;background:#fff;overflow-x:auto}.figure svg,.figure img{max-width:100%;height:auto;display:block;margin:auto}")
            .Append("table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid var(--line);padding:.35rem .5rem;text-align:left;vertical-align:top}th{color:var(--muted);font-weight:600}")
            .Append("ol{padding-left:1.6rem}li{margin:.2rem 0}a.get{display:inline-block;background:var(--accent);color:#fff;padding:.5rem 1rem;border-radius:6px;text-decoration:none;font-weight:600}")
            .Append(".table{overflow-x:auto}</style></head><body>");
        html.Append("<h1>").Append(E(title)).Append("</h1><p class=\"sub\">")
            .Append(circuit.Components.Count).Append(" parts, built on ").Append(E(On(layout))).Append(". ")
            .Append(layout.Matches ? "The board matches the schematic." : E(string.Join(" ", layout.Problems)))
            .Append("</p>");
        html.Append("<p><a class=\"get\" download=\"").Append(E(name)).Append(".cirq\" href=\"data:application/json;base64,")
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(file))).Append("\">Download the circuit (.cirq)</a> ")
            .Append("<span class=\"sub\">Open it in CirqAvalonia to run it, probe it and change it.</span></p>");

        if (schematic is not null)
        {
            var svg = Encoding.UTF8.GetString(schematic);
            var start = svg.IndexOf("<svg", StringComparison.Ordinal);
            html.Append("<h2>The schematic</h2><div class=\"figure\">").Append(start >= 0 ? svg[start..] : svg).Append("</div>");
        }

        if (board is not null)
            html.Append("<h2>On the board</h2><div class=\"figure\"><img alt=\"The circuit built on ").Append(E(On(layout)))
                .Append("\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(board)).Append("\"></div>");

        var parts = BillOfMaterials.Lines(circuit);
        if (parts.Count > 0)
        {
            html.Append("<h2>Parts</h2><div class=\"table\"><table><tr><th>Qty</th><th>Part</th><th>Value</th><th>Package</th><th>Designators</th></tr>");
            foreach (var line in parts)
                html.Append("<tr><td>").Append(line.Quantity).Append("</td><td>").Append(E(line.Part)).Append("</td><td>").Append(E(line.Value))
                    .Append("</td><td>").Append(E(line.Package)).Append("</td><td>").Append(E(string.Join(" ", line.Designators))).Append("</td></tr>");
            html.Append("</table></div>");
        }

        html.Append("<h2>Building it</h2><ol>");
        foreach (var step in steps) html.Append("<li>").Append(E(step.Text)).Append("</li>");
        html.Append("</ol><p class=\"sub\">Made with CirqAvalonia.</p></body></html>");
        return html.ToString();
    }

    public static void Write(string path, string title, Circuit circuit) => File.WriteAllText(path, Html(title, circuit), new UTF8Encoding(false));

    private static string On(ProtoboardLayout layout) => layout.Kind switch
    {
        BoardKind.Stripboard => "stripboard",
        BoardKind.Perfboard => "perfboard",
        BoardKind.Pcb => "a printed circuit board",
        BoardKind.DoubleSidedPcb => "a double-sided printed circuit board",
        _ => layout.Boards == 1 ? "a breadboard" : $"{layout.Boards} breadboards",
    };

    private static byte[]? Drawing(Circuit circuit, ExportFormat format, ExportContent content)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"cirq-share-{Guid.NewGuid():N}{CircuitExporter.Extension(format)}");
        try
        {
            CircuitExporter.Export(circuit, null, temp, new ExportOptions(format, content, RasterScale: 1.4));
            return File.ReadAllBytes(temp);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
