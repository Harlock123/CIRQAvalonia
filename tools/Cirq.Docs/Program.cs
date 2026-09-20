using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Cirq.Docs;

/// <summary>
/// Turns the Markdown user guide into a single self-contained HTML file laid out for print, which
/// a browser then prints to PDF.
/// <para>
/// The guide is written once, in Markdown, and stays the source of truth — this reads it rather
/// than duplicating it, so there is no second copy to drift. What the tool adds is the part
/// Markdown has no way to express: a title page, a table of contents, page breaks in sensible
/// places, and a stylesheet that knows the difference between a screen and a sheet of paper.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var markdown = File.ReadAllText(options.Source);

            var pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()      // tables, auto-identifiers, footnotes, the rest
                .Build();

            var document = Markdown.Parse(markdown, pipeline);
            var body = Markdown.ToHtml(markdown, pipeline);

            // Images are written relative to the guide; the browser is given the file it is
            // printing and resolves them against that, so the HTML goes beside the Markdown.
            var html = Compose(options, body, Headings(document, markdown));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
            File.WriteAllText(options.Output, html, new UTF8Encoding(false));

            Console.WriteLine($"{options.Output}: {html.Length:N0} bytes from {markdown.Length:N0} of Markdown");
            return 0;
        }
        catch (Exception e) when (e is IOException or ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private sealed record Options(string Source, string Output, string Title, string Version, string Paper)
    {
        public static Options Parse(string[] args)
        {
            string? source = null, output = null;
            var title = "CirqAvalonia User Guide";
            var version = string.Empty;
            var paper = "A4";

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--in" when i + 1 < args.Length: source = args[++i]; break;
                    case "--out" when i + 1 < args.Length: output = args[++i]; break;
                    case "--title" when i + 1 < args.Length: title = args[++i]; break;
                    case "--version" when i + 1 < args.Length: version = args[++i]; break;
                    case "--paper" when i + 1 < args.Length: paper = args[++i]; break;
                    default: throw new ArgumentException($"unrecognised argument '{args[i]}'");
                }
            }

            if (source is null || output is null)
                throw new ArgumentException("usage: --in <guide.md> --out <guide.html> [--title T] [--version V] [--paper A4|Letter]");

            if (!File.Exists(source)) throw new FileNotFoundException($"no such file: {source}");

            return new Options(source, output, title, version, Normalise(paper));
        }

        private static string Normalise(string value) => value.ToLowerInvariant() switch
        {
            "a4" => "A4",
            "letter" => "Letter",
            _ => throw new ArgumentException($"paper must be A4 or Letter, not '{value}'"),
        };
    }

    private sealed record Heading(int Level, string Text, string Id);

    /// <summary>
    /// The headings the contents page is built from — the two top levels only, because a guide
    /// with every fourth-level heading in its contents has a contents page nobody reads.
    /// </summary>
    private static List<Heading> Headings(MarkdownDocument document, string markdown)
    {
        List<Heading> found = [];

        foreach (var block in document.Descendants<HeadingBlock>())
        {
            if (block.Level is not (2 or 3)) continue;

            var text = markdown
                .Substring(block.Span.Start, block.Span.Length)
                .TrimStart('#', ' ')
                .Trim();

            var id = block.GetAttributes().Id;
            if (string.IsNullOrEmpty(id)) continue;

            found.Add(new Heading(block.Level, Strip(text), id));
        }

        return found;
    }

    /// <summary>Inline markup removed, since a contents entry is a label rather than prose.</summary>
    private static string Strip(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"`(.+?)`", "$1");
        text = Regex.Replace(text, @"\[(.+?)\]\([^)]*\)", "$1");

        return text;
    }

    private static string Compose(Options options, string body, List<Heading> headings)
    {
        var contents = new StringBuilder();

        foreach (var heading in headings)
        {
            contents.Append($"""<li class="l{heading.Level}"><a href="#{heading.Id}">""");
            contents.Append(Escape(heading.Text));
            contents.AppendLine("</a></li>");
        }

        var subtitle = options.Version.Length > 0 ? $"Version {Escape(options.Version)}" : string.Empty;

        return $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>{Escape(options.Title)}</title>
        <style>
        {Stylesheet(options.Paper)}
        </style>
        </head>
        <body>
        <section class="title-page">
          <h1 class="title">{Escape(options.Title)}</h1>
          <p class="subtitle">{subtitle}</p>
          <p class="strap">An electronic circuit analyzer and mixed-signal simulator</p>
          <p class="built">{DateTime.UtcNow:d MMMM yyyy}</p>
        </section>

        <section class="contents">
          <h1>Contents</h1>
          <ul>
        {contents}
          </ul>
        </section>

        <main>
        {body}
        </main>
        </body>
        </html>
        """;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// Print styling. Everything here is about paper rather than screens: a serif face because it
    /// is being read on a page, tables that may not be split across one, and images that are not
    /// left stranded from the paragraph that introduces them.
    /// </summary>
    private static string Stylesheet(string paper) => $$"""
        @page { size: {{paper}}; margin: 20mm 18mm; }

        :root { --rule: #c9ced6; --muted: #5b6472; --code-bg: #f4f5f7; }

        body {
          font: 10.5pt/1.55 "Georgia", "Times New Roman", serif;
          color: #15181d;
          margin: 0;
          -webkit-print-color-adjust: exact;
          print-color-adjust: exact;
        }

        /* ---- title page ---- */
        .title-page { height: 86vh; display: flex; flex-direction: column; justify-content: center; }
        .title-page .title { font-size: 34pt; margin: 0 0 4mm; border: 0; }
        .title-page .subtitle { font-size: 13pt; color: var(--muted); margin: 0 0 12mm; }
        .title-page .strap { font-size: 12pt; margin: 0; }
        .title-page .built { font-size: 10pt; color: var(--muted); margin-top: 3mm; }

        /* ---- contents ---- */
        .contents { break-before: page; }
        .contents h1 { font-size: 20pt; }
        .contents ul { list-style: none; padding: 0; margin: 0; column-count: 2; column-gap: 10mm; }
        .contents li { break-inside: avoid; margin: 0 0 1.2mm; }
        .contents a { text-decoration: none; color: #15181d; }
        .contents .l3 { padding-left: 6mm; font-size: 9.5pt; color: var(--muted); }

        main { break-before: page; }

        /* ---- headings ---- */
        h1, h2, h3, h4 { font-family: "Helvetica Neue", Arial, sans-serif; line-height: 1.25; }
        h1 { font-size: 22pt; margin: 0 0 6mm; }
        h2 {
          font-size: 16pt; margin: 9mm 0 3mm;
          padding-bottom: 1.5mm; border-bottom: 0.4pt solid var(--rule);
          break-before: page; break-after: avoid;
        }
        h2:first-of-type { break-before: avoid; }
        h3 { font-size: 12.5pt; margin: 6mm 0 2mm; break-after: avoid; }
        h4 { font-size: 11pt; margin: 4mm 0 1.5mm; break-after: avoid; }

        p { margin: 0 0 3mm; orphans: 2; widows: 2; }

        /* ---- code ---- */
        code {
          font-family: "DejaVu Sans Mono", "Consolas", monospace;
          font-size: 9pt;
          background: var(--code-bg);
          padding: 0.3mm 1mm;
          border-radius: 1mm;
        }
        pre {
          background: var(--code-bg);
          border: 0.4pt solid var(--rule);
          border-radius: 1mm;
          padding: 2.5mm 3mm;
          /* Wrapped rather than scrolled: paper has no scrollbar, and a clipped command line is
             worse than a wrapped one. */
          white-space: pre-wrap;
          word-break: break-word;
          break-inside: avoid;
        }
        pre code { background: none; padding: 0; font-size: 8.5pt; }

        /* ---- tables ---- */
        table {
          border-collapse: collapse;
          width: 100%;
          font-size: 9pt;
          margin: 0 0 4mm;
          break-inside: auto;
        }
        th, td { border: 0.4pt solid var(--rule); padding: 1.2mm 2mm; text-align: left; vertical-align: top; }
        th { background: #eef0f4; font-family: "Helvetica Neue", Arial, sans-serif; font-size: 8.5pt; }
        /* A row split across a page break is unreadable; a long table split between rows is fine. */
        tr { break-inside: avoid; }
        thead { display: table-header-group; }

        /* ---- images ---- */
        img { max-width: 100%; height: auto; display: block; margin: 3mm auto; border: 0.4pt solid var(--rule); }
        /* The alt text is the caption, and both belong on the same page as each other. */
        p:has(> img) { break-inside: avoid; text-align: center; }

        /* ---- the rest ---- */
        blockquote { margin: 0 0 3mm; padding-left: 4mm; border-left: 1pt solid var(--rule); color: var(--muted); }
        hr { border: 0; border-top: 0.4pt solid var(--rule); margin: 5mm 0; }
        ul, ol { margin: 0 0 3mm; padding-left: 6mm; }
        li { margin: 0 0 1mm; }
        a { color: #15181d; }
        strong { font-weight: 700; }
        """;
}
