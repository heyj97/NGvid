using System.Windows;
using System.Windows.Controls;

namespace NGL.App;

/// <summary>글 한 줄을 묻는 작은 창. 확인하면 입력한 글을, 취소하면 null을 돌려준다.</summary>
public static class PromptWindow
{
    public static string? Ask(Window owner, string title, string value)
    {
        var box = new TextBox { Text = value, Height = 40, MinWidth = 320 };
        var ok = DialogParts.Button("확인", primary: true);
        ok.IsDefault = true;
        var cancel = DialogParts.Button("취소");
        cancel.IsCancel = true;

        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Content = DialogParts.Layout(box, cancel, ok),
        };
        DialogParts.Apply(window);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text : null;
    }
}
