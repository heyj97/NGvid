using NGL.Core;
using NGL.IO;

namespace NGL.Commands;

/// <summary>복사해 둔 클립 하나.</summary>
/// <param name="Snapshot">클립의 내용.</param>
/// <param name="TrackId">원래 있던 트랙.</param>
/// <param name="TrackType">원래 있던 트랙의 종류.</param>
/// <param name="Offset">복사한 클립들 중 가장 앞선 것으로부터의 거리.</param>
public sealed record ClipCopy(string Snapshot, Guid TrackId, Type TrackType, TimeSpan Offset);

/// <summary>
/// 컷편집 동작들. 모두 시퀀스를 직접 바꾸고, 바꾼 것이 있으면 true를 돌려준다.
/// 실행 취소는 호출하는 쪽이 SequenceEditCommand로 감싸서 얻는다. 잠긴 트랙의 클립은 건드리지 않는다.
/// </summary>
public static class SequenceEdits
{
    /// <summary>주어진 클립들에 연결된 클립(영상의 소리)과 같은 그룹의 클립을 더한 것.</summary>
    public static HashSet<Clip> WithRelated(Sequence sequence, IEnumerable<Clip> clips)
    {
        var all = sequence.Tracks.SelectMany(track => track.Clips).ToList();
        var result = new HashSet<Clip>(clips);
        // 연결과 그룹이 꼬리를 물 수 있으므로 더 늘지 않을 때까지 반복한다.
        for (var count = -1; count != result.Count;)
        {
            count = result.Count;
            var links = result.Select(clip => clip.LinkId).Where(id => id is not null).ToHashSet();
            var groups = result.Select(clip => clip.GroupId).Where(id => id is not null).ToHashSet();
            result.UnionWith(all.Where(clip => links.Contains(clip.LinkId) || groups.Contains(clip.GroupId)));
        }

        return result;
    }

    /// <summary>잠긴 트랙의 클립을 뺀다.</summary>
    private static List<Clip> Editable(Sequence sequence, IEnumerable<Clip> clips) =>
        clips.Where(clip => sequence.TrackOf(clip) is { Locked: false }).ToList();

    // ───── 분할 ─────

    /// <summary>주어진 클립들 중 time이 가운데를 지나는 것을 둘로 자른다.</summary>
    public static bool Split(Sequence sequence, IEnumerable<Clip> clips, TimeSpan time)
    {
        time = sequence.SnapToFrame(time);
        var newLinks = new Dictionary<Guid, Guid>(); // 연결된 클립들의 뒤쪽 조각끼리 새로 연결한다
        var changed = false;

        foreach (var clip in Editable(sequence, clips))
        {
            if (time <= clip.Range.Start || time >= clip.Range.End) continue;

            var track = sequence.TrackOf(clip)!;
            var head = time - clip.Range.Start;
            var tail = SequenceSnapshot.Clone(clip);
            tail.Range = new TimeRange(time, clip.Range.End - time);
            if (tail is MediaClip media) media.SourceStart += head;
            tail.ShiftKeyframes(-head); // 뒤 조각의 움직임이 자르기 전과 똑같이 이어지게
            if (clip.LinkId is { } link)
            {
                if (!newLinks.TryGetValue(link, out var newLink)) newLinks[link] = newLink = Guid.NewGuid();
                tail.LinkId = newLink;
            }

            clip.Range = new TimeRange(clip.Range.Start, head);
            track.Clips.Insert(track.Clips.IndexOf(clip) + 1, tail);
            changed = true;
        }

        return changed;
    }

    // ───── 삭제 ─────

    /// <summary>클립을 지운다. ripple이면 같은 트랙의 뒤쪽 클립을 지운 길이만큼 당겨 빈 구간을 없앤다.</summary>
    public static bool Delete(Sequence sequence, IEnumerable<Clip> clips, bool ripple)
    {
        var doomed = Editable(sequence, clips);
        // 뒤쪽부터 지워야 앞쪽 클립을 지울 때 당겨지는 양이 섞이지 않는다.
        foreach (var clip in doomed.OrderByDescending(clip => clip.Range.Start))
        {
            var track = sequence.TrackOf(clip)!;
            track.Clips.Remove(clip);
            if (!ripple) continue;

            foreach (var later in track.Clips.Where(other => other.Range.Start >= clip.Range.End))
                later.Range = new TimeRange(later.Range.Start - clip.Range.Duration, later.Range.Duration);
        }

        return doomed.Count > 0;
    }

