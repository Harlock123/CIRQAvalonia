using System.Globalization;
using Cirq.Core.Primitives;

namespace Cirq.Core.Probing;

/// <summary>What went wrong with an expression, and where.</summary>
public sealed class ExpressionException(string message) : Exception(message);

/// <summary>
/// Arithmetic on recorded traces: <c>Out / In</c>, <c>V1 - V2</c>, <c>I * I * 220</c>.
/// <para>
/// The scope can already show a difference and a power, because those were common enough to be
/// worth their own probe kinds. But every such kind is a guess at what somebody will want, and the
/// list has no end: a ratio, an envelope, an efficiency, a current squared into a resistance, the
/// error between a reference and a feedback. An expression covers all of them and costs one
/// feature rather than a dozen.
/// </para>
/// <para>
/// It works on what the scope has <b>recorded</b> rather than on the circuit, which is the right
/// place for it: it needs no solver support, it applies equally to a trace that came from a run
/// and one that came from anywhere else, and it cannot affect the answer because it happens
/// entirely after the answer.
/// </para>
/// </summary>
public static class TraceExpression
{
    /// <summary>
    /// Works out an expression over some named traces, at every time any of them was sampled.
    /// <para>
    /// The union of the sample times, not the intersection: the traces on a scope are recorded on
    /// the same clock, but nothing guarantees it — and dropping a point because one input happened
    /// not to have been sampled there would quietly shorten the answer. Values between samples are
    /// interpolated, which is what the trace between two points already means.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DataPoint> Evaluate(
        string expression, IReadOnlyDictionary<string, IReadOnlyList<DataPoint>> traces)
    {
        ArgumentNullException.ThrowIfNull(traces);

        var node = Parse(expression, [.. traces.Keys]);

        var times = new SortedSet<double>();
        foreach (var samples in traces.Values)
            foreach (var sample in samples) times.Add(sample.Time);

        if (times.Count == 0) return [];

        // A cursor per trace, walked forward with the times, so the whole evaluation is linear
        // rather than a binary search per name per point.
        var cursors = traces.Keys.ToDictionary(k => k, _ => 0);
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        List<DataPoint> result = [];

        foreach (var time in times)
        {
            foreach (var (name, samples) in traces)
            {
                var cursor = cursors[name];

                while (cursor + 1 < samples.Count && samples[cursor + 1].Time <= time) cursor++;

                cursors[name] = cursor;
                values[name] = Interpolate(samples, cursor, time);
            }

            result.Add(new DataPoint(time, node.Evaluate(values)));
        }

        return result;
    }

    /// <summary>
    /// The same arithmetic over plain numbers rather than over traces.
    /// <para>
    /// The expression <i>language</i> has two uses and only one implementation. A trace expression
    /// works out <c>Out / In</c> at every recorded instant; a circuit parameter works out
    /// <c>Rf / 10</c> once. Underneath they are the same parser and the same tree, evaluated
    /// against a map of names to numbers — the trace version simply evaluates it many times, once
    /// per sample. Writing a second parser for the second use is how two dialects of the same
    /// notation end up in one application.
    /// </para>
    /// </summary>
    public static double Number(
        string expression, IReadOnlyDictionary<string, double> values, string noun = DefaultNoun)
    {
        ArgumentNullException.ThrowIfNull(values);

        return Parse(expression, [.. values.Keys], null, noun).Evaluate(values);
    }

    /// <summary>
    /// Parses an expression once, for a caller that will work it out many times over.
    /// <para>
    /// <see cref="Number"/> parses on every call, which is right for a parameter worked out when
    /// somebody presses a button and wrong inside a solver: a part whose value is an expression is
    /// asked for it several times per Newton iteration, several iterations per time point, for as
    /// many time points as the run is long. Parsing the same eleven characters a few million times
    /// is not a cost anybody should pay to write down a formula.
    /// </para>
    /// </summary>
    public static Compiled Compile(
        string expression, IEnumerable<string> names, string noun = DefaultNoun)
    {
        ArgumentNullException.ThrowIfNull(names);

        var known = names.ToList();
        List<string> used = [];

        var tree = Parse(expression, known, used, noun);

        return new Compiled(tree, used);
    }

    /// <summary>
    /// Checks an expression against a set of names without needing any samples, so a box can go
    /// red as it is typed rather than when it is run. Null when it is fine.
    /// </summary>
    public static string? Validate(
        string expression, IEnumerable<string> names, string noun = DefaultNoun)
    {
        try
        {
            Parse(expression, [.. names], null, noun);
            return null;
        }
        catch (ExpressionException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The names an expression refers to, so a caller knows which traces it needs.</summary>
    public static IReadOnlyList<string> NamesIn(
        string expression, IEnumerable<string> known, string noun = DefaultNoun)
    {
        var found = new List<string>();

        Parse(expression, [.. known], found, noun);

        return found;
    }

    private static double Interpolate(IReadOnlyList<DataPoint> samples, int index, double time)
    {
        if (samples.Count == 0) return 0;
        if (index >= samples.Count - 1) return samples[^1].Value;

        var a = samples[index];
        var b = samples[index + 1];

        if (time <= a.Time) return a.Value;

        var span = b.Time - a.Time;
        if (span <= 0) return b.Value;

        return a.Value + ((time - a.Time) / span * (b.Value - a.Value));
    }

    /// <summary>
    /// An expression that has already been parsed, and the names it turned out to use.
    /// </summary>
    public sealed class Compiled
    {
        private readonly INode _tree;

        internal Compiled(INode tree, IReadOnlyList<string> names)
        {
            _tree = tree;
            Names = names;
        }

        /// <summary>The names it refers to, which is a subset of the ones it was compiled against.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>Works it out. The dictionary has to hold every name in <see cref="Names"/>.</summary>
        public double Evaluate(IReadOnlyDictionary<string, double> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            return _tree.Evaluate(values);
        }
    }

    // ---- the grammar -------------------------------------------------------

    internal interface INode
    {
        double Evaluate(IReadOnlyDictionary<string, double> values);
    }

    internal sealed record Constant(double Value) : INode
    {
        public double Evaluate(IReadOnlyDictionary<string, double> values) => Value;
    }

    internal sealed record Reference(string Name) : INode
    {
        public double Evaluate(IReadOnlyDictionary<string, double> values) =>
            values.TryGetValue(Name, out var value) ? value : 0.0;
    }

    internal sealed record Unary(INode Operand, Func<double, double> Apply) : INode
    {
        public double Evaluate(IReadOnlyDictionary<string, double> values) =>
            Apply(Operand.Evaluate(values));
    }

    internal sealed record Binary(INode Left, INode Right, Func<double, double, double> Apply) : INode
    {
        public double Evaluate(IReadOnlyDictionary<string, double> values) =>
            Apply(Left.Evaluate(values), Right.Evaluate(values));
    }

    /// <summary>
    /// The functions an expression can use. Deliberately short: this is for arithmetic on traces,
    /// not a programming language, and every entry is one somebody would reach for on a scope.
    /// </summary>
    private static readonly Dictionary<string, Func<double, double>> Functions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["abs"] = Math.Abs,
            ["sqrt"] = x => x < 0 ? 0 : Math.Sqrt(x),
            ["exp"] = Math.Exp,

            // Guarded, because a trace passing through zero is ordinary and an answer of minus
            // infinity in the middle of a plot is not useful. The floor is far below anything a
            // circuit produces.
            ["log"] = x => Math.Log(Math.Max(Math.Abs(x), 1e-30)),
            ["log10"] = x => Math.Log10(Math.Max(Math.Abs(x), 1e-30)),

            // In decibels, which is what somebody dividing two traces usually wanted next.
            ["db"] = x => 20.0 * Math.Log10(Math.Max(Math.Abs(x), 1e-30)),

            ["sin"] = Math.Sin,
            ["cos"] = Math.Cos,
            ["tan"] = Math.Tan,
            ["sign"] = x => Math.Sign(x),

            // The soft limit. Everything that saturates gently — an amplifier running out of
            // headroom, a magnetic core, a compressor — is some scaling of this, and writing one
            // without it means faking the knee with arithmetic that has a corner in it, which is
            // exactly the shape a solver has most trouble with.
            ["tanh"] = Math.Tanh,
            ["sinh"] = Math.Sinh,
            ["cosh"] = Math.Cosh,
        };

    /// <summary>
    /// What an unresolvable name should be called when this is used for something other than
    /// traces. The language is shared; the vocabulary in the error message should not be, because
    /// "there is no trace called Rf" is a confusing thing to be told about a circuit parameter.
    /// </summary>
    public const string DefaultNoun = "trace";

    private static INode Parse(
        string? expression, IReadOnlyList<string> names, List<string>? found = null,
        string noun = DefaultNoun)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ExpressionException("There is no expression to work out.");

        var parser = new Parser(expression, names, found, noun);
        var node = parser.ParseExpression();

        parser.ExpectEnd();

        return node;
    }

    /// <summary>
    /// Recursive descent, precedence climbing. Small enough to read in one sitting, which is worth
    /// more here than generality: the whole grammar is numbers, names, five operators and a
    /// handful of functions.
    /// </summary>
    private sealed class Parser(string text, IReadOnlyList<string> names, List<string>? found, string noun)
    {
        private int _at;

        public INode ParseExpression() => ParseAdditive();

        public void ExpectEnd()
        {
            SkipSpace();

            if (_at < text.Length)
                throw new ExpressionException($"Did not expect '{text[_at]}' at position {_at + 1}.");
        }

        private INode ParseAdditive()
        {
            var left = ParseMultiplicative();

            while (true)
            {
                SkipSpace();

                if (Take('+')) left = new Binary(left, ParseMultiplicative(), (a, b) => a + b);
                else if (Take('-')) left = new Binary(left, ParseMultiplicative(), (a, b) => a - b);
                else return left;
            }
        }

        private INode ParseMultiplicative()
        {
            var left = ParsePower();

            while (true)
            {
                SkipSpace();

                if (Take('*')) left = new Binary(left, ParsePower(), (a, b) => a * b);

                // Division that survives a zero. A ratio of two traces is the commonest expression
                // there is, and the denominator passing through zero is ordinary — a gain plot
                // with a hole in it is more useful than one that is all infinity.
                else if (Take('/')) left = new Binary(left, ParsePower(), Divide);
                else return left;
            }
        }

        private INode ParsePower()
        {
            var left = ParseUnary();

            SkipSpace();

            // Right associative, as powers are: 2^3^2 is 2^9.
            return Take('^') ? new Binary(left, ParsePower(), Math.Pow) : left;
        }

        private INode ParseUnary()
        {
            SkipSpace();

            if (Take('-')) return new Unary(ParseUnary(), x => -x);
            if (Take('+')) return ParseUnary();

            return ParsePrimary();
        }

        private INode ParsePrimary()
        {
            SkipSpace();

            if (_at >= text.Length)
                throw new ExpressionException("The expression stops before it is finished.");

            if (Take('('))
            {
                var inner = ParseExpression();

                SkipSpace();

                if (!Take(')')) throw new ExpressionException("A '(' was never closed.");

                return inner;
            }

            // Braces name a trace whose label has spaces or punctuation in it, which most do.
            if (Take('{'))
            {
                var close = text.IndexOf('}', _at);

                if (close < 0) throw new ExpressionException("A '{' was never closed.");

                var name = text[_at..close].Trim();
                _at = close + 1;

                return Resolve(name);
            }

            if (char.IsDigit(text[_at]) || text[_at] == '.') return ParseNumber();

            if (char.IsLetter(text[_at]) || text[_at] == '_') return ParseWord();

            throw new ExpressionException($"'{text[_at]}' is not something an expression can use.");
        }

        private INode ParseNumber()
        {
            var start = _at;

            while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.')) _at++;

            // An exponent, so 1e-6 is a number rather than a number, a name and a number.
            if (_at < text.Length && (text[_at] == 'e' || text[_at] == 'E'))
            {
                var mark = _at;
                _at++;

                if (_at < text.Length && (text[_at] == '+' || text[_at] == '-')) _at++;

                if (_at < text.Length && char.IsDigit(text[_at]))
                {
                    while (_at < text.Length && char.IsDigit(text[_at])) _at++;
                }
                else
                {
                    _at = mark;
                }
            }

            var span = text[start.._at];

            return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? new Constant(value)
                : throw new ExpressionException($"'{span}' is not a number.");
        }

        private INode ParseWord()
        {
            var start = _at;

            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_')) _at++;

            var word = text[start.._at];

            SkipSpace();

            if (Take('('))
            {
                if (!Functions.TryGetValue(word, out var function))
                {
                    throw new ExpressionException(
                        $"There is no function called '{word}'. Try: " +
                        $"{string.Join(", ", Functions.Keys.Order(StringComparer.Ordinal))}.");
                }

                var argument = ParseExpression();

                SkipSpace();

                if (!Take(')')) throw new ExpressionException($"'{word}(' was never closed.");

                return new Unary(argument, function);
            }

            return Resolve(word);
        }

        private INode Resolve(string name)
        {
            var match = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                throw new ExpressionException(names.Count == 0
                    ? $"There is no {noun} called '{name}' — there are no {noun}s at all."
                    : $"There is no {noun} called '{name}'. There is: {string.Join(", ", names)}.");
            }

            found?.Add(match);

            return new Reference(match);
        }

        private static double Divide(double a, double b) =>
            Math.Abs(b) < 1e-30 ? double.NaN : a / b;

        private bool Take(char c)
        {
            SkipSpace();

            if (_at >= text.Length || text[_at] != c) return false;

            _at++;
            return true;
        }

        private void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++;
        }
    }
}
