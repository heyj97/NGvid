using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NGL.App;

/// <summary>대화상자들이 함께 쓰는 모양: 어두운 바탕, 여백 20의 본문, 위에 선이 있는 아래쪽 버튼 줄.</summary>
public static class DialogParts
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>아래쪽 버튼. primary는 그 창의 가장 중요한 동작 하나에만 쓴다.</summary>
    public static Button Button(string text, bool primary = false) => new()
    {
        Content = text,
        Style = (Style)Application.Current.FindResource(primary ? "DialogPrimary" : "Dialog"),
        Margin = new Thickness(8, 0, 0, 0),
    };

    /// <summary>창의 바탕·글자 색·크기를 맞춘다.</summary>
    public static void Apply(Window window)
    {
        window.Background = Brush("Panel");
        window.Foreground = Brush("Text");
        window.FontSize = 14;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>본문과 아래쪽 버튼 줄(오른쪽 정렬)을 합친다.</summary>
    public static UIElement Layout(UIElement content, params UIElement[] footer)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var item in footer) buttons.Children.Add(item);

        var bar = new Border { BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 16, 20, 16), Child = buttons };
        DockPanel.SetDock(bar, Dock.Bottom);
        return new DockPanel { Children = { bar, new Border { Padding = new Thickness(20), Child = content } } };
    }
}
