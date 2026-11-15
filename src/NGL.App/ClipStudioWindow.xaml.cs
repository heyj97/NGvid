using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace NGL.App;

/// <summary>
/// 선택한 영상·이미지 클립의 전환·애니메이션, 효과·색보정, 크로마키·마스크를 고치는 창.
/// 편집 창과 함께 띄워 두고 쓰며, 타임라인에서 다른 클립을 고르면 그 클립의 값을 보여준다.
/// </summary>
public partial class ClipStudioWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ClipStudioWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    /// <summary>위의 탭에서 고른 쪽만 보여준다.</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages is null) return; // 창을 만드는 중
        for (var i = 0; i < Pages.Children.Count; i++)
            Pages.Children[i].Visibility = i == Tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>값 칸에서 Enter나 Esc를 누르면 칸을 떠나 값을 확정한다.</summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnImportLutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "LUT 불러오기", Filter = "LUT 파일|*.cube|모든 파일|*.*" };
        if (dialog.ShowDialog(this) == true) _viewModel.ImportLut(dialog.FileName);
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (PromptWindow.Ask(this, "프리셋 이름", "내 움직임") is { } name) _viewModel.SavePreset(name);
    }

    /// <summary>색 칸 옆의 네모를 누르면 색을 고르게 하고, 고른 색을 그 칸에 적어 적용한다.</summary>
    private void OnPickColorClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TextBox box || ColorPicker.Pick(this, box.Text) is not { } color) return;
        box.Text = color;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
