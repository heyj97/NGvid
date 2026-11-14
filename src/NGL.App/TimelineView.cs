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
    private static readonly Typeface TextFace = new("Pretendard, Noto Sans KR, Malgun Gothic");
    private static readonly Typeface TimeFace = new("IBM Plex Mono, Cascadia Mono, Consolas");

    private static readonly Pen SelectedPen = FrozenPen("#FFFFFF", 2);
    private static readonly Pen GhostPen = DashedPen("#FFFFFF", 1.5);

    private enum DragMode { None, Scrub, Move, TrimStart, TrimEnd }

    private readonly Dictionary<string, (Brush Fill, Pen Border)> _trackColors = [];

    // 마우스로 끄는 동안의 상태. 놓을 때 한 번만 편집으로 반영한다.
    private DragMode _drag;
    private Clip? _dragClip;
    private Point _dragOrigin;
    private TimeSpan _grabOffset; // 잡은 곳이 클립 시작에서 떨어진 거리
    private bool _dragStarted;    // 조금이라도 움직였는가 (그냥 누른 것과 구별)
    private TimeSpan _dragTime;   // 옮길 시작 시각 또는 트리밍할 끝의 시각
    private Track? _dragTrack;    // 옮겨 놓을 트랙
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
        // 트랙이 적어도 보이는 영역(트랙 4개)만큼은 차지한다. 트랙 아래의 빈 곳도 눌러서 선택을 풀 수 있게.
        RulerHeight + Math.Max(4, Model?.Project?.Sequence.Tracks.Count ?? 0) * TrackHeight);

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

        DrawDragGhost(dc, model, sequence, height);

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
        dc.DrawRoundedRectangle(fill, model.IsSelected(clip) ? SelectedPen : border, rect, 5, 5);
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
        if (clip.GroupId is not null) dc.DrawText(Text("그룹", RulerText, TextFace, 9), new Point(textLeft, rect.Bottom - 14));

        // 키프레임이 있는 시각마다 클립 아래쪽에 ◆를 그린다.
        foreach (var time in clip.Keyframes.Values.SelectMany(keys => keys).Select(key => key.Time).Distinct())
            dc.DrawGeometry(ClipText, null, Diamond(XOf(clip.Range.Start + time), rect.Bottom - 6, 3.5));
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

    private static Pen DashedPen(string color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
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

    /// <summary>그 위치에 있는 (잠기지 않은 트랙의) 클립.</summary>
    private Clip? ClipAt(Point point) => TrackAt(point) is { Locked: false } track
        ? track.Clips.FirstOrDefault(clip => point.X >= XOf(clip.Range.Start) && point.X < XOf(clip.Range.End))
        : null;

    /// <summary>클립의 어디를 잡았는가: 양끝 6픽셀 안이면 트리밍, 그 밖은 이동.</summary>
    private DragMode GripOf(Clip clip, double x)
    {
        var left = XOf(clip.Range.Start);
        var right = XOf(clip.Range.End);
        if (right - left < 16) return DragMode.Move; // 너무 좁으면 끝을 잡기 어렵다
        return x - left <= 6 ? DragMode.TrimStart : right - x <= 6 ? DragMode.TrimEnd : DragMode.Move;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Model is not { Project: not null } model) return;
        var point = e.GetPosition(this);
        e.Handled = true;

        if (point.Y <= RulerHeight)
        {
            if (MarkerAt(point) is { } marker)
            {
                // 마커를 누르면 그 위치로 가고, 두 번 누르면 이름을 바꾼다.
                model.SeekFromTimeline(marker.Time, TimeSpan.Zero);
                if (e.ClickCount == 2) MarkerRenameRequested?.Invoke(marker);
            }
            else
            {
                // 눈금을 누르거나 끌면 재생 위치를 옮긴다.
                _drag = DragMode.Scrub;
                CaptureMouse();
                model.SeekFromTimeline(TimeAt(point.X), SnapTolerance);
            }

            return;
        }

        if (ClipAt(point) is not { } clip)
        {
            model.Select(null); // 빈 곳을 누르면 선택을 푼다
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            model.Select(clip, toggle: true); // Ctrl+클릭: 선택에 더하거나 뺀다
            return;
        }

        if (!model.IsSelected(clip)) model.Select(clip);
        _drag = GripOf(clip, point.X);
        _dragClip = clip;
        _dragOrigin = point;
        _dragStarted = false;
        _grabOffset = TimeSpan.FromSeconds(point.X / Scale + model.TimelineOffset) - clip.Range.Start;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Model is not { } model) return;
        var point = e.GetPosition(this);

        if (!IsMouseCaptured)
        {
            // 클립의 양끝 위에서는 좌우 화살표로 바꿔 트리밍할 수 있음을 알린다.
            Cursor = ClipAt(point) is { } hovered && GripOf(hovered, point.X) != DragMode.Move ? Cursors.SizeWE : null;
            return;
        }

        if (_drag == DragMode.Scrub)
        {
            model.SeekFromTimeline(TimeAt(point.X), SnapTolerance);
            return;
        }

        if (_dragClip is not { } clip) return;
        if (!_dragStarted && Math.Abs(point.X - _dragOrigin.X) < 4 && Math.Abs(point.Y - _dragOrigin.Y) < 4) return;
        _dragStarted = true;

        var moving = model.SelectedClips();
        if (_drag == DragMode.Move)
        {
            // 클립의 시작이나 끝 중 더 가까이 붙는 쪽에 맞춘다.
            var start = TimeSpan.FromSeconds(point.X / Scale + model.TimelineOffset) - _grabOffset;
            var snappedStart = model.SnapTime(start, SnapTolerance, moving);
            var snappedEnd = model.SnapTime(start + clip.Range.Duration, SnapTolerance, moving) - clip.Range.Duration;
            _dragTime = snappedStart != start ? snappedStart : snappedEnd;
            _dragTrack = TrackAt(point);
        }
        else
        {
            _dragTime = model.SnapTime(TimeAt(point.X), SnapTolerance, moving);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();

        var (drag, clip, started) = (_drag, _dragClip, _dragStarted);
        _drag = DragMode.None;
        _dragClip = null;
        _dragStarted = false;
        if (Model is not { } model || clip is null) return;
        if (!started)
        {
            // 끌지 않고 눌렀다 뗐으면 그 클립만 선택한다. (누르는 순간에 풀면 여러 클립을 함께 끌 수 없다.)
            model.Select(clip);
            return;
        }

        switch (drag)
        {
            case DragMode.Move: model.MoveSelection(clip, _dragTime, _dragTrack); break;
            case DragMode.TrimStart: model.Trim(clip, start: true, _dragTime); break;
            case DragMode.TrimEnd: model.Trim(clip, start: false, _dragTime); break;
        }

        InvalidateVisual();
    }

    /// <summary>끄는 동안 놓일 자리를 점선으로 보여준다. 실제 결과는 놓았을 때 편집 규칙(끼워 넣기 등)이 정한다.</summary>
    private void DrawDragGhost(DrawingContext dc, MainViewModel model, Sequence sequence, double height)
    {
        if (!_dragStarted || _dragClip is not { } anchor) return;

        if (_drag != DragMode.Move)
        {
            var x = XOf(_dragTime);
            dc.DrawLine(GhostPen, new Point(x, RulerHeight), new Point(x, height));
            return;
        }

        var delta = _dragTime - anchor.Range.Start;
        var anchorTrack = sequence.TrackOf(anchor);
        foreach (var clip in model.SelectedClips())
        {
            // anchor와 같은 트랙의 클립은 마우스가 가리키는 같은 종류의 트랙으로 따라간다.
            var track = sequence.TrackOf(clip);
            if (track == anchorTrack && _dragTrack is not null && _dragTrack.GetType() == track?.GetType()) track = _dragTrack;
            var index = track is null ? -1 : sequence.Tracks.IndexOf(track);
            if (index < 0) continue;

            var left = XOf(clip.Range.Start + delta);
            dc.DrawRoundedRectangle(null, GhostPen,
                new Rect(left, RulerHeight + index * TrackHeight + 6, Math.Max(2, XOf(clip.Range.End + delta) - left), TrackHeight - 12), 5, 5);
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (Model is not { } model) return;
        var point = e.GetPosition(this);
        e.Handled = true;

        if (MarkerAt(point) is { } marker)
        {
            Open([new("이름 바꾸기", () => MarkerRenameRequested?.Invoke(marker)), new("마커 삭제", () => model.DeleteMarker(marker))]);
        }
        else if (ClipAt(point) is { } clip)
        {
            // 선택하지 않은 클립을 누르면 그 클립을 선택한다. 선택한 것 중 하나를 누르면 선택을 그대로 둔다.
            if (!model.IsSelected(clip)) model.Select(clip);
            Open(model.ClipMenu());
        }
        else if (point.Y > RulerHeight)
        {
            Open(model.EmptyMenu());
        }
    }

    private void Open(List<MenuEntry> entries)
    {
        var menu = new ContextMenu { PlacementTarget = this };
        foreach (var entry in entries) menu.Items.Add(ItemOf(entry));
        menu.IsOpen = true;
    }

    /// <param name="choice">여럿 중 하나를 고르는 항목인가. 체크 표시 자리를 비워 두어 줄이 맞게 한다.</param>
    private static object ItemOf(MenuEntry entry, bool choice = false)
    {
        if (entry.Header.Length == 0) return new Separator();

        var item = new MenuItem { Header = entry.Header, InputGestureText = entry.Gesture, IsCheckable = choice, IsChecked = entry.Checked };
        if (entry.Run is { } run) item.Click += (_, _) => run();
        foreach (var child in entry.Children ?? []) item.Items.Add(ItemOf(child, choice: true));
        return item;
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
