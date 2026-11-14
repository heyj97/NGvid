using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NGL.Core;
using NGL.Media;
using NGL.Playback;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.App;

/// <summary>
/// M1 단계의 임시 화면: 영상 파일 하나를 열어 미리보기에 띄우고 위치를 옮겨 본다.
/// 보관함·타임라인이 생기면(M2~M3) 프로젝트를 여는 흐름으로 바뀐다.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IFrameCompositor _compositor = new SkiaCompositor();
    private VideoFrameSource? _frames;

    [ObservableProperty] private Project? _project;
    [ObservableProperty] private string _infoText = "영상 열기를 누르거나 파일을 창에 끌어 놓으세요";
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _lastFrameSeconds;
    [ObservableProperty] private double _frameStepSeconds = 1 / 30.0;
    [ObservableProperty] private string _timeText = "00:00:00:00 / 00:00:00:00";

    /// <summary>미리보기를 다시 그려야 할 때.</summary>
    public event Action? RenderRequested;

    public void Open(string path)
    {
        MediaInfo info;
        try
        {
            info = MediaProbe.Probe(path);
        }
        catch (Exception e) when (e is InvalidDataException or DllNotFoundException)
        {
            InfoText = e.Message;
            return;
        }

        if (info.Video is not { } video)
        {
            InfoText = $"영상이 없는 파일입니다 ({Path.GetFileName(path)})";
            return;
        }

        var project = new Project { Name = Path.GetFileNameWithoutExtension(path) };
        var sequence = project.Sequence;
        sequence.Preset = video.Width > video.Height ? SequencePreset.Landscape
            : video.Width < video.Height ? SequencePreset.Portrait
            : SequencePreset.Square;
        sequence.FrameRate = video.FrameRate > 45 ? 60 : 30;

        // 임시: 프로젝트 폴더로 복사하지 않고 원래 위치를 그대로 가리킨다. (가져오기는 M2)
        var asset = new MediaAsset { Kind = MediaKind.Video, Name = project.Name, RelativePath = path, Duration = info.Duration };
        project.Assets.Add(asset);

        var track = new VideoTrack { Name = "영상 1" };
        track.Clips.Add(new VideoClip { AssetId = asset.Id, Range = new TimeRange(TimeSpan.Zero, info.Duration) });
        sequence.Tracks.Add(track);

        _frames?.Dispose();
        _frames = new VideoFrameSource(id => project.FindAsset(id)?.RelativePath);

        Project = project;
        FrameStepSeconds = 1.0 / sequence.FrameRate;
        LastFrameSeconds = Math.Max(0, sequence.TimeAt(sequence.FrameAt(info.Duration) - 1).TotalSeconds);
        InfoText = $"{Path.GetFileName(path)}  ·  {video.Width}×{video.Height}  ·  {video.FrameRate:0.##}fps  ·  {video.Codec}"
            + (info.Audio is { } audio ? $"  ·  오디오 {audio.Codec} {audio.SampleRate}Hz" : "  ·  오디오 없음")
            + $"  ·  시퀀스 {sequence.Preset.Name} {sequence.FrameRate}fps";

        PositionSeconds = 0;
        Refresh();
    }

    partial void OnPositionSecondsChanged(double value) => Refresh();

    private void Refresh()
    {
        if (Project is { } project)
            TimeText = $"{Timecode(project.Sequence, CurrentTime)} / {Timecode(project.Sequence, project.Sequence.Duration)}";
        RenderRequested?.Invoke();
    }

    /// <summary>현재 위치를 프레임 경계에 맞춘 시각.</summary>
    private TimeSpan CurrentTime =>
        Project?.Sequence.SnapToFrame(TimeSpan.FromSeconds(PositionSeconds)) ?? TimeSpan.Zero;

    /// <summary>현재 위치의 프레임을 시퀀스 좌표의 캔버스에 그린다.</summary>
    public void Render(SKCanvas canvas)
    {
        if (Project is null || _frames is null) return;

        var time = CurrentTime;
        _compositor.Render(canvas, Project.Sequence, time,
            clip => clip is VideoClip video ? _frames.GetImage(video, time) : null);
    }

    /// <summary>시:분:초:프레임</summary>
    private static string Timecode(Sequence sequence, TimeSpan time)
    {
        var frames = sequence.FrameAt(time);
        var seconds = frames / sequence.FrameRate;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{frames % sequence.FrameRate:00}";
    }

    public void Dispose() => _frames?.Dispose();
}
