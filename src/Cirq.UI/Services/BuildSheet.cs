using System.Net;
using System.Text;
using Cirq.Components.Protoboard;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>
/// The build guide as a page to print and take to the bench: a picture of the finished board, the parts to gather with
/// where each goes, and every step numbered with a box to tick. One self-contained HTML file, the picture inside it, so it
/// opens anywhere and prints from any browser.
/// </summary>
public static class BuildSheet
{
    public static void Write(string path, string title, Circuit circuit, ProtoboardLayout layout, IReadOnlyList<BuildStep> steps)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        File.WriteAllText(path, Html(title, circuit, layout, steps));
    }

    public static string Html(string title, Circuit circuit, ProtoboardLayout layout, IReadOnlyList<BuildStep> steps)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(steps);
        var picture = Picture(circuit);
        var on = layout.Kind switch
        {
            BoardKind.Stripboard => "stripboard",
            BoardKind.Perfboard => "perfboard",
            BoardKind.Pcb => "a printed circuit board",
            _ => layout.Boards == 1 ? "a breadboard" : $"{layout.Boards} breadboards",
        };

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
            .Append(E(title)).Append(" — build sheet</title><style>")
            .Append("body{font:14px/1.45 system-ui,sans-serif;color:#1f2328;max-width:60rem;margin:2rem auto;padding:0 1rem}")
            .Append("h1{font-size:1.6rem;margin:0 0 .2rem}h2{font-size:1.1rem;margin:1.6rem 0 .5rem}p.sub{color:#59636e;margin:0 0 1rem}")
            .Append("img{max-width:100%;border:1px solid #d0d7de;border-radius:6px}table{border-collapse:collapse;width:100%}")
            .Append("td,th{border-bottom:1px solid #d8dee4;padding:.3rem .5rem;text-align:left;vertical-align:top}th{color:#59636e;font-weight:600}")
            .Append("ol{padding-left:0;list-style:none}li{display:flex;gap:.6rem;padding:.35rem 0;border-bottom:1px solid #eef1f4}")
            .Append(".box{flex:none;width:1rem;height:1rem;border:1.5px solid #59636e;border-radius:3px;margin-top:.15rem}")
            .Append(".n{flex:none;width:2rem;color:#59636e;text-align:right;font-variant-numeric:tabular-nums}")
            .Append(".ok{color:#1a7f37}.bad{color:#cf222e}@media print{body{margin:0}li{break-inside:avoid}}")
            .Append("</style></head><body>");
        html.Append("<h1>").Append(E(title)).Append("</h1><p class=\"sub\">Built on ").Append(E(on)).Append(": ")
            .Append(layout.Parts.Count).Append(" parts on the board, ").Append(layout.Bench.Count).Append(" on the bench, ")
            .Append(layout.Kind == BoardKind.Pcb ? layout.Wires.Count(w => w.IsTrace) : layout.Wires.Count)
            .Append(layout.Kind switch { BoardKind.Stripboard => " wire links, ", BoardKind.Pcb => $" traces, {layout.Wires.Count(w => !w.IsTrace)} jumpers, ", _ => " wires, " })
            .Append(layout.Kind == BoardKind.Stripboard ? $"{layout.Cuts.Count} strip cuts. " : "")
            .Append(layout.Matches ? "<span class=\"ok\">The board matches the schematic.</span>" : $"<span class=\"bad\">{E(string.Join(" ", layout.Problems))}</span>")
            .Append("</p>");
        if (picture is not null) html.Append("<img alt=\"The finished board\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(picture)).Append("\">");

        html.Append("<h2>Parts</h2><table><tr><th>Part</th><th>What</th><th>Goes in</th></tr>");
        foreach (var part in layout.Parts.OrderBy(p => p.Part.Name, StringComparer.Ordinal))
        {
            var holes = string.Join(" ", part.Package.Pins.Select((pin, i) => pin is null ? null : $"{pin.Name} {layout.HoleName(part.Holes[i])}").Where(t => t is not null));
            html.Append("<tr><td>").Append(E(part.Part.Name)).Append("</td><td>").Append(E(part.Package.Label)).Append("</td><td>").Append(E(holes)).Append("</td></tr>");
        }

        foreach (var item in layout.Bench)
            html.Append("<tr><td>").Append(E(item.Part.Name)).Append("</td><td>").Append(E(item.Package.Label)).Append("</td><td>on the bench</td></tr>");
        html.Append("</table>");

        html.Append("<h2>Steps</h2><ol>");
        foreach (var step in steps)
            html.Append("<li><span class=\"box\"></span><span class=\"n\">").Append(step.Number).Append(".</span><span>").Append(E(step.Text)).Append("</span></li>");
        html.Append("</ol></body></html>");
        return html.ToString();
    }

    /// <summary>The board as the protoboard view draws it, as PNG bytes; null when there is nothing on it.</summary>
    private static byte[]? Picture(Circuit circuit)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"cirq-build-{Guid.NewGuid():N}.png");
        try
        {
            CircuitExporter.WriteProtoboard(circuit, temp, new ExportOptions(ExportFormat.Png, ExportContent.Protoboard, RasterScale: 1.4));
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
