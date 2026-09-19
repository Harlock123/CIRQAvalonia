using System.Globalization;
using Avalonia.Data.Converters;

namespace Cirq.UI.ViewModels;

/// <summary>Picks one of two strings from a boolean, used for the Run/Pause button caption.</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "True";

    public string FalseText { get; set; } = "False";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueText : FalseText;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
