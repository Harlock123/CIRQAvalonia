using System.Globalization;
using System.Net;
using System.Text;
using Cirq.Components.Explaining;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Core.Verification;

namespace Cirq.UI.Services;

/// <summary>What a report should contain.</summary>
/// <param name="Title">The heading. The circuit's own title by default.</param>
/// <param name="Notes">Anything the author wants to say, shown under the heading.</param>
/// <param name="Schematic">The schematic as SVG, or null to leave the drawing out.</param>
/// <param name="Specs">How every requirement came out.</param>
/// <param name="Explanations">What the explainer made of the circuit.</param>
public sealed record ReportContent(
    string Title,
    string Notes,
    string? Schematic,
    IReadOnlyList<SpecResult> Specs,
    IReadOnlyList<Explanation> Explanations);

/// <summary>
/// One document with the drawing, the parts, the requirements and what the circuit is.
/// <para>
/// Every piece of this already existed — the exporter draws schematics, the parts list is a
/// table, requirements produce verdicts, the explainer produces sentences. What was missing was
/// the thing that assembles them, which is the artefact somebody actually hands to another
/// person. A folder of PNGs is not a deliverable; a page that opens with "all seven requirements
/// met" is.
/// </para>
/// <para>
/// HTML, and self-contained. It opens on anything without asking what it was made with, it prints
/// from a browser, and the schematic goes in as SVG so it is still sharp when somebody zooms in
/// on the one corner they care about.
/// </para>
/// </summary>
public static class DesignReport
{
    /// <summary>Writes the report and returns where it went.</summary>
    public static string Write(Circuit circuit, ReportContent content, string path)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        File.WriteAllText(path, Build(circuit, content), new UTF8Encoding(false));

