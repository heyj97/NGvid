using System.IO;
using System.Windows;
using Microsoft.Win32;
using NGL.Core;

namespace NGL.App;

/// <summary>새 프로젝트의 이름·저장 위치·비율·프레임레이트를 묻는다.</summary>
public partial class NewProjectWindow : Window
{
    private readonly Func<string, string, SequencePreset, int, string?> _create;

    /// <param name="create">(폴더, 이름, 비율, fps)로 프로젝트를 만들고, 실패하면 이유를 돌려준다.</param>
    public NewProjectWindow(Func<string, string, SequencePreset, int, string?> create)
    {
        InitializeComponent();
        _create = create;

        NameBox.Text = "내 프로젝트";
        LocationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NGvid");
        PresetBox.ItemsSource = SequencePreset.All;
        PresetBox.SelectedIndex = 0;
        FrameRateBox.ItemsSource = Sequence.SupportedFrameRates.Select(rate => $"{rate}fps").ToList();
        FrameRateBox.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "저장 위치", InitialDirectory = LocationBox.Text };
        if (dialog.ShowDialog(this) == true) LocationBox.Text = dialog.FolderName;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorText.Text = "프로젝트 이름에 쓸 수 없는 글자가 있거나 비어 있습니다.";
            return;
        }

        if (!Path.IsPathFullyQualified(LocationBox.Text.Trim()))
        {
            ErrorText.Text = "저장 위치를 전체 경로로 적어 주세요.";
            return;
        }

        var error = _create(Path.Combine(LocationBox.Text.Trim(), name), name,
            SequencePreset.All[PresetBox.SelectedIndex], Sequence.SupportedFrameRates[FrameRateBox.SelectedIndex]);
        if (error is null) DialogResult = true;
        else ErrorText.Text = error;
    }
}
