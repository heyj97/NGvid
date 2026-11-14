using System.Text.Json.Serialization;

namespace NGL.Core;

/// <summary>편집하는 결과물 한 편과 그 규격(화면비·fps). 프로젝트당 하나.</summary>
public sealed class Sequence
{
    public static IReadOnlyList<int> SupportedFrameRates { get; } = [30, 60];

    private SequencePreset _preset = SequencePreset.Landscape;
    private int _frameRate = 30;

    public SequencePreset Preset
    {
        get => _preset;
        set => _preset = value ?? throw new ArgumentNullException(nameof(value));
    }

    public int FrameRate
    {
        get => _frameRate;
        set => _frameRate = SupportedFrameRates.Contains(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "지원하지 않는 프레임레이트입니다.");
    }

    /// <summary>목록의 순서가 트랙 순서다. 첫 트랙이 화면 맨 위에 그려진다.</summary>
    public List<Track> Tracks { get; } = [];

    /// <summary>가장 늦게 끝나는 클립의 끝 시각.</summary>
    [JsonIgnore]
    public TimeSpan Duration => Tracks
        .SelectMany(track => track.Clips)
        .Select(clip => clip.Range.End)
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();

    /// <summary>이미지 클립을 처음 놓을 때의 길이.</summary>
    public static readonly TimeSpan DefaultStillDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 보관함의 원본을 트랙 맨 뒤에 클립으로 놓는다. 맞는 종류의 트랙이 없으면 만든다.
    /// 소리가 있는 영상은 영상 클립과 오디오 클립을 같은 위치에 함께 놓는다.
    /// </summary>
    public IReadOnlyList<Clip> AddToEnd(MediaAsset asset)
    {
        switch (asset.Kind)
        {
            case MediaKind.Video:
            {
                var video = FirstOrNew<VideoTrack>("영상");
                var audio = asset.HasAudio ? FirstOrNew<AudioTrack>("오디오") : null;
                var start = EndOf(video);
                if (audio is not null && EndOf(audio) > start) start = EndOf(audio);

                var range = new TimeRange(start, asset.Duration);
                var clips = new List<Clip> { new VideoClip { AssetId = asset.Id, Range = range } };
                video.Clips.Add(clips[0]);
                if (audio is not null)
                {
                    clips.Add(new AudioClip { AssetId = asset.Id, Range = range });
                    audio.Clips.Add(clips[1]);
                }

                return clips;
            }
            case MediaKind.Image:
            {
                var track = FirstOrNew<VideoTrack>("영상");
                var clip = new ImageClip { AssetId = asset.Id, Range = new TimeRange(EndOf(track), DefaultStillDuration) };
                track.Clips.Add(clip);
                return [clip];
            }
            default:
            {
                var track = FirstOrNew<AudioTrack>("오디오");
                var clip = new AudioClip { AssetId = asset.Id, Range = new TimeRange(EndOf(track), asset.Duration) };
                track.Clips.Add(clip);
                return [clip];
            }
        }
    }

    private T FirstOrNew<T>(string name) where T : Track, new()
    {
        if (Tracks.OfType<T>().FirstOrDefault() is { } existing) return existing;

        var track = new T { Name = $"{name} 1" };
        // 영상 트랙은 오디오 트랙보다 위에 둔다.
        if (track is VideoTrack) Tracks.Insert(0, track);
        else Tracks.Add(track);
        return track;
    }

    /// <summary>트랙의 마지막 클립이 끝나는 시각(프레임 경계에 맞춤).</summary>
    private TimeSpan EndOf(Track track) =>
        SnapToFrame(track.Clips.Select(clip => clip.Range.End).DefaultIfEmpty(TimeSpan.Zero).Max());

    // 시간은 fps와 무관한 TimeSpan으로 저장한다. 작업 중 fps를 바꿔도 클립 위치가 변하지 않는다.
    // TimeAt은 올림, FrameAt은 내림이라서 FrameAt(TimeAt(n)) == n이 항상 성립한다.

    /// <summary>주어진 시각에 보이는 프레임 번호.</summary>
    public long FrameAt(TimeSpan time) => time.Ticks * FrameRate / TimeSpan.TicksPerSecond;

    /// <summary>프레임이 시작하는 시각.</summary>
    public TimeSpan TimeAt(long frame) =>
        new((frame * TimeSpan.TicksPerSecond + FrameRate - 1) / FrameRate);

    /// <summary>가장 가까운 프레임 경계로 맞춘 시각.</summary>
    public TimeSpan SnapToFrame(TimeSpan time) =>
        TimeAt((long)Math.Round(time.Ticks * (double)FrameRate / TimeSpan.TicksPerSecond));
}
