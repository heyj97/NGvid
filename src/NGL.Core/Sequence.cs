using System.Text.Json.Serialization;

namespace NGL.Core;

/// <summary>타임라인에 꽂는 책갈피.</summary>
public sealed class Marker
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TimeSpan Time { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>편집하는 결과물 한 편과 그 규격(화면비·fps). 프로젝트당 하나.</summary>
public sealed class Sequence
{
    public static IReadOnlyList<int> SupportedFrameRates { get; } = [30, 60];

    /// <summary>이미지 클립을 처음 놓을 때의 길이.</summary>
    public static readonly TimeSpan DefaultStillDuration = TimeSpan.FromSeconds(5);

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

    public List<Marker> Markers { get; } = [];

    /// <summary>가장 늦게 끝나는 클립의 끝 시각.</summary>
    [JsonIgnore]
    public TimeSpan Duration => Tracks
        .SelectMany(track => track.Clips)
        .Select(clip => clip.Range.End)
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();

    // ───── 트랙 ─────

    /// <summary>
    /// 새 트랙을 "영상 2"처럼 이름 붙여 추가한다.
    /// 자막은 맨 위, 영상은 기존 영상 트랙들 위, 오디오는 맨 아래에 놓는다.
    /// </summary>
    public T AddTrack<T>() where T : Track, new()
    {
        var track = new T();
        track.Name = track switch
        {
            VideoTrack => "영상",
            AudioTrack => "오디오",
            _ => "자막",
        } + " " + (Tracks.OfType<T>().Count() + 1);

        var index = track switch
        {
            CaptionTrack => 0,
            VideoTrack => Tracks.FindIndex(existing => existing is not CaptionTrack) is var first and >= 0 ? first : Tracks.Count,
            _ => Tracks.Count,
        };
        Tracks.Insert(index, track);
        return track;
    }

    /// <summary>트랙을 위(-1)나 아래(+1)로 한 칸 옮긴다. 끝이면 그대로 둔다.</summary>
    public void MoveTrack(Track track, int offset)
    {
        var from = Tracks.IndexOf(track);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Tracks.Count) return;

        Tracks.RemoveAt(from);
        Tracks.Insert(to, track);
    }

    // ───── 클립 배치 ─────

    /// <summary>보관함의 원본을 트랙 맨 뒤에 클립으로 놓는다.</summary>
    public IReadOnlyList<Clip> AddToEnd(MediaAsset asset) => Add(asset);

    /// <summary>
    /// 보관함의 원본을 클립으로 놓는다. 맞는 종류의 트랙이 없으면 만든다.
    /// 소리가 있는 영상은 영상 클립과 오디오 클립을 같은 위치에 함께 놓는다.
    /// </summary>
    /// <param name="track">놓을 트랙. 원본 종류와 맞지 않거나 null이면 그 종류의 첫 트랙.</param>
    /// <param name="start">놓을 시각. null이거나 그 자리에 다른 클립이 있으면 트랙 맨 뒤에 놓는다.</param>
    public IReadOnlyList<Clip> Add(MediaAsset asset, Track? track = null, TimeSpan? start = null)
    {
        Track main = asset.Kind == MediaKind.Audio
            ? track as AudioTrack ?? FirstOrNew<AudioTrack>()
            : track as VideoTrack ?? FirstOrNew<VideoTrack>();
        Track? linked = asset.Kind == MediaKind.Video && asset.HasAudio ? FirstOrNew<AudioTrack>() : null;
        var duration = asset.Kind == MediaKind.Image ? DefaultStillDuration : asset.Duration;

        var wanted = start is { } time
            ? new TimeRange(SnapToFrame(time < TimeSpan.Zero ? TimeSpan.Zero : time), duration)
            : (TimeRange?)null;
        if (wanted is not { } range || !IsFree(main, range) || (linked is not null && !IsFree(linked, range)))
        {
            var end = EndOf(main);
            if (linked is not null && EndOf(linked) > end) end = EndOf(linked);
            range = new TimeRange(end, duration);
        }

        Clip clip = asset.Kind switch
        {
            MediaKind.Video => new VideoClip { AssetId = asset.Id, Range = range },
            MediaKind.Image => new ImageClip { AssetId = asset.Id, Range = range },
            _ => new AudioClip { AssetId = asset.Id, Range = range },
        };
        main.Clips.Add(clip);
        if (linked is null) return [clip];

        var sound = new AudioClip { AssetId = asset.Id, Range = range };
        linked.Clips.Add(sound);
        return [clip, sound];
    }

    private T FirstOrNew<T>() where T : Track, new() => Tracks.OfType<T>().FirstOrDefault() ?? AddTrack<T>();

    private static bool IsFree(Track track, TimeRange range) => !track.Clips.Any(clip => clip.Range.Overlaps(range));

    /// <summary>트랙의 마지막 클립이 끝나는 시각(프레임 경계에 맞춤).</summary>
    private TimeSpan EndOf(Track track) =>
        SnapToFrame(track.Clips.Select(clip => clip.Range.End).DefaultIfEmpty(TimeSpan.Zero).Max());

    // ───── 스냅 ─────

    /// <summary>재생 위치선이나 클립이 달라붙을 수 있는 시각들: 0초, 모든 클립의 양끝, 마커.</summary>
    public IEnumerable<TimeSpan> SnapPoints() => Tracks
        .SelectMany(track => track.Clips)
        .SelectMany(clip => new[] { clip.Range.Start, clip.Range.End })
        .Concat(Markers.Select(marker => marker.Time))
        .Append(TimeSpan.Zero);

    /// <summary>tolerance 안에 달라붙을 시각이 있으면 가장 가까운 것을, 없으면 원래 시각을 돌려준다.</summary>
    public static TimeSpan Snap(TimeSpan time, IEnumerable<TimeSpan> points, TimeSpan tolerance)
    {
        var best = time;
        var bestDistance = tolerance;
        foreach (var point in points)
        {
            var distance = (point - time).Duration();
            if (distance > bestDistance) continue;
            best = point;
            bestDistance = distance;
        }

        return best;
    }

    // ───── 프레임과 시각 ─────

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
