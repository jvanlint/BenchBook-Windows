using System.Numerics;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BenchBook.App.Views;

/// <summary>
/// Paint swatches: a filled circle, or a dashed outline when there's no colour.
/// Clears are drawn as glass (behaviour.md, "Swatches"): the colour at 55%
/// over a faint checkerboard, with a soft white highlight across the top. A
/// clear with no colour is clear (white) glass.
/// </summary>
internal static class PaintSwatch
{
    public static FrameworkElement Make(Paint? paint, double size = 16) =>
        Make(paint?.SwatchHex, size, paint is not null && PaintClarity.IsClear(paint.Name));

    public static FrameworkElement Make(string? hex, double size = 16, bool isClear = false)
    {
        if (isClear) return Glass(hex, size);
        var circle = new Ellipse { Width = size, Height = size, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
        if (Swatch.Components(hex) is var (r, g, b))
        {
            circle.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            circle.Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        }
        else
        {
            circle.Stroke = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            circle.StrokeDashArray = [2, 2];
        }
        return circle;
    }

    private static Grid Glass(string? hex, double size)
    {
        var tint = Swatch.Components(hex) is var (r, g, b) ? Windows.UI.Color.FromArgb(255, r, g, b) : Microsoft.UI.Colors.White;
        var glass = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };

        // A faint checkerboard, four squares a side.
        var board = new Grid();
        const int squares = 4;
        for (var i = 0; i < squares; i++)
        {
            board.RowDefinitions.Add(new RowDefinition());
            board.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var row = 0; row < squares; row++)
            for (var column = 0; column < squares; column++)
            {
                var square = new Rectangle
                {
                    Fill = new SolidColorBrush((row + column) % 2 == 0
                        ? Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF2)
                        : Windows.UI.Color.FromArgb(255, 0xC8, 0xC8, 0xC8)),
                };
                Grid.SetRow(square, row);
                Grid.SetColumn(square, column);
                board.Children.Add(square);
            }
        glass.Children.Add(board);
        glass.Children.Add(new Ellipse { Fill = new SolidColorBrush(tint) { Opacity = 0.55 } });

        // The highlight: a soft white band over the top half.
        var highlight = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
        highlight.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(217, 255, 255, 255), Offset = 0 });
        highlight.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = 1 });
        glass.Children.Add(new Ellipse
        {
            Fill = highlight, Width = size * 0.7, Height = size * 0.45,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, size * 0.05, 0, 0),
        });
        glass.Children.Add(new Ellipse { Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], StrokeThickness = 1 });

        // Clipped to a circle. A XAML clip can only be a rectangle, so the clip is the visual's.
        var visual = ElementCompositionPreview.GetElementVisual(glass);
        var circle = visual.Compositor.CreateEllipseGeometry();
        circle.Center = new Vector2((float)size / 2);
        circle.Radius = new Vector2((float)size / 2);
        visual.Clip = visual.Compositor.CreateGeometricClip(circle);
        return glass;
    }

    public static string Hex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
