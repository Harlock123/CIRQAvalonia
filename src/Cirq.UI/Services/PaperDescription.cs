using System.Globalization;
using Avalonia.Data.Converters;

namespace Cirq.UI.Services;

/// <summary>
/// Shows a paper size by its real dimensions rather than by its enum name, so the picker reads
/// "A4 — 210 × 297 mm" and nobody has to remember which of Legal and Letter is the longer.
/// </summary>
public sealed class PaperDescription : IValueConverter
{
    public static PaperDescription Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PaperSize paper ? PageSetup.Describe(paper) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The picker sets the paper size, not its description.");
}
