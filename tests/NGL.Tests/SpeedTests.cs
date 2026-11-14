using NGL.Commands;
using NGL.Core;
using NGL.IO;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.Tests;

/// <summary>배속, 속도 램프, 정지 프레임. 시험 영상은 2초 30fps이고 소리는 1초부터 440Hz가 난다.</summary>
public class SpeedTests
{
    private const int Rate = IAudioDecoder.SampleRate;

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TimeRange Range(double start, double duration) => new(Sec(start), Sec(duration));

    private static void Near(double expected, double actual, double tolerance = 1e-9) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    // ───── 모델 ─────

    [Fact]
    public void 쌓은_값은_이징_곡선을_잘게_나눠_더한_것과_같다()
    {
        foreach (var easing in Enum.GetValues<Easing>())
        {
            List<Keyframe> keys = [new(Sec(1), 1, easing), new(Sec(3), 4, easing), new(Sec(4), 0.5)];
            foreach (var time in new[] { 0.5, 1, 1.7, 2.2, 3, 3.6, 4, 6 })
            {
                const int steps = 20000;
                var sum = 0.0;
                for (var i = 0; i < steps; i++) sum += Animation.Evaluate(keys, Sec(time * (i + 0.5) / steps)) * time / steps;

                Near(sum, Animation.Integrate(keys, Sec(time)), 1e-5);
            }
        }
    }

