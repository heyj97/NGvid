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
    public AudioTrack() => Color = "#3FA87A";

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