    // ───── 이동 ─────

    /// <summary>
    /// 클립들을 함께 옮긴다. anchor(마우스로 잡은 클립)가 newStart에 오도록 모두 같은 거리만큼 움직이고,
    /// newTrack이 anchor와 같은 종류의 다른 트랙이면 anchor와 같은 트랙에 있던 클립들이 그 트랙으로 간다.
    /// 옮길 자리에 다른 클립이 있으면 끼워 넣는다: anchor가 그 클립의 앞쪽 절반에 걸치면 그 클립 앞에,
    /// 뒤쪽 절반이면 그 클립 뒤에 놓고, 그 뒤의 클립들을 옮긴 길이만큼 뒤로 민다.
    /// </summary>
    public static bool Move(Sequence sequence, IEnumerable<Clip> clips, Clip anchor, TimeSpan newStart, Track? newTrack = null)
    {
        var moving = Editable(sequence, clips);
        if (!moving.Contains(anchor)) return false;

        var anchorTrack = sequence.TrackOf(anchor)!;
        if (newTrack is null || newTrack.Locked || newTrack.GetType() != anchorTrack.GetType()) newTrack = anchorTrack;
        Track TargetOf(Clip clip) => sequence.TrackOf(clip) == anchorTrack ? newTrack : sequence.TrackOf(clip)!;

        // 맨 앞 클립이 0초 앞으로 나가지 않는 범위에서 움직인다.
        var earliest = moving.Min(clip => clip.Range.Start);
        TimeSpan Delta(TimeSpan anchorStart)
        {
            var delta = sequence.SnapToFrame(anchorStart < TimeSpan.Zero ? TimeSpan.Zero : anchorStart) - anchor.Range.Start;
            return earliest + delta < TimeSpan.Zero ? -earliest : delta;
        }

        var delta = Delta(newStart);
        var pushed = new Dictionary<Clip, TimeSpan>(); // 뒤로 밀리는 클립과 민 뒤의 시작 시각

        // 옮길 자리에 anchor와 겹치는 다른 클립이 있으면 끼워 넣기로 바꾼다.
        var anchorRange = new TimeRange(anchor.Range.Start + delta, anchor.Range.Duration);
        var blocker = newTrack.Clips.Where(clip => !moving.Contains(clip) && clip.Range.Overlaps(anchorRange))
            .OrderBy(clip => clip.Range.Start).FirstOrDefault();
        if (blocker is not null)
        {
            var middle = blocker.Range.Start + blocker.Range.Duration / 2;
            var insertAt = anchorRange.Start < middle ? blocker.Range.Start : blocker.Range.End;
            delta = Delta(insertAt);

            var blockStart = earliest + delta;
            var blockLength = moving.Max(clip => clip.Range.End) - earliest;

            // 옮긴 클립들이 놓이는 트랙에서 그 뒤의 클립을 모두 민다. 밀린 클립에 연결된 클립의 트랙도 함께 민다.
            var tracks = moving.Select(TargetOf).ToHashSet();
            for (var count = -1; count != tracks.Count;)
            {
                count = tracks.Count;
                foreach (var track in tracks.ToList())
                {
                    foreach (var clip in track.Clips.Where(clip => !moving.Contains(clip) && clip.Range.End > blockStart))
                    {
                        if (clip.Range.Start < blockStart) return false; // 끼워 넣을 자리를 가로지르는 클립이 있다
                        pushed[clip] = clip.Range.Start + blockLength;
                        foreach (var partner in WithLinks(sequence, clip)) tracks.Add(sequence.TrackOf(partner)!);
                    }
                }
            }
        }

        // 결과에 겹침이 없는지 먼저 확인하고, 없을 때만 실제로 바꾼다.
        var placements = moving.ToDictionary(clip => clip, clip => (Track: TargetOf(clip), Start: clip.Range.Start + delta));
        if (delta == TimeSpan.Zero && newTrack == anchorTrack) return false;
        foreach (var track in sequence.Tracks)
        {
            var ranges = track.Clips.Where(clip => !moving.Contains(clip))
                .Select(clip => new TimeRange(pushed.GetValueOrDefault(clip, clip.Range.Start), clip.Range.Duration))
                .Concat(placements.Where(placed => placed.Value.Track == track)
                    .Select(placed => new TimeRange(placed.Value.Start, placed.Key.Range.Duration)))
                .OrderBy(range => range.Start).ToList();
            for (var i = 1; i < ranges.Count; i++)
            {
                if (ranges[i - 1].End > ranges[i].Start) return false;
            }
        }

        foreach (var (clip, start) in pushed) clip.Range = new TimeRange(start, clip.Range.Duration);
        foreach (var (clip, (track, start)) in placements)
        {
            sequence.TrackOf(clip)!.Clips.Remove(clip);
            clip.Range = new TimeRange(start, clip.Range.Duration);
            track.Clips.Add(clip);
        }

        foreach (var track in sequence.Tracks) track.Clips.SortByStart();
        return true;
    }

