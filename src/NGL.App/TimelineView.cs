using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NGL.Core;
using NGL.Media;

namespace NGL.App;

/// <summary>
/// 타임라인의 시간 눈금, 트랙 줄, 클립, 마커, 재생 위치선을 직접 그린다.
/// 트랙 머리(이름·버튼)는 이 컨트롤 왼쪽에 따로 놓인다.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    public const double RulerHeight = 28;
    public const double TrackHeight = 48;

    private const double MinPixelsPerSecond = 0.1; // 1시간이 360픽셀
    private const double MaxPixelsPerSecond = 600; // 60fps에서 한 프레임이 10픽셀
    private const double SnapPixels = 8;

    /// <summary>눈금 간격 후보(초). 프레임 단위 간격은 fps에 따라 앞에 붙는다.</summary>
    private static readonly double[] RulerSteps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    private static readonly Brush Background = Frozen("#17181C");
    private static readonly Brush RulerText = Frozen("#A7ACB4");
    private static readonly Brush ClipText = Frozen("#F2F3F5");
    private static readonly Brush MarkerBrush = Frozen("#F2C14E");
    private static readonly Brush PlayheadBrush = Frozen("#FF8A3D");
    private static readonly Pen LinePen = FrozenPen("#23262C", 1);
    private static readonly Pen TickPen = FrozenPen("#3A3E46", 1);
    private static readonly Pen PlayheadPen = FrozenPen("#FF8A3D", 1.5);
    private static readonly Pen WavePen = FrozenPen("#99FFFFFF", 1);
    private static readonly Typeface TextFace = new("Segoe UI");
    private static readonly Typeface TimeFace = new("Consolas");

    private readonly Dictionary<string, (Brush Fill, Pen Border)> _trackColors = [];
    private (double Position, double Zoom, double Offset, int Version, double Width) _drawn;
    private int _trackCount = -1;

    /// <summary>마커를 두 번 누르거나 메뉴에서 이름 바꾸기를 골랐을 때.</summary>
    public event Action<Marker>? MarkerRenameRequested;

    private MainViewModel? Model => DataContext as MainViewModel;

    public TimelineView()
    {
        AllowDrop = true;
        ClipToBounds = true;
    }

    /// <summary>확대 슬라이더 값(0~1)을 1초당 픽셀 수로 바꾼다. 양 끝의 비율이 일정하도록 지수로 늘어난다.</summary>
    public static double PixelsPerSecond(double zoom) =>
        MinPixelsPerSecond * Math.Pow(MaxPixelsPerSecond / MinPixelsPerSecond, Math.Clamp(zoom, 0, 1));

    private double Scale => PixelsPerSecond(Model?.TimelineZoom ?? 0);

    private double XOf(TimeSpan time) => (time.TotalSeconds - (Model?.TimelineOffset ?? 0)) * Scale;

    private TimeSpan TimeAt(double x) => TimeSpan.FromSeconds(Math.Max(0, x / Scale + (Model?.TimelineOffset ?? 0)));

    private TimeSpan SnapTolerance => TimeSpan.FromSeconds(SnapPixels / Scale);

    /// <summary>화면에 보이는 구간의 길이(초).</summary>
    public double ViewportSeconds => ActualWidth / Scale;

    /// <summary>화면을 그릴 때마다 불러, 바뀐 것이 있을 때만 다시 그린다.</summary>
    public void Refresh()
    {
        if (Model is not { Project: { } project } model) return;

        // 재생 중에 재생 위치선이 화면 밖으로 나가면 따라간다.
        if (model.IsPlaying && ActualWidth > 0)
        {
            var x = XOf(model.Position);
            if (x < 0 || x > ActualWidth - 24) model.TimelineOffset = Math.Max(0, model.Position.TotalSeconds - 48 / Scale);
        }

        if (project.Sequence.Tracks.Count != _trackCount)
        {
            _trackCount = project.Sequence.Tracks.Count;
            InvalidateMeasure();
        }

        var state = (model.PositionSeconds, model.TimelineZoom, model.TimelineOffset, model.TimelineVersion, ActualWidth);
        if (state == _drawn) return;
        _drawn = state;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
        RulerHeight + Math.Max(1, Model?.Project?.Sequence.Tracks.Count ?? 0) * TrackHeight);

    // ───── 그리기 ─────

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        if (Model is not { Project: { } project } model) return;

        var sequence = project.Sequence;
        DrawRuler(dc, sequence, width);

        for (var i = 0; i < sequence.Tracks.Count; i++)
        {
            var track = sequence.Tracks[i];
            var top = RulerHeight + i * TrackHeight;
            dc.DrawLine(LinePen, new Point(0, top + TrackHeight), new Point(width, top + TrackHeight));

            // 숨기거나 음소거한 트랙은 흐리게 그린다.
            var dimmed = track.Hidden || track.Muted;
            if (dimmed) dc.PushOpacity(0.4);
            foreach (var clip in track.Clips) DrawClip(dc, model, project, track, clip, top, width);
            if (dimmed) dc.Pop();
        }

        foreach (var marker in sequence.Markers)
        {
            var x = XOf(marker.Time);
            if (x < -80 || x > width) continue;
            dc.DrawGeometry(MarkerBrush, null, Diamond(x, RulerHeight - 8, 5));
            if (marker.Name.Length > 0) dc.DrawText(Text(marker.Name, MarkerBrush, TextFace, 10), new Point(x + 8, RulerHeight - 15));
        }

        var playhead = XOf(model.Position);
        if (playhead >= -8 && playhead <= width + 8)
        {
            dc.DrawLine(PlayheadPen, new Point(playhead, 0), new Point(playhead, height));
            dc.DrawRoundedRectangle(PlayheadBrush, null, new Rect(playhead - 6, 0, 12, 12), 2, 2);
        }
    }

    private void DrawRuler(DrawingContext dc, Sequence sequence, double width)
    {
        dc.DrawLine(LinePen, new Point(0, RulerHeight), new Point(width, RulerHeight));

        // 글자가 겹치지 않도록 눈금 사이가 70픽셀 이상 되는 가장 작은 간격을 고른다.
        var frame = 1.0 / sequence.FrameRate;
        var step = new[] { frame, frame * 5 }.Concat(RulerSteps).FirstOrDefault(s => s * Scale >= 70, 3600);
        var offset = Model!.TimelineOffset;
        for (var index = (long)Math.Floor(offset / step); ; index++)
        {
            var seconds = index * step;
            var x = (seconds - offset) * Scale;
            if (x > width) break;
            if (x < -70) continue;

            dc.DrawLine(TickPen, new Point(x, 6), new Point(x, RulerHeight));
            dc.DrawText(Text(RulerLabel(sequence, seconds, step), RulerText, TimeFace, 11), new Point(x + 4, 6));
        }
    }

    /// <summary>1초 미만 간격이면 분:초:프레임, 아니면 분:초 (1시간부터는 시:분:초).</summary>
    private static string RulerLabel(Sequence sequence, double seconds, double step)
    {
        var whole = (long)Math.Floor(seconds + 1e-6);
        var text = whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60:00}:{whole % 60:00}";
        if (step >= 1) return text;

        var frames = (long)Math.Round((seconds - whole) * sequence.FrameRate);
        return $"{text}:{frames:00}";
    }

    private void DrawClip(DrawingContext dc, MainViewModel model, Project project, Track track, Clip clip, double top, double width)
    {
        var left = XOf(clip.Range.Start);
        var right = XOf(clip.Range.End);
        if (right < 0 || left > width) return;

        var rect = new Rect(left, top + 6, Math.Max(2, right - left), TrackHeight - 12);
        var (fill, border) = ColorsOf(track.Color);
        dc.DrawRoundedRectangle(fill, border, rect, 5, 5);
        if (rect.Width < 12) return;

        dc.PushClip(new RectangleGeometry(rect, 5, 5));

        var asset = clip is MediaClip media ? project.FindAsset(media.AssetId) : null;
        var textLeft = rect.Left + 8;

        if (clip is AudioClip audio && model.WaveformOf(audio.AssetId) is { } waveform)
        {
            DrawWaveform(dc, waveform, audio, rect, width);
        }
        else if (clip is VideoClip or ImageClip && asset is not null && model.ItemOf(asset.Id)?.Thumbnail is { } thumbnail)
        {
            // 클립 앞머리에 썸네일을 그린다.
            var thumbWidth = rect.Height * thumbnail.Width / thumbnail.Height;
            dc.DrawImage(thumbnail, new Rect(rect.Left, rect.Top, thumbWidth, rect.Height));
            textLeft = rect.Left + thumbWidth + 6;
        }

        var label = clip switch
        {
            TextClip text => text.Text,
            CaptionClip caption => caption.Text,
            _ => asset?.Name ?? "(원본 없음)",
        };
        dc.DrawText(Text(label, ClipText, TextFace, 11), new Point(textLeft, rect.Top + 3));
        dc.Pop();
    }

    /// <summary>화면의 픽셀 한 줄마다 그 구간에서 가장 큰 소리 크기를 세로선으로 그린다.</summary>
    private void DrawWaveform(DrawingContext dc, Waveform waveform, AudioClip clip, Rect rect, double width)
    {
        var middle = rect.Top + rect.Height / 2;
        var half = rect.Height / 2 - 3;
        var peaksPerPixel = Waveform.PeaksPerSecond / Scale;

        for (var x = Math.Max(rect.Left, 0); x < Math.Min(rect.Right, width); x++)
        {
            var source = clip.SourceStart.TotalSeconds + (x - rect.Left) / Scale;
            var first = (int)(source * Waveform.PeaksPerSecond);
            var last = Math.Max(first + 1, (int)(first + peaksPerPixel));

            var peak = 0f;
            for (var i = Math.Max(first, 0); i < Math.Min(last, waveform.Peaks.Length); i++)
                peak = Math.Max(peak, waveform.Peaks[i]);

            var size = Math.Max(0.5, peak * half);
            dc.DrawLine(WavePen, new Point(x + 0.5, middle - size), new Point(x + 0.5, middle + size));
        }
    }

    private (Brush Fill, Pen Border) ColorsOf(string color)
    {
        if (_trackColors.TryGetValue(color, out var cached)) return cached;

        Color parsed;
        try
        {
            parsed = (Color)ColorConverter.ConvertFromString(color);
        }
        catch (FormatException)
        {
            parsed = Color.FromRgb(0x4C, 0x8D, 0xFF);
        }

        // 클립 안쪽은 트랙 색을 어둡게, 테두리는 트랙 색 그대로.
        var fill = new SolidColorBrush(Color.FromRgb((byte)(parsed.R * 0.45), (byte)(parsed.G * 0.45), (byte)(parsed.B * 0.45)));
        fill.Freeze();
        var border = new Pen(new SolidColorBrush(parsed), 1);
        border.Freeze();
        return _trackColors[color] = (fill, border);
    }

    private FormattedText Text(string text, Brush brush, Typeface face, double size) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Geometry Diamond(double x, double y, double radius)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x, y - radius), true, true);
            context.LineTo(new Point(x + radius, y), false, false);
            context.LineTo(new Point(x, y + radius), false, false);
            context.LineTo(new Point(x - radius, y), false, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }

    // ───── 마우스 ─────

    private Marker? MarkerAt(Point point) => point.Y > RulerHeight
        ? null
        : Model?.Project?.Sequence.Markers.FirstOrDefault(marker => Math.Abs(XOf(marker.Time) - point.X) <= 7);

    /// <summary>트랙 줄 위치에 있는 트랙. 눈금이나 빈 곳이면 null.</summary>
    private Track? TrackAt(Point point)
    {
        var tracks = Model?.Project?.Sequence.Tracks;
        var index = (int)Math.Floor((point.Y - RulerHeight) / TrackHeight);
        return tracks is not null && point.Y >= RulerHeight && index < tracks.Count ? tracks[index] : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Model is not { Project: not null } model) return;
        var point = e.GetPosition(this);
        if (point.Y > RulerHeight) return;

        if (MarkerAt(point) is { } marker)
        {
            // 마커를 누르면 그 위치로 가고, 두 번 누르면 이름을 바꾼다.
            model.SeekFromTimeline(marker.Time, TimeSpan.Zero);
            if (e.ClickCount == 2) MarkerRenameRequested?.Invoke(marker);
        }
        else
        {
            // 눈금을 누르거나 끌면 재생 위치를 옮긴다.
            CaptureMouse();
            model.SeekFromTimeline(TimeAt(point.X), SnapTolerance);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Model?.SeekFromTimeline(TimeAt(e.GetPosition(this).X), SnapTolerance);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (Model is not { } model || MarkerAt(e.GetPosition(this)) is not { } marker) return;

        var rename = new MenuItem { Header = "이름 바꾸기" };
        rename.Click += (_, _) => MarkerRenameRequested?.Invoke(marker);
        var delete = new MenuItem { Header = "마커 삭제" };
        delete.Click += (_, _) => model.DeleteMarker(marker);
        new ContextMenu { Items = { rename, delete }, PlacementTarget = this, IsOpen = true };
        e.Handled = true;
    }

    /// <summary>Ctrl+휠은 마우스 위치를 기준으로 확대·축소, 휠만 굴리면 좌우로 움직인다.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Model is not { } model) return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var x = e.GetPosition(this).X;
            var anchor = TimeAt(x).TotalSeconds;
            model.TimelineZoom = Math.Clamp(model.TimelineZoom + (e.Delta > 0 ? 0.04 : -0.04), 0, 1);
            model.TimelineOffset = Math.Max(0, anchor - x / Scale); // 마우스 아래의 시각이 그대로 있게
        }
        else
        {
            model.TimelineOffset = Math.Max(0, model.TimelineOffset - e.Delta / 120.0 * 80 / Scale);
        }

        e.Handled = true;
    }

    // ───── 보관함에서 끌어 놓기 ─────

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AssetItem)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (Model is not { } model || e.Data.GetData(typeof(AssetItem)) is not AssetItem item) return;

        var point = e.GetPosition(this);
        model.AddToTimelineAt(item, TrackAt(point), TimeAt(point.X), SnapTolerance);
        e.Handled = true;
    }
}
