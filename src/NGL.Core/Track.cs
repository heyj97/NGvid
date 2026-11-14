using System.Collections.ObjectModel;

namespace NGL.Core;

/// <summary>타임라인의 한 줄. 종류마다 올릴 수 있는 클립이 정해져 있다.</summary>
public abstract class Track
{
    protected Track() => Clips = new ClipCollection(this);

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4C8DFF";
    public ClipCollection Clips { get; }

    public abstract bool Accepts(Clip clip);
}

public sealed class VideoTrack : Track
{
    public override bool Accepts(Clip clip) => clip is VideoClip or ImageClip or TextClip;
}

public sealed class AudioTrack : Track
{
    public override bool Accepts(Clip clip) => clip is AudioClip;
}

public sealed class CaptionTrack : Track
{
    public override bool Accepts(Clip clip) => clip is CaptionClip;
}

/// <summary>트랙이 받아들이는 종류의 클립만 담는 목록.</summary>
public sealed class ClipCollection : Collection<Clip>
{
    private readonly Track _track;

    internal ClipCollection(Track track) => _track = track;

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
