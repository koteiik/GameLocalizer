using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
namespace GameLocalizer.UI.Views;
public sealed class LibraryStatusBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value?.ToString()?.ToLowerInvariant() ?? "";
        var color = status.Contains("ошиб") || status.Contains("failed") ? "#D85C67" : status.Contains("измен") || status.Contains("новые") || status.Contains("обнов") ? "#E6A64C" : status.Contains("перевед") || status.Contains("примен") || status.Contains("актуален") ? "#4FCB8D" : "#7587C9";
        return (Brush)new BrushConverter().ConvertFromString(color)!;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
