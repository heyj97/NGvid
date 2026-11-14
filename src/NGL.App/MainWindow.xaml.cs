using System.Windows;
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
        _viewModel.RenderRequested += Preview.InvalidateVisual;
        Closed += (_, _) => _viewModel.Dispose();

        // 실행할 때 파일 경로를 주면 바로 연다. (탐색기에서 실행 파일에 끌어 놓기)
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1) _viewModel.Open(args[1]);
    }

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
