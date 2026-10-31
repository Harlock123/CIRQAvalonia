using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cirq.Mcp;

internal static class Json
{
    /// <summary>
    /// How answers are written: plainly, so an agent reads "10kΩ", "2.2µF" and "PULSE(0 4e+00 …)" rather than their
    /// \u escapes. Nothing here is put into a web page.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
