using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NGL.Audio;
using NGL.Core;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.App;

/// <summary>
/// M1 단계의 임시 화면: 영상 파일 하나를 열어 재생·탐색해 본다.
/// 보관함·타임라인이 생기면(M2~M3) 프로젝트를 여는 흐름으로 바뀐다.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioOutput _output = new();
    private PlaybackEngine? _engine;
    private bool _followingEngine; // 엔진의 위치를 화면에 옮기는 중 (사용자가 슬라이더를 움직인 것이 아님)
    private TimeSpan _loopStart;
    private TimeSpan _loopEnd;

    [ObservableProperty] private Project? _project;
    [ObservableProperty] private string _infoText = "영상 열기를 누르거나 파일을 창에 끌어 놓으세요";
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _lastFrameSeconds;
    [ObservableProperty] private string _timeText = "00:00:00:00 / 00:00:00:00";
    [ObservableProperty] private string _loopText = "";
    [ObservableProperty] private string _audioWarning = "";
    [ObservableProperty] private int _qualityIndex; // 0 전체, 1 = 1/2, 2 = 1/4

    private PreviewQuality Quality => (PreviewQuality)(1 << QualityIndex);

    partial void OnQualityIndexChanged(int value)
    {
        if (_engine is not null) _engine.Quality = Quality;
    }
    [ObservableProperty] private bool _isLooping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    private bool _isPlaying;

    public string PlayButtonText => IsPlaying ? "일시정지" : "재생";

    /// <summary>미리보기를 다시 그려야 할 때. 화면 스레드가 아닌 곳에서도 불린다.</summary>
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
        var range = new TimeRange(TimeSpan.Zero, info.Duration);

        var videoTrack = new VideoTrack { Name = "영상 1" };
        videoTrack.Clips.Add(new VideoClip { AssetId = asset.Id, Range = range });
        sequence.Tracks.Add(videoTrack);

        if (info.Audio is not null)
        {
            var audioTrack = new AudioTrack { Name = "오디오 1" };
            audioTrack.Clips.Add(new AudioClip { AssetId = asset.Id, Range = range });
            sequence.Tracks.Add(audioTrack);
        }

        _engine?.Dispose();
        _engine = new PlaybackEngine(sequence, id => project.FindAsset(id)?.RelativePath, _output);
        _engine.Quality = Quality;
        _engine.FrameReady += () => RenderRequested?.Invoke();

        Project = project;
        LastFrameSeconds = _engine.LastFrameTime.TotalSeconds;
        InfoText = $"{Path.GetFileName(path)}  ·  {video.Width}×{video.Height}  ·  {video.FrameRate:0.##}fps  ·  {video.Codec}"
            + (info.Audio is { } audio ? $"  ·  오디오 {audio.Codec} {audio.SampleRate}Hz" : "  ·  오디오 없음")
            + $"  ·  시퀀스 {sequence.Preset.Name} {sequence.FrameRate}fps";

        _loopStart = TimeSpan.Zero;
        _loopEnd = sequence.Duration;
        IsLooping = false;
        ApplyLoop();
        Tick();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_engine is null) return;
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Play();
        AudioWarning = _engine.IsPlaying && _output.IsSilent ? "소리 장치를 열 수 없어 소리 없이 재생합니다" : "";
        Tick();
    }

    [RelayCommand]
    private void Step(string frames)
    {
        _engine?.Step(int.Parse(frames));
        Tick();
    }

    /// <summary>현재 위치를 반복 구간의 시작으로 정한다.</summary>
    [RelayCommand]
    private void SetLoopStart()
    {
        if (_engine is null || Project is null) return;
        _loopStart = _engine.Position;
        if (_loopEnd <= _loopStart) _loopEnd = Project.Sequence.Duration;
        ApplyLoop();
    }

    /// <summary>현재 프레임까지를 반복 구간으로 정한다.</summary>
    [RelayCommand]
    private void SetLoopEnd()
    {
        if (_engine is null || Project is null) return;
        var sequence = Project.Sequence;
        _loopEnd = sequence.TimeAt(sequence.FrameAt(_engine.Position) + 1);
        if (_loopEnd <= _loopStart) _loopStart = TimeSpan.Zero;
        ApplyLoop();
    }

    [RelayCommand]
    private void ToggleLoop() => IsLooping = !IsLooping;

    partial void OnIsLoopingChanged(bool value) => ApplyLoop();

    private void ApplyLoop()
    {
        if (_engine is null || Project is null) return;
        _engine.LoopRange = IsLooping ? new TimeRange(_loopStart, _loopEnd - _loopStart) : null;
        LoopText = $"{Timecode(Project.Sequence, _loopStart)} ~ {Timecode(Project.Sequence, _loopEnd)}";
    }

    /// <summary>엔진의 현재 위치와 재생 상태를 화면에 옮긴다. 화면을 그릴 때마다 부른다.</summary>
    public void Tick()
    {
        if (_engine is null || Project is null) return;

        var position = _engine.Position;
        _followingEngine = true;
        PositionSeconds = position.TotalSeconds;
        _followingEngine = false;

        IsPlaying = _engine.IsPlaying;
        TimeText = $"{Timecode(Project.Sequence, position)} / {Timecode(Project.Sequence, Project.Sequence.Duration)}";
    }

    // 사용자가 슬라이더를 움직였을 때
    partial void OnPositionSecondsChanged(double value)
    {
        if (_followingEngine) return;
        _engine?.Seek(TimeSpan.FromSeconds(value));
    }

    /// <summary>현재 프레임을 시퀀스 좌표의 캔버스에 그린다.</summary>
    public void Render(SKCanvas canvas) => _engine?.Render(canvas);

    /// <summary>시:분:초:프레임</summary>
    private static string Timecode(Sequence sequence, TimeSpan time)
    {
        var frames = sequence.FrameAt(time);
        var seconds = frames / sequence.FrameRate;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{frames % sequence.FrameRate:00}";
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _output.Dispose();
    }
}