    private static IEnumerable<Clip> WithLinks(Sequence sequence, Clip clip) => clip.LinkId is null
        ? [clip]
        : sequence.Tracks.SelectMany(track => track.Clips).Where(other => other.LinkId == clip.LinkId);

    // ───── 트리밍 ─────

    /// <summary>
    /// 클립의 앞쪽 끝을 newStart로 옮겨 길이를 바꾼다. 연결된 클립도 같은 만큼 바뀐다.
    /// 한 프레임보다 짧아지거나, 원본의 처음보다 앞으로 가거나, 앞 클립과 겹치는 곳까지는 가지 않는다.
    /// </summary>
    public static bool TrimStart(Project project, Clip clip, TimeSpan newStart)
    {
        var sequence = project.Sequence;
        var clips = Editable(sequence, WithLinks(sequence, clip));
        if (!clips.Contains(clip)) return false;

        var frame = sequence.TimeAt(1);
        var delta = sequence.SnapToFrame(newStart < TimeSpan.Zero ? TimeSpan.Zero : newStart) - clip.Range.Start;
        foreach (var each in clips)
        {
            var previousEnd = sequence.TrackOf(each)!.Clips.Where(other => other.Range.End <= each.Range.Start)
                .Select(other => other.Range.End).DefaultIfEmpty(TimeSpan.Zero).Max();
            var earliest = previousEnd - each.Range.Start;
            if (HasSourceTime(each)) earliest = Max(earliest, -((MediaClip)each).SourceStart);

            delta = Max(delta, earliest);
            delta = Min(delta, each.Range.Duration - frame);
        }

        if (delta == TimeSpan.Zero) return false;
        foreach (var each in clips)
        {
            each.Range = new TimeRange(each.Range.Start + delta, each.Range.Duration - delta);
            each.ShiftKeyframes(-delta); // 움직임은 화면 내용에 붙어 있게
            if (HasSourceTime(each)) ((MediaClip)each).SourceStart += delta;
        }

        return true;
    }

