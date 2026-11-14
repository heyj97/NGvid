using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NGL.App;

/// <summary>글 한 줄을 묻는 작은 창. 확인하면 입력한 글을, 취소하면 null을 돌려준다.</summary>
public static class PromptWindow
{
    public static string? Ask(Window owner, string title, string value)
    {
        var box = new TextBox { Text = value, Padding = new Thickness(8, 6, 8, 6), MinWidth = 280 };
        var ok = new Button { Content = "확인", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, ok },
        };

        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1C)),
            Content = new StackPanel { Margin = new Thickness(20), Children = { box, buttons } },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text : null;
    }
}
