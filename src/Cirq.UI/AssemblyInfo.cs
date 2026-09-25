using System.Runtime.CompilerServices;

// The dialogs built in code are constructed by one internal builder and shown by another method.
// A test cannot await a modal window — the call does not return until the window is gone, which is
// the thing being tested — so it builds one and looks at it instead. Friend access keeps the
// builder off the public surface while letting the suite reach the two dialogs that have between
// them accounted for both of the UI bugs that have ever shipped.
[assembly: InternalsVisibleTo("Cirq.UI.Tests")]
