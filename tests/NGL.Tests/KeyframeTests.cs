using NGL.Audio;
using NGL.Commands;
using NGL.Core;
using NGL.IO;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

public class KeyframeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ngvid-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1e-9, expected + 1e-9);

    [Fact]
    public void 키프레임_사이는_잇고_바깥은_가장_가까운_값을_유지한다()
    {
        // 타임라인 10초에 놓인 클립. 키프레임은 클립 시작 기준 1초(값 100)와 3초(값 300).
        var clip = new VideoClip { Range = new TimeRange(Sec(10), Sec(5)), X = 7 };
        Near(7, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(12))); // 키프레임이 없으면 고정된 값

        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(3), 300));
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(1), 100)); // 나중에 찍어도 시간순으로 정리된다

        Near(100, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(10)));   // 첫 키프레임 전
        Near(100, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(11)));
        Near(150, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(11.5)));
        Near(200, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(12)));
        Near(300, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(13)));
        Near(300, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(14.9))); // 마지막 키프레임 뒤
        Near(7, clip.ValueAt(AnimatedProperty.Y, 7, Sec(12)));          // 다른 속성은 그대로

        // 같은 시각에 다시 찍으면 바뀐다.
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(3), 500));
        Assert.Equal(2, clip.Keyframes[AnimatedProperty.X].Count);
        Near(300, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(12)));

        // 지우면 남은 것으로 계산하고, 다 지우면 다시 고정된 값이다.
        Assert.True(clip.RemoveKeyframe(AnimatedProperty.X, Sec(1)));
        Assert.False(clip.RemoveKeyframe(AnimatedProperty.X, Sec(1)));
        Near(500, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(10)));
        Assert.True(clip.RemoveKeyframe(AnimatedProperty.X, Sec(3)));
        Assert.Empty(clip.Keyframes);
        Near(7, clip.ValueAt(AnimatedProperty.X, clip.X, Sec(12)));
    }

    [Theory]
    [InlineData(Easing.Linear, 0.25, 0.5, 0.75)]
    [InlineData(Easing.EaseIn, 0.0625, 0.25, 0.5625)]     // 천천히 시작
    [InlineData(Easing.EaseOut, 0.4375, 0.75, 0.9375)]    // 천천히 끝
    [InlineData(Easing.EaseInOut, 0.125, 0.5, 0.875)]     // 양쪽 다 천천히
    public void 이징은_앞_키프레임의_것을_따른다(Easing easing, double quarter, double half, double threeQuarters)
    {
        var keys = new List<Keyframe> { new(Sec(0), 0, easing), new(Sec(4), 1, Easing.EaseIn), new(Sec(8), 3) };

        Near(0, Animation.Evaluate(keys, Sec(0)));
        Near(quarter, Animation.Evaluate(keys, Sec(1)));
        Near(half, Animation.Evaluate(keys, Sec(2)));
        Near(threeQuarters, Animation.Evaluate(keys, Sec(3)));
        Near(1, Animation.Evaluate(keys, Sec(4)));
        Near(1.5, Animation.Evaluate(keys, Sec(6))); // 다음 구간은 EaseIn: 1 + 2 × 0.25
    }

    [Fact]
    public void 화면은_키프레임의_위치와_불투명도로_그려진다()
    {
        // 빨간 화면이 1초 동안 왼쪽 화면 밖(-1920)에서 제자리로 들어오고, 불투명도는 0에서 1로.
        var sequence = new Sequence();
        var clip = new ImageClip { Range = new TimeRange(Sec(0), Sec(2)) };
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(0), -1920));
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(1), 0));
        clip.SetKeyframe(AnimatedProperty.Opacity, new Keyframe(Sec(0), 0));
        clip.SetKeyframe(AnimatedProperty.Opacity, new Keyframe(Sec(1), 1));
        sequence.AddTrack<VideoTrack>().Clips.Add(clip);

        using var surface = SKSurface.Create(new SKImageInfo(16, 9));
        surface.Canvas.Clear(SKColors.Red);
        using var red = surface.Snapshot();

        SKBitmap At(double seconds)
        {
            var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
            using var canvas = new SKCanvas(bitmap);
            new SkiaCompositor().Render(canvas, sequence, Sec(seconds), _ => red);
            return bitmap;
        }

        using var start = At(0);
        Assert.Equal(SKColors.Black, start.GetPixel(100, 540));

        using var middle = At(0.5); // 절반 들어옴(왼쪽 960픽셀), 불투명도 0.5
        Assert.InRange(middle.GetPixel(480, 540).Red, 126, 129);
        Assert.Equal(SKColors.Black, middle.GetPixel(1440, 540));

        using var end = At(1.5);
        Assert.Equal(SKColors.Red, end.GetPixel(1440, 540));
    }

    [Fact]
    public void 볼륨_키프레임이_소리_크기에_반영된다()
    {
        var clip = new AudioClip { Range = new TimeRange(Sec(5), Sec(10)), Volume = 1 };
        clip.SetKeyframe(AnimatedProperty.Volume, new Keyframe(Sec(0), 0.2));
        clip.SetKeyframe(AnimatedProperty.Volume, new Keyframe(Sec(4), 1.0));

        Near(0.2, AudioMixer.ClipGain(clip, Sec(5)));
        Near(0.6, AudioMixer.ClipGain(clip, Sec(7)));
        Near(1.0, AudioMixer.ClipGain(clip, Sec(12)));
    }

    [Fact]
    public void 분할하거나_앞을_트리밍해도_움직임은_그대로_이어진다()
    {
        var project = new Project();
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(10) };
        project.Assets.Add(asset);
        var clip = (VideoClip)project.Sequence.Add(asset)[0];
        clip.SetKeyframe(AnimatedProperty.Scale, new Keyframe(Sec(0), 1));
        clip.SetKeyframe(AnimatedProperty.Scale, new Keyframe(Sec(10), 2));

        // 4초에서 분할: 뒤 조각은 4초 시점의 값(1.4)에서 이어진다.
        SequenceEdits.Split(project.Sequence, [clip], Sec(4));
        var tail = project.Sequence.Tracks[0].Clips[1];
        Near(1.2, clip.ValueAt(AnimatedProperty.Scale, 1, Sec(2)));
        Near(1.4, tail.ValueAt(AnimatedProperty.Scale, 1, Sec(4)));
        Near(1.7, tail.ValueAt(AnimatedProperty.Scale, 1, Sec(7)));

        // 뒤 조각의 앞 1초를 잘라내도 7초 시점의 값은 같다.
        SequenceEdits.TrimStart(project, tail, Sec(5));
        Near(1.5, tail.ValueAt(AnimatedProperty.Scale, 1, Sec(5)));
        Near(1.7, tail.ValueAt(AnimatedProperty.Scale, 1, Sec(7)));
    }

    [Fact]
    public void 키프레임이_저장되고_실행_취소로_되돌아온다()
    {
        var project = new Project();
        var clip = new VideoClip { Range = new TimeRange(Sec(0), Sec(5)) };
        project.Sequence.AddTrack<VideoTrack>().Clips.Add(clip);
        clip.SetKeyframe(AnimatedProperty.Rotation, new Keyframe(Sec(0), 0, Easing.EaseInOut));
        clip.SetKeyframe(AnimatedProperty.Rotation, new Keyframe(Sec(2.5), 360));

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Sequence.Tracks[0].Clips[0];
        Assert.Equal(clip.Keyframes[AnimatedProperty.Rotation], loaded.Keyframes[AnimatedProperty.Rotation]);
        Assert.Single(loaded.Keyframes);

        var history = new CommandHistory();
        history.Execute(new SequenceEditCommand("키프레임 삭제", project.Sequence,
            () => project.Sequence.Tracks[0].Clips[0].Keyframes.Clear()));
        Assert.Empty(project.Sequence.Tracks[0].Clips[0].Keyframes);
        history.Undo();
        Assert.Equal(2, project.Sequence.Tracks[0].Clips[0].Keyframes[AnimatedProperty.Rotation].Count);
    }

    [Fact]
    public void 프리셋은_클립의_움직임을_담아_맞는_종류의_클립에_입힌다()
    {
        var sequence = new Sequence();
        var pair = sequence.Add(new MediaAsset { Kind = MediaKind.Video, Duration = Sec(5), HasAudio = true });
        var (video, audio) = (pair[0], pair[1]);

        var source = new VideoClip();
        source.SetKeyframe(AnimatedProperty.Scale, new Keyframe(Sec(0), 0.5, Easing.EaseOut));
        source.SetKeyframe(AnimatedProperty.Scale, new Keyframe(Sec(0.5), 1));
        var preset = SequenceEdits.ToPreset(source, "확대 등장");
        preset.Keyframes[AnimatedProperty.Volume] = [new Keyframe(Sec(0), 0), new Keyframe(Sec(1), 1)];

        video.SetKeyframe(AnimatedProperty.Scale, new Keyframe(Sec(3), 2)); // 기존 것은 바뀐다
        video.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(1), 50));    // 프리셋에 없는 속성은 남는다
        Assert.True(SequenceEdits.ApplyPreset(sequence, pair, preset));

        // 영상에는 크기만, 소리에는 볼륨만 들어간다.
        Assert.Equal(source.Keyframes[AnimatedProperty.Scale], video.Keyframes[AnimatedProperty.Scale]);
        Assert.Equal([AnimatedProperty.X, AnimatedProperty.Scale], video.Keyframes.Keys.Order());
        Assert.Equal([AnimatedProperty.Volume], audio.Keyframes.Keys);

        // 프리셋과 클립은 목록을 공유하지 않는다.
        video.SetKeyframe(AnimatedProperty.Scale, new Keyframe(Sec(2), 3));
        Assert.Equal(2, preset.Keyframes[AnimatedProperty.Scale].Count);

        Assert.False(SequenceEdits.ApplyPreset(sequence, pair, new AnimationPreset("빈 프리셋", [])));
    }

    [Fact]
    public void 프리셋_목록을_저장하고_읽는다()
    {
        var path = Path.Combine(_folder, "presets.json");

        // 파일이 없으면 기본 프리셋
        var presets = AnimationPresetStore.Load(path);
        Assert.Equal(["페이드 인", "확대 등장", "왼쪽에서 등장"], presets.Select(preset => preset.Name));

        presets.Add(new AnimationPreset("내 움직임", new() { [AnimatedProperty.Y] = [new(Sec(0), -100, Easing.EaseIn), new(Sec(1), 0)] }));
        presets.RemoveAt(0);
        AnimationPresetStore.Save(presets, path);

        var loaded = AnimationPresetStore.Load(path);
        Assert.Equal(["확대 등장", "왼쪽에서 등장", "내 움직임"], loaded.Select(preset => preset.Name));
        Assert.Equal(presets[2].Keyframes[AnimatedProperty.Y], loaded[2].Keyframes[AnimatedProperty.Y]);

        File.WriteAllText(path, "{ 깨진 파일");
        Assert.Equal(3, AnimationPresetStore.Load(path).Count);
    }
}
