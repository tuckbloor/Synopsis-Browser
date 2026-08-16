using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.App.Converters;

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiagnosticSeverity.Critical => new SolidColorBrush(Color.FromRgb(255, 83, 112)),
        DiagnosticSeverity.Error => new SolidColorBrush(Color.FromRgb(255, 108, 124)),
        DiagnosticSeverity.Warning => new SolidColorBrush(Color.FromRgb(255, 201, 92)),
        _ => new SolidColorBrush(Color.FromRgb(105, 210, 231))
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DiagnosticSeverity.Critical => "!!",
        DiagnosticSeverity.Error => "X",
        DiagnosticSeverity.Warning => "!",
        _ => "i"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
