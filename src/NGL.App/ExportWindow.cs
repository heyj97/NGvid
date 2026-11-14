using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NGL.Core;
using NGL.Export;

namespace NGL.App;

/// <summary>내보내기 창: 파일 위치와 품질을 고르고, 진행률을 보며 취소할 수 있다.</summary>
public sealed class ExportWindow : Window
{
    private static Brush Muted => DialogParts.Brush("Muted");

    private readonly Project _project;
    private readonly string _projectFolder;
    private readonly IExporter _exporter = new Mp4Exporter();

    private readonly TextBox _path = new() { Height = 40, Padding = new Thickness(12, 0, 12, 0) };
    private readonly ComboBox _quality = new() { Height = 40, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0), ItemsSource = new[] { "낮음", "보통", "높음" }, SelectedIndex = 1 };
    private readonly TextBlock _estimate = new() { Foreground = Muted, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 16, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _browse = DialogParts.Button("찾아보기");
    private readonly Button _start = DialogParts.Button("내보내기 시작", primary: true);
    private readonly Button _cancel = DialogParts.Button("닫기");
    private readonly Button _openFolder = DialogParts.Button("폴더 열기");

    private CancellationTokenSource? _running;

    public ExportWindow(Project project, string projectFolder)
    {
        _project = project;
        _projectFolder = projectFolder;
        var sequence = project.Sequence;

        Title = "내보내기";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        DialogParts.Apply(this);

        _path.Text = Path.Combine(projectFolder, project.Name + ".mp4");
        _openFolder.Visibility = Visibility.Collapsed;

        // 자동 시험이 찾을 수 있도록 이름을 붙인다.
        AutomationProperties.SetAutomationId(_path, "ExportPathBox");
        AutomationProperties.SetAutomationId(_quality, "ExportQualityBox");
        AutomationProperties.SetAutomationId(_start, "ExportStartButton");
        AutomationProperties.SetAutomationId(_cancel, "ExportCancelButton");
        AutomationProperties.SetAutomationId(_status, "ExportStatusLabel");

        var pathRow = new DockPanel();
        DockPanel.SetDock(_browse, Dock.Right);
        _browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(_browse);
        pathRow.Children.Add(_path);


        Content = DialogParts.Layout(new StackPanel
        {
            Children =
            {
                Label("파일"),
                pathRow,
                Label($"규격  ·  {sequence.Preset.Width}×{sequence.Preset.Height}, {sequence.FrameRate}fps, H.264 (시퀀스 설정을 따릅니다)", 20),
                Label("품질", 20),
                _quality,
                _estimate,
                _progress,
                _status,
            },
        }, _openFolder, _cancel, _start);

        _browse.Click += (_, _) => Browse();
        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) =>
        {
            if (_running is null) Close();
            else _running.Cancel();
        };
        _openFolder.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{_path.Text}\"");
        _quality.SelectionChanged += (_, _) => ShowEstimate();
        Closing += (_, e) =>
        {
            // 내보내는 중에 창을 닫으면 먼저 취소한다.
            if (_running is null) return;
            _running.Cancel();
            e.Cancel = true;
        };
        ShowEstimate();
    }

    private ExportQuality Quality => (ExportQuality)_quality.SelectedIndex;

    /// <summary>비트레이트 × 길이로 어림한 파일 크기.</summary>
    private void ShowEstimate()
    {
        var bitrate = ExportSettings.BitrateFor(Quality, _project.Sequence);
        var megabytes = (bitrate + 192_000) / 8.0 * _project.Sequence.Duration.TotalSeconds / 1_000_000;
        _estimate.Text = $"영상 {bitrate / 1_000_000.0:0.#} Mbps  ·  예상 크기 약 {megabytes:0.#} MB";
    }

    private void Browse()
    {
        var dialog = new SaveFileDialog
        {
            Title = "내보낼 파일",
            Filter = "MP4 영상|*.mp4",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text),
        };
        if (dialog.ShowDialog(this) == true) _path.Text = dialog.FileName;
    }

    private async Task RunAsync()
    {
        var path = _path.Text.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            _status.Text = "파일 위치를 전체 경로로 적어 주세요.";
            return;
        }

        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) path += ".mp4";
        _path.Text = path;

        var settings = new ExportSettings(path, ExportSettings.BitrateFor(Quality, _project.Sequence));
        var timer = Stopwatch.StartNew();
        // Progress는 알림을 이 창의 스레드로 넘겨 준다.
        var progress = new Progress<double>(value =>
        {
            _progress.Value = value;
            var remaining = value > 0.02 ? TimeSpan.FromSeconds(timer.Elapsed.TotalSeconds * (1 - value) / value) : (TimeSpan?)null;
            _status.Text = remaining is { } left
                ? $"내보내는 중… {value:P0}  ·  남은 시간 약 {(int)left.TotalMinutes}분 {left.Seconds}초"
                : $"내보내는 중… {value:P0}";
        });

        SetRunning(new CancellationTokenSource());
        try
        {
            var token = _running!.Token;
            await Task.Run(() => _exporter.Export(_project, _projectFolder, settings, progress, token));
            _progress.Value = 1;
            _status.Text = $"완료했습니다. ({timer.Elapsed.TotalSeconds:0.#}초)";
            _openFolder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            _progress.Value = 0;
            _status.Text = "취소했습니다.";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or DllNotFoundException)
        {
            _progress.Value = 0;
            _status.Text = "내보내지 못했습니다: " + e.Message;
        }
        finally
        {
            SetRunning(null);
        }
    }

    private void SetRunning(CancellationTokenSource? running)
    {
        _running?.Dispose();
        _running = running;
        var idle = running is null;
        _start.IsEnabled = _path.IsEnabled = _quality.IsEnabled = _browse.IsEnabled = idle;
        _cancel.Content = idle ? "닫기" : "취소";
        if (!idle) _openFolder.Visibility = Visibility.Collapsed;
    }

    private static TextBlock Label(string text, double top = 0) =>
        new() { Text = text, Foreground = Muted, FontSize = 13, Margin = new Thickness(0, top, 0, 8), TextWrapping = TextWrapping.Wrap };

}