    [Fact]
    public void 원본이_흐른_시간은_배속을_곱한_것이고_키프레임이_있으면_그쪽을_따른다()
    {
        var clip = new VideoClip { Range = Range(5, 4), Speed = 2 };
        Assert.Equal(Sec(3), clip.SourceOffsetAt(Sec(1.5)));

        // 1초 동안 1배에서 3배로: 평균 2배
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(0), 1));
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(1), 3));
        Near(2, clip.SourceOffsetAt(Sec(1)).TotalSeconds);
        Near(2 + 3, clip.SourceOffsetAt(Sec(2)).TotalSeconds); // 마지막 키프레임 뒤는 3배 그대로

        Assert.Equal(4, new AudioClip { Speed = 99 }.Speed);
        Assert.Equal(0.25, new AudioClip { Speed = 0 }.Speed);
    }

    [Fact]
    public void 배속은_저장했다_읽어도_그대로다()
    {
        var clip = new VideoClip { Range = Range(0, 1), Speed = 1.5 };
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(0.5), 2, Easing.EaseIn));

        var copy = (VideoClip)SequenceSnapshot.Clone(clip);

        Assert.Equal(1.5, copy.Speed);
        Assert.Equal(clip.Keyframes[AnimatedProperty.Speed], copy.Keyframes[AnimatedProperty.Speed]);
    }

    // ───── 편집 ─────

    private static (Project Project, VideoTrack Track, VideoClip Clip) OneClip(double duration)
    {
        var project = new Project();
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(10) };
        project.Assets.Add(asset);
        var track = project.Sequence.AddTrack<VideoTrack>();
        var clip = new VideoClip { AssetId = asset.Id, Range = Range(0, duration) };
        track.Clips.Add(clip);
        return (project, track, clip);
    }

    [Fact]
    public void 속도를_바꾸면_원본_구간은_그대로이고_길이가_바뀐다()
    {
        var (project, track, clip) = OneClip(4);

        SequenceEdits.SetSpeed(project.Sequence, clip, 2);
        Assert.Equal(Range(0, 2), clip.Range);

        SequenceEdits.SetSpeed(project.Sequence, clip, 0.5);
        Assert.Equal(Range(0, 8), clip.Range);

        // 뒤 클립이 있으면 그 앞까지만 늘어난다.
        SequenceEdits.SetSpeed(project.Sequence, clip, 1);
        track.Clips.Add(new VideoClip { Range = Range(5, 1) });
        SequenceEdits.SetSpeed(project.Sequence, clip, 0.25);
        Assert.Equal(Range(0, 5), clip.Range);
    }

    [Fact]
    public void 빠른_클립을_자르면_뒤_조각은_원본의_그만큼_뒤에서_시작한다()
    {
        var (project, track, clip) = OneClip(4);
        clip.Speed = 2;
        clip.SourceStart = Sec(1);

        Assert.True(SequenceEdits.Split(project.Sequence, [clip], Sec(1.5)));

        var tail = (VideoClip)track.Clips[1];
        Assert.Equal(Sec(1 + 3), tail.SourceStart);
        Assert.Equal(2, tail.Speed);
    }

    [Fact]
    public void 속도_램프가_있는_클립을_잘라도_화면이_이어진다()
    {
        var (project, track, clip) = OneClip(4);
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(1), 1));
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(3), 3, Easing.EaseInOut));
        var before = clip.SourceOffsetAt(Sec(3.5));

        SequenceEdits.Split(project.Sequence, [clip], Sec(2));

        var tail = (VideoClip)track.Clips[1];
        Near(before.TotalSeconds, (tail.SourceStart + tail.SourceOffsetAt(Sec(1.5))).TotalSeconds, 1e-6);
    }

    [Fact]
    public void 트리밍은_배속만큼_원본을_쓴다()
    {
        var (project, _, clip) = OneClip(2);
        clip.Speed = 2;
        clip.SourceStart = Sec(2);

        // 뒤쪽: 원본 10초 중 2초부터 → 남은 8초를 2배로 틀면 4초까지
        Assert.True(SequenceEdits.TrimEnd(project, clip, Sec(100)));
        Assert.Equal(Range(0, 4), clip.Range);

        // 앞쪽을 1초 줄이면 원본은 2초 넘어간다.
        Assert.True(SequenceEdits.TrimStart(project, clip, Sec(1)));
        Assert.Equal(Sec(4), clip.SourceStart);

        // 앞으로 늘이면 원본의 처음(4초 ÷ 2배 = 2초)까지만 간다. 클립은 0초 앞으로 못 가므로 먼저 옮겨 둔다.
        clip.Range = Range(10, 3);
        Assert.True(SequenceEdits.TrimStart(project, clip, Sec(0)));
        Assert.Equal(Sec(8), clip.Range.Start);
        Assert.Equal(Sec(0), clip.SourceStart);
    }

    [Fact]
    public void 속도_키프레임은_영상과_오디오_클립에만_찍을_수_있다()
    {
        Assert.True(SequenceEdits.CanAnimate(new VideoClip(), AnimatedProperty.Speed));
        Assert.True(SequenceEdits.CanAnimate(new AudioClip(), AnimatedProperty.Speed));
        Assert.False(SequenceEdits.CanAnimate(new ImageClip(), AnimatedProperty.Speed));
        Assert.False(SequenceEdits.CanAnimate(new TextClip(), AnimatedProperty.Speed));
    }

    [Fact]
    public void 정지_프레임은_클립을_자르고_뒤를_밀고_멈춘_순간의_모습을_물려받는다()
    {
        var (project, track, clip) = OneClip(4);
        var sequence = project.Sequence;
        clip.Effects.Brightness = 0.3;
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(0), 0));
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(4), 400));
        var audioTrack = sequence.AddTrack<AudioTrack>();
        var sound = new AudioClip { Range = Range(0, 4), LinkId = clip.LinkId = Guid.NewGuid() };
        audioTrack.Clips.Add(sound);
        var music = new AudioClip { Range = Range(0.5, 10) }; // 가로지르는 클립은 그대로 둔다
        sequence.AddTrack<AudioTrack>().Clips.Add(music);
        var later = new VideoClip { Range = Range(6, 1) };
        track.Clips.Add(later);
        var stillAsset = Guid.NewGuid();

        var still = SequenceEdits.InsertFreezeFrame(sequence, clip, Sec(1), stillAsset, Sec(2));

        Assert.NotNull(still);
        Assert.Equal([Range(0, 1), Range(1, 2), Range(3, 3), Range(8, 1)], track.Clips.Select(each => each.Range));
        Assert.Same(still, track.Clips[1]);
        Assert.Equal(stillAsset, still.AssetId);
        Assert.Equal(100, still.X);            // 움직이던 값은 그 순간의 값으로
        Assert.Empty(still.Keyframes);
        Assert.Equal(0.3, still.Effects.Brightness);
        Assert.Null(still.LinkId);
        Assert.Equal([Range(0, 1), Range(3, 3)], audioTrack.Clips.Select(each => each.Range)); // 소리는 그동안 비어 있다
        Assert.Equal(Sec(1), ((VideoClip)track.Clips[2]).SourceStart);
        Assert.Equal(Range(0.5, 10), music.Range);

        // 클립 밖의 시각이면 아무것도 하지 않는다.
        Assert.Null(SequenceEdits.InsertFreezeFrame(sequence, clip, Sec(1), stillAsset, Sec(2)));
    }

    // ───── 실제 화면과 소리 ─────

    private static int FrameShownAt(VideoFrameSource frames, VideoClip clip, double seconds)
    {
        frames.BeginFrame();
        using var image = frames.GetImage(clip, Sec(seconds));
        using var bitmap = SKBitmap.FromImage(image);
        return (int)Math.Round(bitmap.GetPixel(160, 90).Green * 219.0 / 255.0 / 3);
    }

    [Fact]
    public void 배속과_속도_램프에_맞는_원본_프레임이_보인다()
    {
        using var frames = new VideoFrameSource(_ => MediaTests.SamplePath);
        var half = 0.5 / 30;

        var fast = new VideoClip { Range = Range(1, 1), Speed = 2 };
        Assert.Equal(0, FrameShownAt(frames, fast, 1 + half));
        Assert.Equal(20, FrameShownAt(frames, fast, 1 + 10 / 30.0 + half / 2));

        var slow = new VideoClip { Range = Range(0, 2), Speed = 0.5, SourceStart = Sec(1) };
        Assert.Equal(30 + 15, FrameShownAt(frames, slow, 1 + half));

        // 1초 동안 1배 → 3배: t초에 원본은 t + t² 초
        var ramp = new VideoClip { Range = Range(0, 1) };
        ramp.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(0), 1));
        ramp.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(1), 3));
        Assert.Equal((int)((0.5 + 0.25) * 30), FrameShownAt(frames, ramp, 0.5 + half / 4));
    }

    /// <summary>오디오 클립 하나의 0~2초 소리(왼쪽 채널)를 1/100초씩 읽는다.</summary>
    private static float[] Render(AudioClip clip)
    {
        var sequence = new Sequence();
        sequence.AddTrack<AudioTrack>().Clips.Add(clip);
        using var reader = new SequenceAudioReader(sequence, _ => MediaTests.SamplePath);
        var result = new List<float>();
        var buffer = new float[Rate / 100 * 2];
        for (var i = 0; i < 200; i++)
        {
            reader.Read(buffer);
            for (var frame = 0; frame < buffer.Length / 2; frame++) result.Add(buffer[frame * 2]);
        }

        return [.. result];
    }

    /// <summary>from~to초 사이의 주파수(0을 위로 지나는 횟수로 잰다).</summary>
    private static double FrequencyBetween(float[] samples, double from, double to)
    {
        var crossings = 0;
        for (var i = (int)(from * Rate) + 1; i < (int)(to * Rate); i++)
        {
            if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;
        }

        return crossings / (to - from);
    }

    [Theory]
    [InlineData(2, 0, 0.5)]      // 원본 1초의 소리가 0.5초에
    [InlineData(1.5, 0.25, 0.5)]
    [InlineData(0.5, 0.75, 0.5)]
    [InlineData(0.25, 0.9, 0.4)] // 필터 둘을 이은 경우
    [InlineData(4, 0, 0.25)]
    [InlineData(3, 0, 0.3333)]
    [InlineData(0.75, 0.25, 1)]
    [InlineData(0.4, 0.8, 0.5)]
    [InlineData(0.3, 0.85, 0.5)]
    public void 배속한_소리는_제때_시작하고_음높이는_그대로다(double speed, double sourceStart, double onset)
    {
        var samples = Render(new AudioClip { Range = Range(0, 2), Speed = speed, SourceStart = Sec(sourceStart) });

        // 필터가 소리의 시작을 창 하나만큼 번지게 하므로 넉넉히 본다.
        Assert.InRange(AudioDecoderTests.OnsetOf(samples) / (double)Rate, onset - 0.04, onset + 0.04);
        var sounding = Math.Min(2, onset + 1 / speed);
        Assert.InRange(FrequencyBetween(samples, onset + 0.1, sounding - 0.1), 430, 450);
        Assert.InRange(samples[(int)((onset + 0.1) * Rate)..(int)((sounding - 0.1) * Rate)].Max(), 0.4f, 0.6f);
    }

    [Fact]
    public void 빠르게_한_소리는_원본이_끝나면_조용하다()
    {
        var samples = Render(new AudioClip { Range = Range(0, 2), Speed = 2 });

        Assert.All(samples[(int)(1.1 * Rate)..], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void 속도_램프의_소리는_속도를_따라_빨라진다()
    {
        // 1초 동안 1배 → 3배: 원본 1초(소리의 시작)는 t + t² = 1인 0.618초에 온다.
        var clip = new AudioClip { Range = Range(0, 2) };
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(0), 1));
        clip.SetKeyframe(AnimatedProperty.Speed, new Keyframe(Sec(1), 3));

        var samples = Render(clip);

        Assert.InRange(AudioDecoderTests.OnsetOf(samples) / (double)Rate, 0.613, 0.623);
        // 0.618초에 속도는 약 2.24배, 원본 2초에 닿는 것은 t + t² = 2인 1초.
        Assert.InRange(FrequencyBetween(samples, 0.75, 0.85), 440 * 2.5, 440 * 2.7);
        Assert.All(samples[(int)(1.05 * Rate)..], sample => Assert.Equal(0, sample));
    }
}
