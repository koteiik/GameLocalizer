using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GameLocalizer.UI.ViewModels;
namespace GameLocalizer.UI.Views;

// Pure presentation converters: no file discovery, game mutations or backend queries.
public sealed class GameArtworkConverter : IValueConverter
{
    private static readonly Brush[] Artwork = BuildArtwork();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        uint hash = 2166136261;
        foreach(var c in value?.ToString() ?? "GameLocalizer") hash = (hash ^ c) * 16777619;
        return Artwork[hash % Artwork.Length];
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    private static Brush[] BuildArtwork()
    {
        var palettes = new[] { new[] { "#27315C", "#6578AA", "#17213A" }, new[] { "#213B47", "#55969C", "#142630" }, new[] { "#432943", "#9B698E", "#28192F" }, new[] { "#46392B", "#A38A62", "#282529" } };
        return palettes.Select(p =>
        {
            var drawing = new DrawingGroup();
            var sky = new LinearGradientBrush(Color(p[0]), Color(p[2]), 70);
            drawing.Children.Add(new GeometryDrawing(sky, null, new RectangleGeometry(new Rect(0,0,400,220))));
            var glow = new RadialGradientBrush(Color(p[1]), Colors.Transparent) { Center = new Point(.66,.25), GradientOrigin = new Point(.66,.25), RadiusX = .72, RadiusY = .85, Opacity = .65 };
            drawing.Children.Add(new GeometryDrawing(glow,null,new RectangleGeometry(new Rect(0,0,400,220))));
            var horizon = new SolidColorBrush(Color(p[1])) { Opacity = .18 };
            drawing.Children.Add(new GeometryDrawing(horizon,null,Geometry.Parse("M0,154 L72,84 133,135 209,68 282,132 346,83 400,131 400,220 0,220 Z")));
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color(p[2])) { Opacity = .7 },null,Geometry.Parse("M0,178 L90,137 176,167 261,122 400,179 400,220 0,220 Z")));
            var line = new SolidColorBrush(Colors.White) { Opacity = .13 };
            drawing.Children.Add(new GeometryDrawing(null,new Pen(line,1),new EllipseGeometry(new Point(291,78),77,77)));
            drawing.Children.Add(new GeometryDrawing(null,new Pen(line,1),new EllipseGeometry(new Point(291,78),56,56)));
            var icon = new SolidColorBrush(Colors.White) { Opacity = .36 };
            drawing.Children.Add(new GeometryDrawing(null,new Pen(icon,3) { StartLineCap=PenLineCap.Round, EndLineCap=PenLineCap.Round, LineJoin=PenLineJoin.Round }, Geometry.Parse("M261,59 C250,60 245,69 242,81 L235,105 C232,121 241,126 251,115 L267,99 H307 L323,115 C333,126 342,121 338,105 L331,81 C328,69 323,60 312,59 Z M261,76 V92 M253,84 H269 M311,77 H312 M319,88 H320")));
            var brush = new DrawingBrush(drawing) { Stretch = Stretch.UniformToFill }; brush.Freeze(); return (Brush)brush;
        }).ToArray();
    }
    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
}
public sealed class PreviewCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if(values[0] is not IEnumerable<TranslationRow> rows) return "—";
        return rows.Count(r => (parameter?.ToString() == "Translated") == !string.IsNullOrWhiteSpace(r.Russian)).ToString("N0",culture);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class WorkflowVisualStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var stage = value?.ToString() ?? "";
        if(stage.StartsWith("⚠",StringComparison.Ordinal) || stage.StartsWith("Ошибка",StringComparison.Ordinal)) return "Warning";
        if(stage.StartsWith("Обработано",StringComparison.Ordinal)) return "Active";
        return stage.StartsWith("✓",StringComparison.Ordinal) ? "Completed" : stage is "Не готово" or "Требует обновления" or "Требует перевода" or "Готово к применению" or "Обновите анализ" ? "Active" : "Pending";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class VersionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value?.ToString() ?? "").Split('·')[0].Trim();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
