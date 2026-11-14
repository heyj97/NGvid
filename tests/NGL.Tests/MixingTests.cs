using NGL.Audio;
using NGL.Core;
using NGL.IO;
using NGL.Media;
using NGL.Playback;

namespace NGL.Tests;

/// <summary>시험 영상의 소리: 1초까지 무음, 1초부터 2초까지 진폭 0.5의 440Hz.</summary>
public class MixingTests
{
    private const int Rate = IAudioDecoder.SampleRate;

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1e-9, expected + 1e-9);

    // ───── 크기 계산 ─────

    [Fact]
    public void 클립_볼륨과_페이드는_시각에_따라_곱해진다()
    {
        var clip = new AudioClip { Range = new TimeRange(Sec(10), Sec(10)), Volume = 0.8, FadeIn = Sec(2), FadeOut = Sec(4) };

        Near(0, AudioMixer.ClipGain(clip, Sec(10)));      // 페이드 인의 시작
        Near(0.4, AudioMixer.ClipGain(clip, Sec(11)));    // 절반
        Near(0.8, AudioMixer.ClipGain(clip, Sec(12)));    // 끝
        Near(0.8, AudioMixer.ClipGain(clip, Sec(15)));    // 가운데는 볼륨 그대로
        Near(0.8, AudioMixer.ClipGain(clip, Sec(16)));    // 페이드 아웃의 시작
        Near(0.2, AudioMixer.ClipGain(clip, Sec(19)));    // 3/4 지남
        Near(0, AudioMixer.ClipGain(clip, Sec(20)));

        // 페이드가 없으면 처음부터 끝까지 볼륨 그대로
        var plain = new AudioClip { Range = new TimeRange(Sec(0), Sec(5)), Volume = 1.5 };
        Near(1.5, AudioMixer.ClipGain(plain, Sec(0)));
        Near(1.5, AudioMixer.ClipGain(plain, Sec(4.999)));

        // 볼륨은 0~2로 제한된다.
        Assert.Equal(2, new AudioClip { Volume = 9 }.Volume);
        Assert.Equal(0, new AudioClip { Volume = -1 }.Volume);
        Assert.Equal(2, new AudioTrack { Volume = 9 }.Volume);
        Assert.Equal(1, new AudioTrack { DuckLevel = 9 }.DuckLevel);
    }

    [Fact]
    public void 자동_감쇠는_기준_트랙에_클립이_있는_동안_줄이고_앞뒤로_서서히_오간다()
    {
        var sequence = new Sequence();
        var narration = sequence.AddTrack<AudioTrack>();
        var bgm = sequence.AddTrack<AudioTrack>();
        narration.Clips.Add(new AudioClip { Range = new TimeRange(Sec(5), Sec(5)) });
        narration.Clips.Add(new AudioClip { Range = new TimeRange(Sec(10), Sec(2)) }); // 바로 이어지는 클립
        bgm.DuckUnder = narration.Id;
        bgm.DuckLevel = 0.3;

        Near(1, AudioMixer.DuckGain(sequence, bgm, Sec(0)));
        Near(1, AudioMixer.DuckGain(sequence, bgm, Sec(4.7)));    // 0.3초 전부터 줄기 시작
        Near(0.65, AudioMixer.DuckGain(sequence, bgm, Sec(4.85)));
        Near(0.3, AudioMixer.DuckGain(sequence, bgm, Sec(5)));
        Near(0.3, AudioMixer.DuckGain(sequence, bgm, Sec(10)));   // 이어지는 클립 사이에서 튀지 않는다
        Near(0.3, AudioMixer.DuckGain(sequence, bgm, Sec(11.99)));
        Near(0.65, AudioMixer.DuckGain(sequence, bgm, Sec(12.15)));
        Near(1, AudioMixer.DuckGain(sequence, bgm, Sec(12.3)));

        // 기준 트랙 자신은 줄지 않는다.
        Near(1, AudioMixer.DuckGain(sequence, narration, Sec(6)));

        // 기준 트랙을 음소거하거나 지우면 줄이지 않는다.
        narration.Muted = true;
        Near(1, AudioMixer.DuckGain(sequence, bgm, Sec(6)));
        narration.Muted = false;
        sequence.Tracks.Remove(narration);
        Near(1, AudioMixer.DuckGain(sequence, bgm, Sec(6)));
    }

    [Fact]
    public void 전체_크기는_트랙_볼륨과_클립_크기와_자동_감쇠의_곱이다()
    {
        var sequence = new Sequence();
        var narration = sequence.AddTrack<AudioTrack>();
        var bgm = sequence.AddTrack<AudioTrack>();
        narration.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(10)) });
        var clip = new AudioClip { Range = new TimeRange(Sec(0), Sec(10)), Volume = 0.5 };
        bgm.Clips.Add(clip);
        bgm.Volume = 1.6;
        bgm.DuckUnder = narration.Id;
        bgm.DuckLevel = 0.25;

        Near(1.6 * 0.5 * 0.25, AudioMixer.Gain(sequence, bgm, clip, Sec(5)));
    }

    [Fact]
    public void 샘플에_곱하는_크기는_처음과_끝_사이를_고르게_잇는다()
    {
        var samples = Enumerable.Repeat(1f, 8).ToArray(); // 4프레임 스테레오

        AudioMixer.ApplyGain(samples, 0, 1);

        Assert.Equal([0f, 0f, 0.25f, 0.25f, 0.5f, 0.5f, 0.75f, 0.75f], samples);
    }

    // ───── 실제 소리 ─────

    /// <summary>시퀀스의 0~2초 소리를 섞어 왼쪽 채널만 돌려준다.</summary>
    private static float[] Render(Sequence sequence, out AudioMeter meter)
    {
        using var reader = new SequenceAudioReader(sequence, _ => MediaTests.SamplePath);
        var buffer = new float[Rate * 2 * 2];
        reader.Read(buffer);
        meter = reader.Meter;
        return Enumerable.Range(0, buffer.Length / 2).Select(i => buffer[i * 2]).ToArray();
    }

    private static float PeakBetween(float[] samples, double from, double to) =>
        samples[(int)(from * Rate)..(int)(to * Rate)].Max(Math.Abs);

    [Fact]
    public void 클립_볼륨과_트랙_볼륨이_소리에_곱해진다()
    {
        var sequence = new Sequence();
        var track = sequence.AddTrack<AudioTrack>();
        var clip = new AudioClip { Range = new TimeRange(Sec(0), Sec(2)), Volume = 0.5 };
        track.Clips.Add(clip);

        Assert.InRange(PeakBetween(Render(sequence, out _), 1.1, 1.9), 0.23f, 0.27f); // 0.5 × 0.5

        track.Volume = 2;
        Assert.InRange(PeakBetween(Render(sequence, out _), 1.1, 1.9), 0.47f, 0.53f); // × 2
    }

    [Fact]
    public void 페이드_아웃은_끝으로_갈수록_작아진다()
    {
        var sequence = new Sequence();
        var track = sequence.AddTrack<AudioTrack>();
        track.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)), FadeOut = Sec(1) }); // 1~2초에 걸쳐 사라진다

        var samples = Render(sequence, out _);

        Assert.InRange(PeakBetween(samples, 1.05, 1.15), 0.40f, 0.48f);  // 약 0.9배
        Assert.InRange(PeakBetween(samples, 1.45, 1.55), 0.22f, 0.29f);  // 약 0.5배
        Assert.InRange(PeakBetween(samples, 1.90, 1.99), 0.0f, 0.06f);   // 거의 0
    }

    [Fact]
    public void 내레이션이_나오는_동안_배경음악이_자동으로_줄어든다()
    {
        var sequence = new Sequence();
        var narration = sequence.AddTrack<AudioTrack>();
        var bgm = sequence.AddTrack<AudioTrack>();
        // 배경음악: 원본의 소리 나는 1초를 0~1초에 놓는다. 내레이션: 0.5~1초에 원본의 무음 부분을 놓는다.
        bgm.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(1)), SourceStart = Sec(1) });
        narration.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0.5), Sec(0.5)) });
        bgm.DuckUnder = narration.Id;
        bgm.DuckLevel = 0.2;

        var samples = Render(sequence, out _);

        Assert.InRange(PeakBetween(samples, 0.02, 0.18), 0.46f, 0.54f);  // 원래 크기
        Assert.InRange(PeakBetween(samples, 0.55, 0.95), 0.08f, 0.12f);  // 0.5 × 0.2

        bgm.DuckUnder = null;
        Assert.InRange(PeakBetween(Render(sequence, out _), 0.55, 0.95), 0.46f, 0.54f);
    }

    [Fact]
    public void 미터는_가장_큰_크기를_알려주고_1을_넘는_소리는_잘라서_내보낸다()
    {
        var sequence = new Sequence();
        var track = sequence.AddTrack<AudioTrack>();
        track.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)), Volume = 2 });
        track.Volume = 2; // 0.5 × 2 × 2 = 2

        var samples = Render(sequence, out var meter);
        var (left, right) = meter.Read();

        Assert.InRange(left, 1.9f, 2.1f);
        Assert.Equal(left, right);
        Assert.Equal(1f, samples.Max());   // 잘린 소리
        Assert.Equal(-1f, samples.Min());
        Assert.Equal((0f, 0f), meter.Read()); // 읽으면 다시 0부터 잰다
    }

    [Fact]
    public void 볼륨과_페이드와_자동_감쇠_설정이_저장된다()
    {
        var project = new Project();
        var narration = project.Sequence.AddTrack<AudioTrack>();
        var bgm = project.Sequence.AddTrack<AudioTrack>();
        bgm.Volume = 0.7;
        bgm.DuckUnder = narration.Id;
        bgm.DuckLevel = 0.4;
        bgm.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(5)), Volume = 1.2, FadeIn = Sec(0.5), FadeOut = Sec(1.5) });

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        var track = Assert.IsType<AudioTrack>(loaded.Sequence.Tracks[1]);
        Assert.Equal((0.7, narration.Id, 0.4), (track.Volume, track.DuckUnder, track.DuckLevel));
        var clip = Assert.IsType<AudioClip>(track.Clips[0]);
        Assert.Equal((1.2, Sec(0.5), Sec(1.5)), (clip.Volume, clip.FadeIn, clip.FadeOut));
        Assert.Null(Assert.IsType<AudioTrack>(loaded.Sequence.Tracks[0]).DuckUnder);
    }
}