    /// <summary>
    /// 클립의 뒤쪽 끝을 newEnd로 옮겨 길이를 바꾼다. 연결된 클립도 같은 만큼 바뀐다.
    /// 한 프레임보다 짧아지거나, 원본의 끝을 넘거나, 뒤 클립과 겹치는 곳까지는 가지 않는다.
    /// </summary>
    public static bool TrimEnd(Project project, Clip clip, TimeSpan newEnd)
    {
        var sequence = project.Sequence;
        var clips = Editable(sequence, WithLinks(sequence, clip));
        if (!clips.Contains(clip)) return false;

        var frame = sequence.TimeAt(1);
        var delta = sequence.SnapToFrame(newEnd) - clip.Range.End;
        foreach (var each in clips)
        {
            var nextStart = sequence.TrackOf(each)!.Clips.Where(other => other.Range.Start >= each.Range.End)
                .Select(other => other.Range.Start).DefaultIfEmpty(TimeSpan.MaxValue).Min();
            var latest = nextStart - each.Range.End;
            if (HasSourceTime(each) && project.FindAsset(((MediaClip)each).AssetId) is { Duration.Ticks: > 0 } asset)
                latest = Min(latest, asset.Duration - ((MediaClip)each).SourceStart - each.Range.Duration);

            delta = Min(delta, latest);
            delta = Max(delta, frame - each.Range.Duration);
        }

        if (delta == TimeSpan.Zero) return false;
        foreach (var each in clips) each.Range = new TimeRange(each.Range.Start, each.Range.Duration + delta);
        return true;
    }

    /// <summary>영상·오디오 클립은 원본의 길이 안에서만 늘일 수 있다. 이미지·텍스트는 제한이 없다.</summary>
    private static bool HasSourceTime(Clip clip) => clip is VideoClip or AudioClip;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // ───── 그룹과 연결 ─────

    /// <summary>두 개 이상의 클립을 한 그룹으로 묶는다.</summary>
    public static bool Group(Sequence sequence, IEnumerable<Clip> clips) => Mark(Editable(sequence, clips), 2, (clip, id) => clip.GroupId = id);

    public static bool Ungroup(Sequence sequence, IEnumerable<Clip> clips) =>
        Mark(Editable(sequence, clips).Where(clip => clip.GroupId is not null).ToList(), 1, (clip, _) => clip.GroupId = null);

    /// <summary>두 개 이상의 클립을 연결한다(영상과 소리).</summary>
    public static bool Link(Sequence sequence, IEnumerable<Clip> clips) => Mark(Editable(sequence, clips), 2, (clip, id) => clip.LinkId = id);

    public static bool Unlink(Sequence sequence, IEnumerable<Clip> clips) =>
        Mark(Editable(sequence, clips).Where(clip => clip.LinkId is not null).ToList(), 1, (clip, _) => clip.LinkId = null);

    private static bool Mark(List<Clip> clips, int minimum, Action<Clip, Guid> set)
    {
        if (clips.Count < minimum) return false;
        var id = Guid.NewGuid();
        foreach (var clip in clips) set(clip, id);
        return true;
    }

    // ───── 복사와 붙여넣기 ─────

    public static IReadOnlyList<ClipCopy> Copy(Sequence sequence, IEnumerable<Clip> clips)
    {
        var list = clips.Where(clip => sequence.TrackOf(clip) is not null).ToList();
        if (list.Count == 0) return [];

        var earliest = list.Min(clip => clip.Range.Start);
        return list.Select(clip =>
        {
            var track = sequence.TrackOf(clip)!;
            return new ClipCopy(SequenceSnapshot.Take(clip), track.Id, track.GetType(), clip.Range.Start - earliest);
        }).ToList();
    }

