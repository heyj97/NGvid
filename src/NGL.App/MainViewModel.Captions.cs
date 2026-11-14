using NGL.Commands;
using NGL.Core;

namespace NGL.App;

/// <summary>자막 목록 창에서 쓰는 동작.</summary>
public partial class MainViewModel
{
    /// <summary>시퀀스가 바뀔 때마다(편집, 실행 취소) 알린다.</summary>
    public event Action? SequenceEdited;

    private CaptionClip? CaptionOf(Guid id) => AllClips.OfType<CaptionClip>().FirstOrDefault(clip => clip.Id == id);

    /// <summary>자막의 시간과 문구를 바꾼다. 끝이 시작보다 앞서거나 다른 자막과 겹치면 시간은 바꾸지 않는다.</summary>
    public void EditCaption(Guid id, TimeSpan start, TimeSpan end, string text)
    {
        if (Project is not { } project) return;
        Edit("자막 수정", () =>
        {
            if (CaptionOf(id) is not { } clip || project.Sequence.TrackOf(clip) is not { Locked: false } track) return;

            clip.Text = text;
            var range = end > start ? new TimeRange(project.Sequence.SnapToFrame(start), end - start) : clip.Range;
            if (track.Clips.Any(other => other != clip && other.Range.Overlaps(range))) return;
            clip.Range = range;
            track.Clips.SortByStart();
        });
    }

    /// <summary>트랙의 마지막 자막 뒤(재생 위치가 더 뒤면 재생 위치)에 2초짜리 자막을 놓는다.</summary>
    public void AddCaptionAtEnd(CaptionTrack track)
    {
        if (Project is not { } project) return;
        var end = track.Clips.Select(clip => clip.Range.End).DefaultIfEmpty(TimeSpan.Zero).Max();
        var range = new TimeRange(Position > end ? Position : end, TimeSpan.FromSeconds(2));
        Edit("자막 추가", () => project.Sequence.AddCaption(track, range, "자막"));
    }

    public void DeleteCaption(Guid id)
    {
        if (Project is { } project && CaptionOf(id) is { } clip)
            Edit("자막 삭제", () => SequenceEdits.Delete(project.Sequence, [clip], ripple: false));
    }

    /// <summary>자막의 시작으로 가서 그 자막을 선택한다.</summary>
    public void GoToCaption(Guid id)
    {
        if (CaptionOf(id) is not { } clip || _engine is null) return;
        _engine.Seek(clip.Range.Start);
        Select(clip);
        Tick();
    }

    public void ApplyCaptionStyleToTrack(Guid id)
    {
        if (Project is { } project && CaptionOf(id) is { } clip)
            Edit("모든 자막에 스타일 적용", () => SequenceEdits.ApplyStyleToTrack(project.Sequence, clip));
    }
}
