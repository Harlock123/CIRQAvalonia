using Cirq.Cli;

// Everything is in CommandLine so it can be driven by a test with a StringWriter rather than by a
// process: a command-line tool whose behaviour can only be checked by running it is a command-line
// tool nobody checks.
return CommandLine.Run(args, Console.Out, Console.Error);
