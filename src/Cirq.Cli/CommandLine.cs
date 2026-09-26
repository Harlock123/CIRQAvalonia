namespace Cirq.Cli;

/// <summary>
/// The command line: what was asked for, and which command it goes to.
/// <para>
/// Hand-rolled rather than taken from a parser library, for the same reason the rest of this
/// program has no dependencies: it is a few dozen lines, it has to run on a build machine with
/// nothing installed, and a library would be a third of the download for the whole tool.
/// </para>
/// </summary>
public static class CommandLine
{
    /// <summary>Nothing wrong.</summary>
    public const int Ok = 0;

    /// <summary>The circuit was checked and it does not meet its requirements.</summary>
    public const int Failed = 1;

    /// <summary>The command itself was wrong — no such file, an argument that is not a number.</summary>
    public const int Misused = 2;

    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            Usage(output);
            return arguments.Count == 0 ? Misused : Ok;
        }

        var verb = arguments[0];
        var rest = arguments.Skip(1).ToList();

        // --help after a verb asks about that verb, which is what everybody tries first.
        if (rest.Any(IsHelp))
        {
            Usage(output, verb);
            return Ok;
        }

        try
        {
            return verb switch
            {
                "check" => Commands.Check(new Options(rest), output, error),
                "run" => Commands.RunTransient(new Options(rest), output, error),
                "netlist" => Commands.Netlist(new Options(rest), output, error),
                "info" => Commands.Info(new Options(rest), output, error),
                "version" => Version(output),
                _ => Unknown(verb, error),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            return Misused;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return Misused;
        }
    }

    private static bool IsHelp(string argument) =>
        argument is "--help" or "-h" or "help" or "-?" or "/?";

    private static int Version(TextWriter output)
    {
        var version = typeof(CommandLine).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";

        // A build that was never given a version says so rather than claiming 1.0.0.
        output.WriteLine(version.Split('+')[0]);

        return Ok;
    }

    private static int Unknown(string verb, TextWriter error)
    {
        error.WriteLine($"cirq: there is no command called \"{verb}\". Try cirq --help.");
        return Misused;
    }

    private static void Usage(TextWriter output, string? verb = null)
    {
        if (verb == "check")
        {
            output.WriteLine("""
                cirq check <circuit.cirq> [options]

                  Runs the circuit and holds it to the requirements saved in it. Exits 1 if any of
                  them is not met, so it can be the last line of a build.

                  --for <seconds>      How long to run. Default 10 ms.
                  --over <what>        Check across a range instead of at one point: "temperature",
                                       or a part and its setting, as R1.Resistance.
                  --from <value>       The bottom of that range. Default -40 for a temperature.
                  --to <value>         The top of it. Default 85.
                  --points <n>         How many values across the range. Default 8.
                  --quiet              Only the verdict line.
                """);
            return;
        }

        if (verb == "netlist")
        {
            output.WriteLine("""
                cirq netlist <circuit.cirq> [-o <path>]

                  Writes the circuit out as a SPICE deck, to the screen or to a file. Anything that
                  has no SPICE element — an annotation, a development board — is named on the error
                  stream rather than left out silently.
                """);
            return;
        }

        if (verb == "run")
        {
            output.WriteLine("""
                cirq run <circuit.cirq> [options]

                  Runs the circuit and writes what the probes recorded.

                  --for <seconds>      How long to run. Default 10 ms.
                  --csv <path>         Where to write the samples. Default: beside the circuit.
                  --every <seconds>    Sample interval. Default: the run divided by two thousand.
                """);
            return;
        }

        output.WriteLine("""
            cirq — the circuit editor's engine, without the editor.

              cirq check <circuit.cirq>     Hold it to the requirements saved in it
              cirq run <circuit.cirq>       Run it and write the traces as CSV
              cirq netlist <circuit.cirq>   Write it out as a SPICE netlist
              cirq info <circuit.cirq>      What is in it: parts, nets, probes, requirements
              cirq version                  What version this is

            Exit codes: 0 all well, 1 a requirement was not met, 2 the command was wrong.

            cirq <command> --help says more about one command.
            """);
    }
}

/// <summary>Thrown when the command line itself is wrong, as opposed to the circuit being wrong.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The arguments of one command: a path, and named options after it.
/// <para>
/// Options are <c>--name value</c> or <c>--flag</c>, which is the whole grammar. Anything cleverer
/// would need explaining, and there is nothing here that needs it.
/// </para>
/// </summary>
public sealed class Options
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.Ordinal);

    public Options(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            // A lone "-" is not an option, and neither is a negative number: "--from -40" has to
            // reach the option before it as a value rather than being read as an option itself.
            var isOption = argument.Length > 1
                           && argument[0] == '-'
                           && !char.IsAsciiDigit(argument[1])
                           && argument[1] != '.';

            if (!isOption)
            {
                if (Path is not null)
                    throw new UsageException($"cirq: one circuit at a time — \"{argument}\" is a second.");

                Path = argument;
                continue;
            }

            // Short options are single-dashed — "-o out.cir" — and are stored under the letter.
            var name = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : argument[1..];
            var next = i + 1 < arguments.Count ? arguments[i + 1] : null;

            // A value, unless what follows is another option. A negative number is a value: --from
            // -40 is a range that starts below freezing, not a flag called "40".
            var value = next is not null
                        && (next.Length < 2 || next[0] != '-'
                            || char.IsAsciiDigit(next[1]) || next[1] == '.')
                ? arguments[++i]
                : null;

            _named[name] = value;
        }
    }

    /// <summary>The circuit file, which every command needs.</summary>
    public string? Path { get; }

    public bool Has(string name) => _named.ContainsKey(name);

    public string? Text(string name) => _named.GetValueOrDefault(name);

    /// <summary>A number, or the default when it was not given. Refuses anything that is not one.</summary>
    public double Number(string name, double fallback)
    {
        if (!_named.TryGetValue(name, out var text) || text is null) return fallback;

        return double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new UsageException($"cirq: --{name} wants a number, not \"{text}\".");
    }

    public int Count(string name, int fallback)
    {
        var value = Number(name, fallback);

        return value is >= 2 and <= 1000
            ? (int)Math.Round(value)
            : throw new UsageException($"cirq: --{name} wants between 2 and 1000 points.");
    }

    /// <summary>The circuit file, or a usage error naming what was missing.</summary>
    public string RequirePath()
    {
        if (Path is null) throw new UsageException("cirq: which circuit? Give it a .cirq file.");

        if (!File.Exists(Path)) throw new UsageException($"cirq: there is no file called \"{Path}\".");

        return Path;
    }
}
