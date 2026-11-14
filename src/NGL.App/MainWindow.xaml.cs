using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NGL.Core;
using NGL.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace NGL.App;

public partial class MainWindow : Window
{
    private const float PreviewMargin = 16;
    private const string MediaFilter =
        "미디어 파일|*.mp4;*.mov;*.webm;*.mkv;*.avi;*.m4v;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.png;*.jpg;*.jpeg;*.webp;*.bmp|모든 파일|*.*";

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
        Timeline.MarkerRenameRequested += OnMarkerRename;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _viewModel.Dispose();
        };

        // 실행할 때 프로젝트 폴더나 .ngvid 파일 경로를 주면 바로 연다.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1) _viewModel.OpenProject(args[1]);
    }

    private bool _updatingScroll;

    private void OnRendering(object? sender, EventArgs e)
    {
        _viewModel.Tick();
        Timeline.Refresh();

        // 가로 스크롤 막대를 타임라인의 보이는 구간에 맞춘다. 끝에 화면 절반만큼 여유를 둔다.
        var viewport = Timeline.ViewportSeconds;
        var duration = _viewModel.Project?.Sequence.Duration.TotalSeconds ?? 0;
        _updatingScroll = true;
        TimelineScroll.Maximum = Math.Max(_viewModel.TimelineOffset, duration - viewport / 2);
        TimelineScroll.ViewportSize = viewport;
        TimelineScroll.LargeChange = viewport;
        TimelineScroll.SmallChange = viewport / 10;
        TimelineScroll.Value = _viewModel.TimelineOffset;
        _updatingScroll = false;
    }

    private void OnTimelineScroll(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingScroll) _viewModel.TimelineOffset = e.NewValue;
    }

    // ───── 타임라인 ─────

    private void OnAddTrackClick(object sender, RoutedEventArgs e)
    {
        var menu = ((Button)sender).ContextMenu!;
        menu.DataContext = _viewModel;
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private static TrackItem? TrackOf(object sender) => ((FrameworkElement)sender).DataContext as TrackItem;

    private void OnTrackUpClick(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is { } item) _viewModel.MoveTrack(item.Track, -1);
    }

    private void OnTrackDownClick(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is { } item) _viewModel.MoveTrack(item.Track, 1);
    }

    private void OnTrackColorClick(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is { } item && e.OriginalSource is MenuItem { DataContext: string color }) item.SetColor(color);
    }

    /// <summary>클립이 있는 트랙은 되돌릴 수 없으므로 먼저 묻는다.</summary>
    private void OnTrackDeleteClick(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } item) return;
        if (item.Track.Clips.Count > 0 && MessageBox.Show(this,
                $"'{item.Name}' 트랙과 그 안의 클립 {item.Track.Clips.Count}개를 삭제할까요?", "트랙 삭제",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _viewModel.DeleteTrack(item.Track);
    }

    private void OnMarkerRename(Marker marker)
    {
        if (PromptWindow.Ask(this, "마커 이름", marker.Name) is { } name) _viewModel.RenameMarker(marker, name);
    }

    /// <summary>보관함의 썸네일을 끌기 시작하면 타임라인에 놓을 수 있다.</summary>
    private void OnAssetMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && ((FrameworkElement)sender).DataContext is AssetItem item)
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AssetItem), item), DragDropEffects.Copy);
    }

    // ───── 메뉴 ─────

    private void OnNewClick(object sender, RoutedEventArgs e) =>
        new NewProjectWindow(_viewModel.CreateProject) { Owner = this }.ShowDialog();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "프로젝트 열기", Filter = "NGvid 프로젝트|*.ngvid" };
        if (dialog.ShowDialog(this) == true) _viewModel.OpenProject(dialog.FileName);
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "미디어 가져오기", Filter = MediaFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) _viewModel.Import(dialog.FileNames);
    }

    /// <summary>파일을 끌어 놓으면: 프로젝트 파일은 열고, 그 밖의 파일은 보관함으로 가져온다.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;

        if (files[0].EndsWith(".ngvid", StringComparison.OrdinalIgnoreCase)) _viewModel.OpenProject(files[0]);
        else _viewModel.Import(files);
    }

    // ───── 보관함 ─────

    private void OnTabChecked(object sender, RoutedEventArgs e) =>
        _viewModel.LibraryTab = int.Parse((string)((RadioButton)sender).Tag); // XAML에서 Tag를 IsChecked보다 먼저 적어야 한다

    private void OnAddToTimelineClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AssetItem item) _viewModel.AddToTimelineCommand.Execute(item);
    }

    private void OnRelinkClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AssetItem item) return;
        var dialog = new OpenFileDialog { Title = $"'{item.Name}' 다시 연결", Filter = MediaFilter };
        if (dialog.ShowDialog(this) == true) _viewModel.Relink(item, dialog.FileName);
    }

    private void OnRelinkFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "사라진 원본이 있는 폴더" };
        if (dialog.ShowDialog(this) == true) _viewModel.RelinkFromFolder(dialog.FolderName);
    }

    /// <summary>이름 입력 중 Enter를 누르면 확정한다.</summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ───── 단축키 ─────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        var commands = _viewModel;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: OnNewClick(this, e); break;
                case Key.O: OnOpenClick(this, e); break;
                case Key.S: commands.SaveCommand.Execute(null); break;
                case Key.I when commands.HasProject: OnImportClick(this, e); break;
                // 글자를 입력하는 중에는 Ctrl+Z·C·V·A가 입력 칸의 것이다.
                case Key.Z when !typing: commands.UndoCommand.Execute(null); break;
                case Key.Y when !typing: commands.RedoCommand.Execute(null); break;
                case Key.K when !typing: commands.SplitCommand.Execute(null); break;
                case Key.C when !typing: commands.CopyCommand.Execute(null); break;
                case Key.V when !typing: commands.PasteCommand.Execute(null); break;
                case Key.A when !typing: commands.SelectAllCommand.Execute(null); break;
                case Key.G when !typing: commands.GroupCommand.Execute(null); break;
                default: return;
            }

            e.Handled = true;
            return;
        }

        if (typing) return; // 글자를 입력하는 중에는 아래 단축키를 쓰지 않는다.

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.Z: commands.RedoCommand.Execute(null); break;
                case Key.G: commands.UngroupCommand.Execute(null); break;
                default: return;
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.Delete)
        {
            commands.RippleDeleteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Space: _viewModel.PlayPauseCommand.Execute(null); break;
            case Key.Left: _viewModel.StepCommand.Execute("-1"); break;
            case Key.Right: _viewModel.StepCommand.Execute("1"); break;
            case Key.I: _viewModel.SetLoopStartCommand.Execute(null); break;
            case Key.O: _viewModel.SetLoopEndCommand.Execute(null); break;
            case Key.L: _viewModel.ToggleLoopCommand.Execute(null); break;
            case Key.M: _viewModel.AddMarkerCommand.Execute(null); break;
            case Key.S: _viewModel.SplitCommand.Execute(null); break;
            case Key.Delete: _viewModel.DeleteCommand.Execute(null); break;
            default: return;
        }

        e.Handled = true;
    }

    // ───── 미리보기 ─────

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
