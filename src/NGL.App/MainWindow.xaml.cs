using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NGL.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace NGL.App;

public partial class MainWindow : Window
{
    private const float PreviewMargin = 16;
    private static readonly SKColor Backdrop = new(0x10, 0x11, 0x14);

    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // 새 프레임은 디코딩 스레드에서 준비되므로 화면 스레드로 넘겨서 다시 그린다.
        _viewModel.RenderRequested += () => Dispatcher.BeginInvoke(Preview.InvalidateVisual);
        // 화면을 그릴 때마다 재생 위치 표시(슬라이더·타임코드)를 갱신한다.
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _viewModel.Dispose();
        };

        // 실행할 때 파일 경로를 주면 바로 연다. (탐색기에서 실행 파일에 끌어 놓기)
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1) _viewModel.Open(args[1]);
    }

    private void OnRendering(object? sender, EventArgs e) => _viewModel.Tick();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "영상 열기",
            Filter = "영상 파일|*.mp4;*.mov;*.webm;*.mkv;*.avi;*.m4v|모든 파일|*.*",
        };
        if (dialog.ShowDialog(this) == true) _viewModel.Open(dialog.FileName);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _viewModel.Open(files[0]);
    }

    /// <summary>시퀀스 비율을 유지한 채 미리보기 영역 가운데에 맞춰 그린다.</summary>
    private void OnPaintSurface(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(Backdrop);
        if (_viewModel.Project is not { } project) return;

        var area = SKRect.Create(e.BackendRenderTarget.Width, e.BackendRenderTarget.Height);
        area.Inflate(-PreviewMargin, -PreviewMargin);
        if (area.Width <= 0 || area.Height <= 0) return;

        var preset = project.Sequence.Preset;
        var target = SkiaCompositor.Fit(preset.Width, preset.Height, area);

        canvas.Save();
        canvas.Translate(target.Left, target.Top);
        canvas.Scale(target.Width / preset.Width);
        _viewModel.Render(canvas);
        canvas.Restore();
    }
}
