using System.Collections.ObjectModel;

namespace NGL.Core;

/// <summary>타임라인의 한 줄. 종류마다 올릴 수 있는 클립이 정해져 있다.</summary>
public abstract class Track
{
    protected Track() => Clips = new ClipCollection(this);

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4C8DFF";

    /// <summary>잠근 트랙의 클립은 편집할 수 없다.</summary>
    public bool Locked { get; set; }

    /// <summary>숨긴 영상·자막 트랙은 화면에 그리지 않는다.</summary>
    public bool Hidden { get; set; }

    /// <summary>음소거한 오디오 트랙은 소리를 내지 않는다.</summary>
    public bool Muted { get; set; }

    /// <summary>솔로인 오디오 트랙이 하나라도 있으면 솔로인 트랙만 소리를 낸다.</summary>
    public bool Solo { get; set; }

    public ClipCollection Clips { get; }

    public abstract bool Accepts(Clip clip);
}

public sealed class VideoTrack : Track
{
    public override bool Accepts(Clip clip) => clip is VideoClip or ImageClip or TextClip;
}

public sealed class AudioTrack : Track
{
    private double _volume = 1;
    private double _duckLevel = 0.3;

    public AudioTrack() => Color = "#3FA87A";

    /// <summary>트랙 볼륨. 1이 원래 크기이고 0~2(200%)까지. 이 트랙의 모든 클립에 곱해진다.</summary>
    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 2);
    }

    /// <summary>
    /// 자동 감쇠의 기준 트랙(예: 내레이션). 그 트랙에 클립이 있는 동안 이 트랙(예: BGM)의 소리를 줄인다.
    /// null이면 줄이지 않는다.
    /// </summary>
    public Guid? DuckUnder { get; set; }

    /// <summary>자동 감쇠 중의 볼륨 비율(0~1). 0.3이면 30%로 줄인다.</summary>
    public double DuckLevel
    {
        get => _duckLevel;
        set => _duckLevel = Math.Clamp(value, 0, 1);
    }

    public override bool Accepts(Clip clip) => clip is AudioClip;
}

public sealed class CaptionTrack : Track
{
    public CaptionTrack() => Color = "#C79A2B";

    public override bool Accepts(Clip clip) => clip is CaptionClip;
}

/// <summary>트랙이 받아들이는 종류의 클립만 담는 목록.</summary>
public sealed class ClipCollection : Collection<Clip>
{
    private readonly Track _track;

    internal ClipCollection(Track track) => _track = track;

    /// <summary>시작 시각 순으로 정렬한다.</summary>
    public void SortByStart() => ((List<Clip>)Items).Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));

    protected override void InsertItem(int index, Clip item) => base.InsertItem(index, Checked(item));

    protected override void SetItem(int index, Clip item) => base.SetItem(index, Checked(item));

    private Clip Checked(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return _track.Accepts(clip)
            ? clip
            : throw new ArgumentException($"{_track.GetType().Name}에는 {clip.GetType().Name}을 올릴 수 없습니다.", nameof(clip));
    }
}