    /// <summary>
    /// 복사해 둔 클립들을 at부터 붙여 넣는다. 원래 있던 트랙(없어졌거나 잠겼으면 같은 종류의 다른 트랙)에 놓고,
    /// 그 자리에 다른 클립이 있으면 그 트랙들의 맨 뒤에 놓는다. 붙여 넣은 클립들을 돌려준다.
    /// </summary>
    public static IReadOnlyList<Clip> Paste(Sequence sequence, IReadOnlyList<ClipCopy> copies, TimeSpan at)
    {
        if (copies.Count == 0) return [];

        // 붙여 넣은 클립끼리의 연결·그룹은 유지하되 원본과는 끊는다.
        var newIds = new Dictionary<Guid, Guid>();
        Guid? Renew(Guid? id) => id is { } old ? newIds.TryGetValue(old, out var renewed) ? renewed : newIds[old] = Guid.NewGuid() : null;

        var placed = copies.Select(copy =>
        {
            var clip = SequenceSnapshot.Create(copy.Snapshot);
            clip.LinkId = Renew(clip.LinkId);
            clip.GroupId = Renew(clip.GroupId);
            var track = sequence.Tracks.FirstOrDefault(track => track.Id == copy.TrackId && !track.Locked)
                ?? sequence.Tracks.FirstOrDefault(track => track.GetType() == copy.TrackType && !track.Locked)
                ?? NewTrack(sequence, copy.TrackType);
            return (Clip: clip, Track: track, copy.Offset);
        }).ToList();

        var start = sequence.SnapToFrame(at < TimeSpan.Zero ? TimeSpan.Zero : at);
        bool Fits() => placed.All(item => !item.Track.Clips.Any(other =>
            other.Range.Overlaps(new TimeRange(start + item.Offset, item.Clip.Range.Duration))));
        if (!Fits())
        {
            start = sequence.SnapToFrame(placed.SelectMany(item => item.Track.Clips).Max(clip => clip.Range.End));
        }

        foreach (var (clip, track, offset) in placed)
        {
            clip.Range = new TimeRange(start + offset, clip.Range.Duration);
            track.Clips.Add(clip);
            track.Clips.SortByStart();
        }

        return placed.Select(item => item.Clip).ToList();
    }

    /// <summary>
    /// 복사해 둔 클립의 속성(변형·볼륨·페이드 등)만 targets에 입힌다. 같은 종류의 클립끼리만 적용된다.
    /// </summary>
    public static bool PasteProperties(Sequence sequence, IReadOnlyList<ClipCopy> copies, IEnumerable<Clip> targets)
    {
        var changed = false;
        foreach (var target in Editable(sequence, targets))
        {
            var source = copies.FirstOrDefault(copy => SequenceSnapshot.Create(copy.Snapshot).GetType() == target.GetType());
            if (source is null) continue;

            var track = sequence.TrackOf(target)!;
            track.Clips[track.Clips.IndexOf(target)] = SequenceSnapshot.WithPropertiesOf(target, source.Snapshot);
            changed = true;
        }

        return changed;
    }

    // ───── 애니메이션 프리셋 ─────

    /// <summary>그 종류의 클립에 키프레임을 찍을 수 있는 속성인가.</summary>
    public static bool CanAnimate(Clip clip, AnimatedProperty property) => property switch
    {
        AnimatedProperty.Volume => clip is AudioClip,
        >= AnimatedProperty.MaskX => clip is VisualClip,
        _ => clip is VisualClip or TextualClip,
    };

    /// <summary>source의 글자 스타일을 같은 트랙의 모든 자막에 복사한다. 문구와 시간은 그대로다.</summary>
    public static bool ApplyStyleToTrack(Sequence sequence, TextualClip source)
    {
        if (sequence.TrackOf(source) is not { Locked: false } track) return false;
        foreach (var clip in track.Clips.OfType<TextualClip>().Where(clip => clip != source)) clip.Style = source.Style.Clone();
        return true;
    }

    /// <summary>클립의 움직임(모든 키프레임)을 프리셋으로 만든다.</summary>
    public static AnimationPreset ToPreset(Clip clip, string name) =>
        new(name, clip.Keyframes.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()));

    /// <summary>프리셋의 움직임을 클립들에 입힌다. 프리셋에 있는 속성의 기존 키프레임은 바뀐다.</summary>
    public static bool ApplyPreset(Sequence sequence, IEnumerable<Clip> clips, AnimationPreset preset)
    {
        var changed = false;
        foreach (var clip in Editable(sequence, clips))
        {
            foreach (var (property, keys) in preset.Keyframes.Where(pair => CanAnimate(clip, pair.Key) && pair.Value.Count > 0))
            {
                clip.Keyframes[property] = keys.OrderBy(key => key.Time).ToList();
                changed = true;
            }
        }

        return changed;
    }

    private static Track NewTrack(Sequence sequence, Type type) =>
        type == typeof(AudioTrack) ? sequence.AddTrack<AudioTrack>()
        : type == typeof(CaptionTrack) ? sequence.AddTrack<CaptionTrack>()
        : sequence.AddTrack<VideoTrack>();
}
