using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NGL.Core;
using NGL.Media;

namespace NGL.App;

/// <summary>새 프로젝트의 이름·저장 위치·비율·프레임레이트를 묻는다.</summary>
public partial class NewProjectWindow : Window
{
    private readonly Func<string, string, SequencePreset, int, string?> _create;
    private readonly Action<string>? _importDownloaded;
    private readonly CancellationTokenSource _cancel = new();
    private readonly string _downloadFolder = Path.Combine(Path.GetTempPath(), "NGvid-" + Guid.NewGuid().ToString("N"));
    private string? _downloaded; // 받아 둔 영상. 프로젝트 만들기가 실패해 다시 눌러도 또 받지 않는다.

    /// <param name="create">(폴더, 이름, 비율, fps)로 프로젝트를 만들고, 실패하면 이유를 돌려준다.</param>
    /// <param name="importDownloaded">주면 링크 입력란을 보여주고, 받은 영상의 경로를 프로젝트를 만든 뒤 넘긴다.</param>
    public NewProjectWindow(Func<string, string, SequencePreset, int, string?> create, Action<string>? importDownloaded = null)
    {
        InitializeComponent();
        _create = create;
        _importDownloaded = importDownloaded;
        if (importDownloaded is not null)
        {
            Title = "링크로 프로젝트 시작";
            LinkPanel.Visibility = Visibility.Visible;
            Loaded += (_, _) => LinkBox.Focus();
        }

        Closed += (_, _) =>
        {
            _cancel.Cancel();
            try
            {
                if (Directory.Exists(_downloadFolder)) Directory.Delete(_downloadFolder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 받던 중에 닫으면 파일이 잠겨 있을 수 있다. 임시 폴더라 남아도 괜찮다.
            }
        };

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

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Foreground = ErrorBrush;
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

        if (_importDownloaded is not null && _downloaded is null)
        {
            var link = LinkBox.Text.Trim();
            if (!LinkDownloader.IsLink(link))
            {
                ErrorText.Text = "http:// 또는 https://로 시작하는 영상 링크를 적어 주세요.";
                return;
            }

            CreateButton.IsEnabled = LinkBox.IsEnabled = false;
            ErrorText.Foreground = (Brush)FindResource("Muted");
            DownloadProgress.Value = 0;
            DownloadProgress.Visibility = Visibility.Visible;
            try
            {
                _downloaded = await LinkDownloader.DownloadAsync(link, _downloadFolder, new Progress<string>(text =>
                {
                    ErrorText.Text = text;
                    // 진행 문구 끝의 "12.3%"를 막대에 옮긴다. 퍼센트가 없는 단계는 0으로 돌아간다.
                    var percent = Regex.Match(text, @"([\d.]+)%").Groups[1].Value;
                    DownloadProgress.Value = double.TryParse(percent, CultureInfo.InvariantCulture, out var value) ? value : 0;
                }), _cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return; // 창을 닫았다
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException
                                           or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                LinkBox.IsEnabled = true;
                ErrorText.Foreground = ErrorBrush;
                ErrorText.Text = "영상을 받지 못했습니다: " + ex.Message;
                return;
            }
            finally
            {
                DownloadProgress.Visibility = Visibility.Collapsed;
                CreateButton.IsEnabled = true;
            }

            if (_cancel.IsCancellationRequested) return;
            ErrorText.Foreground = ErrorBrush;
            ErrorText.Text = "";
        }

        var error = _create(Path.Combine(LocationBox.Text.Trim(), name), name,
            SequencePreset.All[PresetBox.SelectedIndex], Sequence.SupportedFrameRates[FrameRateBox.SelectedIndex]);
        if (error is not null)
        {
            ErrorText.Text = error;
            return;
        }

        if (_downloaded is not null) _importDownloaded!(_downloaded);
        DialogResult = true;
    }

    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
}
