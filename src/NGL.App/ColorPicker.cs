using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.App;

/// <summary>색과 불투명도를 고르는 작은 창.</summary>
public static class ColorPicker
{
    private const double SquareWidth = 260, SquareHeight = 160;

    public static Brush BrushOf(SKColor color) => new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

    /// <summary>불투명하면 #RRGGBB, 아니면 #AARRGGBB.</summary>
    public static string HexOf(SKColor color) => color.Alpha == 255
        ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
        : $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    /// <summary>current(#RRGGBB 또는 #AARRGGBB, 비어 있어도 됨)에서 시작해 색을 고르게 한다. 취소하면 null.</summary>
    public static string? Pick(Window owner, string current)
    {
        var start = TextRenderer.ColorOf(current) ?? SKColors.White;
        start.ToHsv(out var hue, out var saturation, out var value);
        var alpha = start.Alpha;

        // 색 고르는 사각형: 왼쪽→오른쪽으로 진해지고(채도), 위→아래로 어두워진다(밝기).
        var hueLayer = new Border();
        var darkLayer = new Border { Background = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) };
        var marker = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        var square = new Grid
        {
            Width = SquareWidth, Height = SquareHeight, ClipToBounds = true, Cursor = Cursors.Cross,
            Children = { hueLayer, darkLayer, new Canvas { Children = { marker } } },
        };

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        Color[] stops = [Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red];
        for (var i = 0; i < stops.Length; i++) rainbow.GradientStops.Add(new GradientStop(stops[i], i / 6.0));
        var hueBar = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = rainbow, Margin = new Thickness(0, 14, 0, 2) };
        var hueSlider = new Slider { Minimum = 0, Maximum = 360, Value = hue };
        var alphaLabel = new TextBlock { Foreground = DialogParts.Brush("Muted"), FontSize = 13, Margin = new Thickness(0, 12, 0, 2) };
        var alphaSlider = new Slider { Minimum = 0, Maximum = 100, Value = alpha / 2.55 };

        var preview = new Border { Width = 56, Height = 32, CornerRadius = new CornerRadius(4), BorderBrush = DialogParts.Brush("InputLine"), BorderThickness = new Thickness(1) };
        var hex = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Consolas") };
        var result = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { preview, hex } };

        SKColor Chosen() => SKColor.FromHsv(hue, saturation, value, alpha);

        void Show()
        {
            var pure = SKColor.FromHsv(hue, 100, 100);
            hueLayer.Background = new LinearGradientBrush(Colors.White, Color.FromRgb(pure.Red, pure.Green, pure.Blue), 0);
            Canvas.SetLeft(marker, saturation / 100 * SquareWidth - 6);
            Canvas.SetTop(marker, (1 - value / 100) * SquareHeight - 6);
            alphaLabel.Text = $"불투명도 {Math.Round(alpha / 2.55)}%";
            preview.Background = BrushOf(Chosen());
            hex.Text = HexOf(Chosen());
        }

        void MoveTo(Point point)
        {
            saturation = (float)(Math.Clamp(point.X / SquareWidth, 0, 1) * 100);
            value = (float)((1 - Math.Clamp(point.Y / SquareHeight, 0, 1)) * 100);
            Show();
        }

        square.MouseLeftButtonDown += (_, e) =>
        {
            square.CaptureMouse();
            MoveTo(e.GetPosition(square));
        };
        square.MouseMove += (_, e) =>
        {
            if (square.IsMouseCaptured) MoveTo(e.GetPosition(square));
        };
        square.MouseLeftButtonUp += (_, _) => square.ReleaseMouseCapture();
        hueSlider.ValueChanged += (_, e) =>
        {
            hue = (float)e.NewValue;
            Show();
        };
        alphaSlider.ValueChanged += (_, e) =>
        {
            alpha = (byte)Math.Round(e.NewValue * 2.55);
            Show();
        };

        var ok = DialogParts.Button("확인", primary: true);
        ok.IsDefault = true;
        var cancel = DialogParts.Button("취소");
        cancel.IsCancel = true;

        var window = new Window
        {
            Title = "색 고르기",
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Content = DialogParts.Layout(new StackPanel { Children = { square, hueBar, hueSlider, alphaLabel, alphaSlider, result } }, cancel, ok),
        };
        DialogParts.Apply(window);
        ok.Click += (_, _) => window.DialogResult = true;
        Show();

        return window.ShowDialog() == true ? HexOf(Chosen()) : null;
    }
}