        return path;
    }

    /// <summary>The document itself, so it can be checked without writing a file.</summary>
    public static string Build(Circuit circuit, ReportContent content)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(content);

        var page = new StringBuilder();

        page.AppendLine("<!DOCTYPE html>");
        page.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        page.AppendLine($"<title>{Escape(content.Title)}</title>");
        page.AppendLine(Style);
        page.AppendLine("</head><body>");

        page.AppendLine($"<h1>{Escape(content.Title)}</h1>");
        page.AppendLine(
            $"<p class=\"when\">{DateTime.Now.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture)}" +
            $" · CirqAvalonia</p>");

        if (content.Notes.Trim().Length > 0)
            page.AppendLine($"<p class=\"notes\">{Escape(content.Notes)}</p>");

        Verdict(page, content.Specs);
        Schematic(page, content.Schematic);
        What(page, content.Explanations);
        Requirements(page, content.Specs);
        Parts(page, circuit);
        Conditions(page, circuit);

        page.AppendLine("</body></html>");

        return page.ToString();
    }

    /// <summary>
    /// The headline, at the top where somebody who reads nothing else will still see it. A report
    /// whose verdict is on page four is a report whose verdict nobody knows.
    /// </summary>
    private static void Verdict(StringBuilder page, IReadOnlyList<SpecResult> specs)
    {
        if (specs.Count == 0) return;

        var failed = specs.Count(s => s.Passed == false);
        var unknown = specs.Count(s => s.Passed is null);

        var state = failed > 0 ? "bad" : unknown > 0 ? "unsure" : "good";

        page.AppendLine($"<p class=\"verdict {state}\">{Escape(SpecCheck.Summarise(specs))}</p>");
    }

    private static void Schematic(StringBuilder page, string? svg)
    {
        if (svg is null) return;

        page.AppendLine("<h2>The circuit</h2>");

        // Straight in rather than as a data URI: it is already XML, it scales, and a reader can
        // select the designators as text.
        page.AppendLine($"<div class=\"schematic\">{svg}</div>");
    }

    private static void What(StringBuilder page, IReadOnlyList<Explanation> explanations)
    {
        if (explanations.Count == 0) return;

        page.AppendLine("<h2>What it is</h2><ul class=\"what\">");

        foreach (var explanation in explanations)
        {
            page.AppendLine(
                $"<li><strong>{Escape(explanation.Headline)}</strong> — {Escape(explanation.Detail)}</li>");
        }

        page.AppendLine("</ul>");
    }

    private static void Requirements(StringBuilder page, IReadOnlyList<SpecResult> specs)
    {
        if (specs.Count == 0) return;

        page.AppendLine("<h2>Requirements</h2>");
        page.AppendLine("<table><thead><tr><th>Requirement</th><th>Asked for</th>" +
                        "<th>Measured</th><th>Margin</th><th></th></tr></thead><tbody>");

        foreach (var result in specs)
        {
            var state = result.Passed switch
            {
                true => "good",
                false => "bad",
                _ => "unsure",
            };

            var verdict = result.Passed switch
            {
                true => "met",
                false => "not met",
                _ => "not measurable",
            };

            var measured = result.Measured is { } value
                ? Escape(SiPrefix.Format(value, result.Spec.Unit))
                : "—";

            var margin = result.Margin is { } room ? $"{room * 100:0}%" : "—";

            page.AppendLine(
                $"<tr class=\"{state}\"><td>{Escape(result.Spec.Name)}</td>" +
                $"<td>{Escape(result.Spec.Describe())}</td>" +
                $"<td>{measured}</td><td>{margin}</td><td>{verdict}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    private static void Parts(StringBuilder page, Circuit circuit)
    {
        var rows = PartsList.For(circuit);

        if (rows.Count == 0) return;

        page.AppendLine("<h2>Parts</h2>");
        page.AppendLine("<table><thead><tr><th>Qty</th><th>Ref</th><th>Part</th>" +
                        "<th>Value</th></tr></thead><tbody>");

        foreach (var row in rows)
        {
            page.AppendLine(
                $"<tr><td>{row.Quantity}</td><td>{Escape(row.Designators)}</td>" +
                $"<td>{Escape(row.Part)}</td><td>{Escape(row.Value)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    /// <summary>
    /// What the circuit was run at. A number without its conditions is not a measurement, and a
    /// report that leaves them out is one nobody can repeat.
    /// </summary>
    private static void Conditions(StringBuilder page, Circuit circuit)
    {
        page.AppendLine("<h2>Conditions</h2><table><tbody>");
        page.AppendLine(
            $"<tr><td>Ambient temperature</td><td>{circuit.AmbientTemperatureCelsius:0.##} °C</td></tr>");
        page.AppendLine($"<tr><td>Parts</td><td>{circuit.Components.Count}</td></tr>");
        page.AppendLine($"<tr><td>Probes</td><td>{circuit.Probes.Count}</td></tr>");
        page.AppendLine("</tbody></table>");
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Plain, printable and self-contained. No web fonts and no script: a report that needs the
    /// internet to render is one that stops working the moment it is filed.
    /// </summary>
    private const string Style = """
        <style>
          body { font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
                 max-width: 52rem; margin: 2.5rem auto; padding: 0 1.5rem; color: #1a1d21; }
          h1 { font-size: 1.7rem; margin: 0 0 .2rem; }
          h2 { font-size: 1.15rem; margin: 2.2rem 0 .6rem;
               border-bottom: 1px solid #d8dde3; padding-bottom: .3rem; }
          .when { color: #6b737c; margin: 0 0 1.4rem; font-size: .85rem; }
          .notes { background: #f4f6f8; padding: .8rem 1rem; border-radius: 4px; }
          .verdict { font-size: 1.1rem; font-weight: 600; padding: .7rem 1rem;
                     border-radius: 4px; border-left: 4px solid; }
          .verdict.good { background: #eef8f0; border-color: #2e9e48; }
          .verdict.bad { background: #fdeeee; border-color: #c0392b; }
          .verdict.unsure { background: #fdf6e8; border-color: #d19b1e; }
          table { border-collapse: collapse; width: 100%; font-size: .93rem; }
          th, td { text-align: left; padding: .4rem .6rem; border-bottom: 1px solid #e6eaee; }
          th { color: #6b737c; font-weight: 600; font-size: .8rem;
               text-transform: uppercase; letter-spacing: .03em; }
          tr.good td:last-child { color: #2e9e48; font-weight: 600; }
          tr.bad td:last-child { color: #c0392b; font-weight: 600; }
          tr.unsure td:last-child { color: #8a8f96; }
          ul.what { padding-left: 1.2rem; }
          ul.what li { margin: .35rem 0; }
          .schematic svg { max-width: 100%; height: auto; }
          @media print { body { margin: 0; max-width: none; } h2 { break-after: avoid; } }
        </style>
        """;
}
