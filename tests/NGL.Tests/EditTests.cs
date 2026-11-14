using NGL.Commands;
using NGL.Core;

namespace NGL.Tests;

public class EditTests
{
    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TimeRange Range(double start, double duration) => new(Sec(start), Sec(duration));

    /// <summary>영상 트랙 하나에 길이·위치를 지정한 영상 클립들을 놓은 프로젝트. 원본은 10초짜리다.</summary>
    private static (Project Project, VideoTrack Track, List<VideoClip> Clips) VideoProject(params (double Start, double Duration)[] clips)
    {
        var project = new Project();
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(10) };
        project.Assets.Add(asset);
        var track = project.Sequence.AddTrack<VideoTrack>();
        var list = clips.Select(c => new VideoClip { AssetId = asset.Id, Range = Range(c.Start, c.Duration) }).ToList();
        foreach (var clip in list) track.Clips.Add(clip);
        return (project, track, list);
    }

    private static List<TimeRange> RangesOf(Track track) => track.Clips.Select(clip => clip.Range).ToList();

    // ───── 실행 취소 기록 ─────

    private sealed class Counter(List<string> log, string name) : IEditCommand
    {
        public string Name => name;
        public void Execute() => log.Add("+" + name);
        public void Undo() => log.Add("-" + name);
    }

    [Fact]
    public void 기록은_실행_취소와_다시_실행을_순서대로_한다()
    {
        var log = new List<string>();
        var history = new CommandHistory();
        Assert.False(history.CanUndo);
        Assert.False(history.Undo());
        Assert.False(history.Redo());

        history.Execute(new Counter(log, "a"));
        history.Execute(new Counter(log, "b"));
        Assert.Equal(("b", null), (history.UndoName, history.RedoName));

        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.False(history.Undo());
        Assert.Equal((null, "a"), (history.UndoName, history.RedoName));
        Assert.True(history.Redo());
        Assert.Equal(["+a", "+b", "-b", "-a", "+a"], log);

        // 새 편집을 하면 다시 실행할 것이 사라진다.
        history.Execute(new Counter(log, "c"));
        Assert.False(history.CanRedo);
        Assert.Equal("c", history.UndoName);

        history.Clear();
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void 기록은_정한_단계_수까지만_기억한다()
    {
        var log = new List<string>();
        var history = new CommandHistory();
        Assert.Equal(50, history.Limit);

        for (var i = 0; i < 60; i++) history.Execute(new Counter(log, i.ToString()));
        var undone = 0;
        while (history.Undo()) undone++;
        Assert.Equal(50, undone);
        Assert.Equal("-10", log[^1]); // 가장 오래된 10개는 버려졌다

        history.Limit = 500;
        Assert.Equal(100, history.Limit);
        history.Limit = 0;
        Assert.Equal(1, history.Limit);
    }

    [Fact]
    public void 묶음_명령은_한_번에_실행하고_거꾸로_취소한다()
    {
        var log = new List<string>();
        var history = new CommandHistory();

        history.Execute(new CompositeCommand("묶음", [new Counter(log, "a"), new Counter(log, "b")]));
        history.Undo();

        Assert.Equal(["+a", "+b", "-b", "-a"], log);
    }

    [Fact]
    public void 시퀀스_편집_명령은_어떤_변경이든_되돌린다()
    {
        var (project, track, clips) = VideoProject((0, 2), (2, 3));
        var sequence = project.Sequence;
        var history = new CommandHistory();

        var edit = new SequenceEditCommand("여러 변경", sequence, () =>
        {
            track.Clips.RemoveAt(0);
            track.Name = "바뀐 이름";
            sequence.AddTrack<AudioTrack>();
            sequence.Markers.Add(new Marker { Time = Sec(1), Name = "표시" });
            sequence.FrameRate = 60;
        });
        history.Execute(edit);
        Assert.True(edit.Changed);

        history.Undo();
        var restored = Assert.Single(sequence.Tracks);
        Assert.Equal("영상 1", restored.Name);
        Assert.Equal([clips[0].Id, clips[1].Id], restored.Clips.Select(clip => clip.Id)); // ID가 그대로다
        Assert.Equal([Range(0, 2), Range(2, 3)], RangesOf(restored));
        Assert.Empty(sequence.Markers);
        Assert.Equal(30, sequence.FrameRate);

        history.Redo();
        Assert.Equal(2, sequence.Tracks.Count);
        Assert.Equal("바뀐 이름", sequence.Tracks[0].Name);
        Assert.Equal([Range(2, 3)], RangesOf(sequence.Tracks[0]));
        Assert.Equal("표시", Assert.Single(sequence.Markers).Name);
        Assert.Equal(60, sequence.FrameRate);

        // 아무것도 바꾸지 않은 편집은 바뀐 것이 없다고 알려준다.
        var nothing = new SequenceEditCommand("없음", sequence, () => { });
        nothing.Execute();
        Assert.False(nothing.Changed);
    }

    // ───── 분할 ─────

    [Fact]
    public void 분할은_클립을_둘로_나누고_뒤_조각은_원본의_이어지는_곳부터_쓴다()
    {
        var (project, track, clips) = VideoProject((1, 4), (6, 2));
        clips[0].SourceStart = Sec(2);

        Assert.True(SequenceEdits.Split(project.Sequence, track.Clips.ToList(), Sec(2.5)));

        Assert.Equal([Range(1, 1.5), Range(2.5, 2.5), Range(6, 2)], RangesOf(track));
        var tail = Assert.IsType<VideoClip>(track.Clips[1]);
        Assert.Same(clips[0], track.Clips[0]);
        Assert.NotEqual(clips[0].Id, tail.Id);
        Assert.Equal(clips[0].AssetId, tail.AssetId);
        Assert.Equal(Sec(2), clips[0].SourceStart);
        Assert.Equal(Sec(3.5), tail.SourceStart);

        // 클립의 끝이나 밖에서는 나누지 않는다.
        Assert.False(SequenceEdits.Split(project.Sequence, track.Clips.ToList(), Sec(1)));
        Assert.False(SequenceEdits.Split(project.Sequence, track.Clips.ToList(), Sec(5.5)));
        Assert.Equal(3, track.Clips.Count);
    }

    [Fact]
    public void 연결된_영상과_소리를_분할하면_뒤_조각끼리_새로_연결된다()
    {
        var sequence = new Sequence();
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(10), HasAudio = true };
        var pair = sequence.Add(asset);

        Assert.True(SequenceEdits.Split(sequence, pair, Sec(4)));

        var video = sequence.Tracks[0].Clips;
        var audio = sequence.Tracks[1].Clips;
        Assert.Equal([Range(0, 4), Range(4, 6)], RangesOf(sequence.Tracks[0]));
        Assert.Equal([Range(0, 4), Range(4, 6)], RangesOf(sequence.Tracks[1]));
        Assert.Equal(video[0].LinkId, audio[0].LinkId);
        Assert.Equal(video[1].LinkId, audio[1].LinkId);
        Assert.NotNull(video[1].LinkId);
        Assert.NotEqual(video[0].LinkId, video[1].LinkId);

        // 앞 조각을 고르면 그 소리만 따라온다.
        Assert.Equal([video[0], audio[0]], SequenceEdits.WithRelated(sequence, [video[0]]).OrderBy(c => c is AudioClip));
    }

    // ───── 삭제 ─────

    [Fact]
    public void 삭제는_빈_구간을_남기고_리플_삭제는_뒤를_당긴다()
    {
        var (project, track, clips) = VideoProject((0, 2), (2, 3), (5, 1), (8, 2));

        Assert.True(SequenceEdits.Delete(project.Sequence, [clips[1]], ripple: false));
        Assert.Equal([Range(0, 2), Range(5, 1), Range(8, 2)], RangesOf(track));

        // 두 개를 한꺼번에 리플 삭제: 0~2(2초)와 5~6(1초)이 빠지고 8초 클립이 3초 당겨진다.
        Assert.True(SequenceEdits.Delete(project.Sequence, [clips[0], clips[2]], ripple: true));
        Assert.Equal([Range(5, 2)], RangesOf(track));

        Assert.False(SequenceEdits.Delete(project.Sequence, [], ripple: true));
    }

    [Fact]
    public void 리플_삭제는_같은_트랙만_당긴다()
    {
        var (project, track, clips) = VideoProject((0, 2), (2, 3));
        var other = project.Sequence.AddTrack<VideoTrack>();
        other.Clips.Add(new VideoClip { Range = Range(4, 1) });

        SequenceEdits.Delete(project.Sequence, [clips[0]], ripple: true);

        Assert.Equal([Range(0, 3)], RangesOf(track));
        Assert.Equal([Range(4, 1)], RangesOf(other));
    }

    // ───── 이동 ─────

    [Fact]
    public void 빈_자리로는_그대로_옮기고_여러_클립은_간격을_유지한다()
    {
        var (project, track, clips) = VideoProject((0, 2), (3, 1), (10, 2));

        Assert.True(SequenceEdits.Move(project.Sequence, [clips[0], clips[1]], clips[1], Sec(7.01)));
        Assert.Equal([Range(4, 2), Range(7, 1), Range(10, 2)], RangesOf(track));

        // 맨 앞 클립이 0초 앞으로 나가지 않는 데까지만 간다.
        Assert.True(SequenceEdits.Move(project.Sequence, [clips[0], clips[1]], clips[1], Sec(1)));
        Assert.Equal([Range(0, 2), Range(3, 1), Range(10, 2)], RangesOf(track));

        // 제자리로는 옮긴 것이 아니다.
        Assert.False(SequenceEdits.Move(project.Sequence, [clips[2]], clips[2], Sec(10)));
    }

    [Fact]
    public void 다른_클립_위로_옮기면_앞쪽_절반은_그_앞에_뒤쪽_절반은_그_뒤에_끼워_넣는다()
    {
        // A(0~2) B(2~6) C(6~7), 옮기는 것은 C
        var (project, track, clips) = VideoProject((0, 2), (2, 4), (6, 1));
        var (a, b, c) = (clips[0], clips[1], clips[2]);

        // B의 뒤쪽 절반(4초 이후)에 놓으면 B 뒤, 즉 제자리 → 바뀌지 않는다.
        Assert.False(SequenceEdits.Move(project.Sequence, [c], c, Sec(4.5)));

        // B의 앞쪽 절반에 놓으면 B 앞(2초)에 끼워 넣고 B는 C의 길이만큼 뒤로 밀린다.
        Assert.True(SequenceEdits.Move(project.Sequence, [c], c, Sec(3)));
        Assert.Equal([a, c, b], track.Clips);
        Assert.Equal([Range(0, 2), Range(2, 1), Range(3, 4)], RangesOf(track));

        // A의 앞쪽 절반에 놓으면 맨 앞으로.
        Assert.True(SequenceEdits.Move(project.Sequence, [c], c, Sec(0.5)));
        Assert.Equal([c, a, b], track.Clips);
        Assert.Equal([Range(0, 1), Range(1, 2), Range(4, 4)], RangesOf(track));
    }

    [Fact]
    public void 같은_종류의_다른_트랙으로만_옮길_수_있다()
    {
        var (project, track, clips) = VideoProject((0, 2), (2, 2));
        var upper = project.Sequence.AddTrack<VideoTrack>();
        var audio = project.Sequence.AddTrack<AudioTrack>();
        var locked = project.Sequence.AddTrack<VideoTrack>();
        locked.Locked = true;

        // 오디오 트랙이나 잠긴 트랙을 주면 트랙은 그대로 두고 시간만 옮긴다.
        Assert.True(SequenceEdits.Move(project.Sequence, [clips[1]], clips[1], Sec(5), audio));
        Assert.True(SequenceEdits.Move(project.Sequence, [clips[1]], clips[1], Sec(6), locked));
        Assert.Equal([Range(0, 2), Range(6, 2)], RangesOf(track));
        Assert.Empty(audio.Clips);
        Assert.Empty(locked.Clips);

        Assert.True(SequenceEdits.Move(project.Sequence, [clips[1]], clips[1], Sec(1), upper));
        Assert.Equal([Range(0, 2)], RangesOf(track));
        Assert.Equal([Range(1, 2)], RangesOf(upper));
        Assert.Same(clips[1], Assert.Single(upper.Clips));
    }

    [Fact]
    public void 연결된_영상과_소리는_함께_움직이고_끼워_넣을_때_두_트랙을_함께_민다()
    {
        var sequence = new Sequence();
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2), HasAudio = true };
        var first = sequence.Add(asset);  // 0~2
        var second = sequence.Add(asset); // 2~4
        var third = sequence.Add(asset);  // 4~6

        // 세 번째(영상+소리)를 첫 번째 앞에 끼워 넣는다.
        var moving = SequenceEdits.WithRelated(sequence, [third[0]]);
        Assert.Equal(2, moving.Count);
        Assert.True(SequenceEdits.Move(sequence, moving, third[0], Sec(0.5)));

        foreach (var track in sequence.Tracks)
            Assert.Equal([Range(0, 2), Range(2, 2), Range(4, 2)], RangesOf(track));
        Assert.Equal([third[0], first[0], second[0]], sequence.Tracks[0].Clips);
        Assert.Equal([third[1], first[1], second[1]], sequence.Tracks[1].Clips);
    }

    [Fact]
    public void 잠긴_트랙의_클립은_편집하지_않는다()
    {
        var (project, track, clips) = VideoProject((0, 2), (2, 2));
        track.Locked = true;
        var sequence = project.Sequence;

        Assert.False(SequenceEdits.Split(sequence, clips, Sec(1)));
        Assert.False(SequenceEdits.Delete(sequence, clips, ripple: true));
        Assert.False(SequenceEdits.Move(sequence, clips, clips[0], Sec(5)));
        Assert.False(SequenceEdits.TrimEnd(project, clips[1], Sec(3)));
        Assert.False(SequenceEdits.Group(sequence, clips));
        Assert.Equal([Range(0, 2), Range(2, 2)], RangesOf(track));
    }

    // ───── 트리밍 ─────

    [Fact]
    public void 앞쪽_트리밍은_원본의_시작_위치를_함께_옮기고_한계를_넘지_않는다()
    {
        var (project, track, clips) = VideoProject((0, 2), (3, 4));
        var clip = clips[1];
        clip.SourceStart = Sec(0.5);

        Assert.True(SequenceEdits.TrimStart(project, clip, Sec(4)));
        Assert.Equal((Range(4, 3), Sec(1.5)), (clip.Range, clip.SourceStart));

        // 앞으로 늘릴 때: 원본의 처음(0.5초 여유 → 여기서는 1.5초)까지만.
        Assert.True(SequenceEdits.TrimStart(project, clip, Sec(0)));
        Assert.Equal((Range(2.5, 4.5), Sec(0)), (clip.Range, clip.SourceStart));

        // 한 프레임보다 짧게는 줄이지 않는다.
        Assert.True(SequenceEdits.TrimStart(project, clip, Sec(100)));
        Assert.Equal(project.Sequence.TimeAt(1), clip.Range.Duration);
        Assert.Equal(Sec(7), clip.Range.End);

        Assert.False(SequenceEdits.TrimStart(project, clip, clip.Range.Start));
    }

    [Fact]
    public void 앞쪽_트리밍은_앞_클립과_겹치지_않는다()
    {
        var (project, _, clips) = VideoProject((0, 2), (3, 4));
        clips[1].SourceStart = Sec(5);

        Assert.True(SequenceEdits.TrimStart(project, clips[1], Sec(0)));

        Assert.Equal((Range(2, 5), Sec(4)), (clips[1].Range, clips[1].SourceStart));
    }

    [Fact]
    public void 뒤쪽_트리밍은_원본의_끝과_뒤_클립을_넘지_않는다()
    {
        var (project, track, clips) = VideoProject((0, 2), (6, 1));
        var clip = clips[0];
        clip.SourceStart = Sec(7); // 원본 10초 중 7초부터 → 최대 3초

        Assert.True(SequenceEdits.TrimEnd(project, clip, Sec(1)));
        Assert.Equal(Range(0, 1), clip.Range);

        Assert.True(SequenceEdits.TrimEnd(project, clip, Sec(100)));
        Assert.Equal(Range(0, 3), clip.Range); // 원본의 끝

        clip.SourceStart = Sec(0);
        Assert.True(SequenceEdits.TrimEnd(project, clip, Sec(100)));
        Assert.Equal(Range(0, 6), clip.Range); // 뒤 클립의 시작

        Assert.True(SequenceEdits.TrimEnd(project, clip, Sec(-5)));
        Assert.Equal(project.Sequence.TimeAt(1), clip.Range.Duration); // 한 프레임

        // 이미지 클립은 원본 길이의 제한이 없다.
        var image = new ImageClip { Range = Range(10, 5) };
        track.Clips.Add(image);
        Assert.True(SequenceEdits.TrimEnd(project, image, Sec(100)));
        Assert.Equal(Range(10, 90), image.Range);
    }

    [Fact]
    public void 연결된_클립은_함께_트리밍된다()
    {
        var project = new Project();
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(10), HasAudio = true };
        project.Assets.Add(asset);
        var pair = project.Sequence.Add(asset);

        Assert.True(SequenceEdits.TrimStart(project, pair[1], Sec(2)));
        Assert.True(SequenceEdits.TrimEnd(project, pair[0], Sec(7)));

        Assert.All(pair, clip => Assert.Equal((Range(2, 5), Sec(2)), (clip.Range, ((MediaClip)clip).SourceStart)));
    }

    // ───── 그룹과 연결 ─────

    [Fact]
    public void 그룹으로_묶은_클립은_함께_선택되고_해제하면_따로_선택된다()
    {
        var (project, _, clips) = VideoProject((0, 1), (1, 1), (2, 1));
        var sequence = project.Sequence;

        Assert.False(SequenceEdits.Group(sequence, [clips[0]])); // 하나로는 그룹이 안 된다
        Assert.True(SequenceEdits.Group(sequence, [clips[0], clips[2]]));
        Assert.Equal([clips[0], clips[2]], SequenceEdits.WithRelated(sequence, [clips[2]]).OrderBy(c => c.Range.Start));
        Assert.Equal([clips[1]], SequenceEdits.WithRelated(sequence, [clips[1]]));

        Assert.True(SequenceEdits.Ungroup(sequence, [clips[0], clips[2]]));
        Assert.Equal([clips[2]], SequenceEdits.WithRelated(sequence, [clips[2]]));
        Assert.False(SequenceEdits.Ungroup(sequence, clips));
    }

    [Fact]
    public void 연결을_끊으면_영상과_소리를_따로_다룬다()
    {
        var sequence = new Sequence();
        var pair = sequence.Add(new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2), HasAudio = true });
        Assert.Equal(2, SequenceEdits.WithRelated(sequence, [pair[0]]).Count);

        Assert.True(SequenceEdits.Unlink(sequence, pair));
        Assert.Equal([pair[0]], SequenceEdits.WithRelated(sequence, [pair[0]]));
        Assert.True(SequenceEdits.Move(sequence, [pair[0]], pair[0], Sec(5)));
        Assert.Equal(Range(0, 2), pair[1].Range); // 소리는 제자리

        Assert.True(SequenceEdits.Link(sequence, pair));
        Assert.Equal(2, SequenceEdits.WithRelated(sequence, [pair[1]]).Count);
    }

    // ───── 복사와 붙여넣기 ─────

    [Fact]
    public void 붙여넣기는_간격을_유지한_새_클립을_만들고_자리가_없으면_맨_뒤에_놓는다()
    {
        var (project, track, clips) = VideoProject((1, 2), (4, 1));
        var sequence = project.Sequence;
        clips[0].SourceStart = Sec(3);
        SequenceEdits.Group(sequence, clips);

        var copies = SequenceEdits.Copy(sequence, clips);
        Assert.Equal([Sec(0), Sec(3)], copies.Select(copy => copy.Offset));

        // 빈 자리(10초)에 붙여넣기
        var pasted = SequenceEdits.Paste(sequence, copies, Sec(10));
        Assert.Equal([Range(1, 2), Range(4, 1), Range(10, 2), Range(13, 1)], RangesOf(track));
        Assert.Equal(Sec(3), Assert.IsType<VideoClip>(pasted[0]).SourceStart);
        Assert.Equal(clips[0].AssetId, ((VideoClip)pasted[0]).AssetId);
        Assert.Empty(pasted.Select(clip => clip.Id).Intersect(clips.Select(clip => clip.Id)));

        // 붙여 넣은 것끼리는 그룹이지만 원본과는 다른 그룹이다.
        Assert.Equal(pasted[0].GroupId, pasted[1].GroupId);
        Assert.NotEqual(clips[0].GroupId, pasted[0].GroupId);

        // 겹치는 자리(0초: 3~4초 조각이 4초 클립과... 0~2가 1~3과 겹침)면 트랙 맨 뒤(14초)로
        var again = SequenceEdits.Paste(sequence, copies, Sec(0));
        Assert.Equal([Range(14, 2), Range(17, 1)], again.Select(clip => clip.Range));
        Assert.Equal(6, track.Clips.Count);

        Assert.Empty(SequenceEdits.Paste(sequence, [], Sec(0)));
    }

    [Fact]
    public void 붙여넣을_트랙이_없어졌으면_같은_종류의_트랙에_놓는다()
    {
        var sequence = new Sequence();
        var pair = sequence.Add(new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2), HasAudio = true });
        var copies = SequenceEdits.Copy(sequence, pair);

        sequence.Tracks.Clear();
        var video = sequence.AddTrack<VideoTrack>();
        var pasted = SequenceEdits.Paste(sequence, copies, Sec(1));

        // 영상은 남아 있는 영상 트랙에, 소리는 새 오디오 트랙에. 둘은 서로 연결되어 있다.
        Assert.Same(pasted[0], Assert.Single(video.Clips));
        Assert.Same(pasted[1], Assert.Single(Assert.IsType<AudioTrack>(sequence.Tracks[1]).Clips));
        Assert.All(pasted, clip => Assert.Equal(Range(1, 2), clip.Range));
        Assert.Equal(pasted[0].LinkId, pasted[1].LinkId);
        Assert.NotEqual(pair[0].LinkId, pasted[0].LinkId);
    }
}
