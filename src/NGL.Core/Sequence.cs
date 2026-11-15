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
    /// 영상은 맨 위, 오디오는 맨 아래에 놓는다.
    /// </summary>
    public T AddTrack<T>() where T : Track, new()
    {
        var track = new T();
        track.Name = (track is VideoTrack ? "영상" : "오디오") + " " + (Tracks.OfType<T>().Count() + 1);
        Tracks.Insert(track is VideoTrack ? 0 : Tracks.Count, track);
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

    /// <summary>클립이 놓인 트랙. 이 시퀀스의 클립이 아니면 null.</summary>
    public Track? TrackOf(Clip clip) => Tracks.FirstOrDefault(track => track.Clips.Contains(clip));

    // ───── 클립 배치 ─────

    /// <summary>보관함의 원본을 트랙 맨 뒤에 클립으로 놓는다.</summary>
    public IReadOnlyList<Clip> AddToEnd(MediaAsset asset) => Add(asset);

    /// <summary>
    /// 보관함의 원본을 클립으로 놓는다. 맞는 종류의 트랙이 없으면 만든다.
    /// 소리가 있는 영상은 영상 클립과 오디오 클립을 같은 위치에 함께 놓는다.
    /// </summary>
    /// <param name="track">놓을 트랙. 원본 종류와 맞지 않거나 잠겨 있거나 null이면 그 종류의 잠기지 않은 첫 트랙.</param>
    /// <param name="start">놓을 시각. null이거나 그 자리에 다른 클립이 있으면 트랙 맨 뒤에 놓는다.</param>
    public IReadOnlyList<Clip> Add(MediaAsset asset, Track? track = null, TimeSpan? start = null)
    {
        Track main = asset.Kind == MediaKind.Audio
            ? track as AudioTrack is { Locked: false } audioTrack ? audioTrack : FirstOrNew<AudioTrack>()
            : track as VideoTrack is { Locked: false } videoTrack ? videoTrack : FirstOrNew<VideoTrack>();
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

        // 영상과 그 소리를 연결해 둔다.
        clip.LinkId = Guid.NewGuid();
        var sound = new AudioClip { AssetId = asset.Id, Range = range, LinkId = clip.LinkId };
        linked.Clips.Add(sound);
        return [clip, sound];
    }

    /// <summary>그 종류의 잠기지 않은 첫 트랙. 없으면 새로 만든다.</summary>
    /// <summary>
    /// time의 화면을 그리는 데 그림이 필요한 영상·이미지 클립들: 그 시각에 놓인 클립과,
    /// 전환 중이라 함께 보여야 하는 앞 클립. 숨긴 트랙은 뺀다.
    /// </summary>
    public List<VisualClip> VisualsAt(TimeSpan time)
    {
        var result = new List<VisualClip>();
        foreach (var track in Tracks.Where(track => track is VideoTrack { Hidden: false }))
        {
            foreach (var clip in track.Clips.OfType<VisualClip>().Where(clip => clip.Range.Contains(time)))
            {
                result.Add(clip);
                if (Transitions.Outgoing(track, clip, time) is { } outgoing) result.Add(outgoing);
            }
        }

        return result;
    }

    // ───── 텍스트 ─────

    /// <summary>텍스트 클립을 처음 놓을 때의 길이.</summary>
    public static readonly TimeSpan DefaultTextDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 텍스트 클립을 time에 놓는다. 그 자리가 빈 영상 트랙(잠기지 않은 것 중 위쪽부터)에 놓고,
    /// 없으면 맨 위에 새 영상 트랙을 만든다. 텍스트는 영상 위에 보여야 하기 때문이다.
    /// </summary>
    /// <param name="preferred">먼저 써 볼 트랙. 영상 트랙이 아니거나 잠겼거나 그 자리가 차 있으면 위의 규칙을 따른다.</param>
    public TextClip AddText(TimeSpan time, string text, Track? preferred = null)
    {
        var clip = new TextClip
        {
            Text = text,
            Range = new TimeRange(SnapToFrame(time < TimeSpan.Zero ? TimeSpan.Zero : time), DefaultTextDuration),
        };
        var track = preferred as VideoTrack is { Locked: false } wanted && IsFree(wanted, clip.Range)
            ? wanted
            : Tracks.OfType<VideoTrack>().FirstOrDefault(each => !each.Locked && IsFree(each, clip.Range)) ?? AddTrack<VideoTrack>();
        track.Clips.Add(clip);
        track.Clips.SortByStart();
        return clip;
    }

    /// <summary>
    /// 자막 줄을 track의 range에 텍스트 클립으로 놓는다. (텍스트 목록 창과 자막 파일 가져오기에서 쓴다.)
    /// 스타일은 그 트랙의 첫 텍스트를 따르고, 첫 텍스트이면 화면 아래쪽에 자막 크기로 놓는다.
    /// 그 자리에 다른 클립이 있으면 놓지 않고 null을 돌려준다.
    /// </summary>
    public TextClip? AddSubtitle(VideoTrack track, TimeRange range, string text)
    {
        range = new TimeRange(SnapToFrame(range.Start < TimeSpan.Zero ? TimeSpan.Zero : range.Start), range.Duration);
        if (range.Duration <= TimeSpan.Zero || !IsFree(track, range)) return null;

        var first = track.Clips.OfType<TextClip>().FirstOrDefault();
        var clip = new TextClip
        {
            Text = text,
            Range = range,
            Style = first?.Style.Clone() ?? new TextStyle { Size = 64, Y = Preset.Height * 0.36 },
            Effects = first?.Effects.Select(effect => effect with { }).ToList() ?? [],
        };
        track.Clips.Add(clip);
        track.Clips.SortByStart();
        return clip;
    }

    private T FirstOrNew<T>() where T : Track, new() =>
        Tracks.OfType<T>().FirstOrDefault(track => !track.Locked) ?? AddTrack<T>();

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
