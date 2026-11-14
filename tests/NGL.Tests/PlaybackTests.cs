using System.Diagnostics;
using NGL.Audio;
using NGL.Core;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.Tests;

public class PlaybackTests
{
    private const int Rate = IAudioDecoder.SampleRate;
    private const int Tolerance = Rate * 5 / 1000; // 5ms

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>시간을 테스트가 직접 정하는 출력.</summary>
    private sealed class ManualOutput : IAudioOutput
    {
        public AudioCallback? Callback;
        public int StartCount;
        public TimeSpan Elapsed { get; set; }

        public void Start(AudioCallback read)
        {
            Callback = read;
            Elapsed = TimeSpan.Zero;
            Interlocked.Increment(ref StartCount);
        }

        public void Stop() => Callback = null;

        public void Dispose() { }

        /// <summary>장치가 가져갈 소리를 대신 가져와 왼쪽 채널만 돌려준다.</summary>
        public float[] Pull(double seconds)
        {
            var buffer = new float[(int)(seconds * Rate) * 2];
            Callback!(buffer);
            return Enumerable.Range(0, buffer.Length / 2).Select(i => buffer[i * 2]).ToArray();
        }
    }

    /// <summary>시험 영상(2초)의 영상과 소리를 0초부터 놓은 시퀀스.</summary>
    private static Sequence SampleSequence()
    {
        var sequence = new Sequence();
        var video = new VideoTrack();
        video.Clips.Add(new VideoClip { Range = new TimeRange(Sec(0), Sec(2)) });
        var audio = new AudioTrack();
        audio.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)) });
        sequence.Tracks.AddRange([video, audio]);
        return sequence;
    }

    private static PlaybackEngine Engine(Sequence sequence, IAudioOutput output) =>
        new(sequence, _ => MediaTests.SamplePath, output);

    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < Sec(5), "5초 안에 조건이 이루어지지 않았습니다.");
            Thread.Sleep(2);
        }
    }

    /// <summary>엔진이 지금 화면에 그리는 시험 영상의 프레임 번호. 영상이 없으면 -1.</summary>
    private static int ShownFrame(PlaybackEngine engine)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
        engine.Render(canvas);
        var pixel = bitmap.GetPixel(960, 540);
        return pixel == SKColors.Magenta ? -1 : (int)Math.Round(pixel.Green * 219.0 / 255.0 / 3);
    }

    [Fact]
    public void 처음에는_0초에_멈춰_있고_첫_프레임을_보여준다()
    {
        var sequence = SampleSequence();
        using var engine = Engine(sequence, new ManualOutput());

        Assert.False(engine.IsPlaying);
        Assert.Equal(TimeSpan.Zero, engine.Position);
        Assert.Equal(sequence.TimeAt(59), engine.LastFrameTime);
        WaitUntil(() => ShownFrame(engine) == 0);
    }

    [Fact]
    public void 재생_위치는_출력_장치의_시계를_따르고_일시정지하면_프레임에_맞춰_선다()
    {
        var sequence = SampleSequence();
        var output = new ManualOutput();
        using var engine = Engine(sequence, output);

        engine.Play();
        Assert.True(engine.IsPlaying);
        Assert.Equal(TimeSpan.Zero, engine.Position);

        output.Elapsed = Sec(0.51);
        Assert.Equal(Sec(0.51), engine.Position);
        WaitUntil(() => ShownFrame(engine) == 15);

        engine.Pause();
        Assert.False(engine.IsPlaying);
        Assert.Null(output.Callback);
        Assert.Equal(sequence.TimeAt(15), engine.Position);

        // 이어서 재생하면 멈춘 곳부터
        engine.Play();
        output.Elapsed = Sec(0.1);
        Assert.Equal(sequence.TimeAt(15) + Sec(0.1), engine.Position);
    }

    [Fact]
    public void 탐색과_프레임_이동은_정확한_프레임을_보여준다()
    {
        var sequence = SampleSequence();
        using var engine = Engine(sequence, new ManualOutput());

        engine.Seek(Sec(1.51)); // 가장 가까운 프레임은 45
        Assert.Equal(sequence.TimeAt(45), engine.Position);
        WaitUntil(() => ShownFrame(engine) == 45);

        engine.Step(1);
        WaitUntil(() => ShownFrame(engine) == 46);
        engine.Step(-2);
        WaitUntil(() => ShownFrame(engine) == 44);

        engine.Seek(Sec(100));
        Assert.Equal(sequence.TimeAt(59), engine.Position);
        WaitUntil(() => ShownFrame(engine) == 59);
        engine.Step(1); // 끝을 넘지 않는다
        Assert.Equal(sequence.TimeAt(59), engine.Position);

        engine.Seek(Sec(-3));
        Assert.Equal(TimeSpan.Zero, engine.Position);
        engine.Step(-1);
        Assert.Equal(TimeSpan.Zero, engine.Position);
        WaitUntil(() => ShownFrame(engine) == 0);
    }

    [Fact]
    public void 화질을_바꾸면_현재_프레임을_다시_준비하고_화면에는_같은_프레임이_꽉_차게_보인다()
    {
        using var engine = Engine(SampleSequence(), new ManualOutput());
        engine.Seek(Sec(1));
        WaitUntil(() => ShownFrame(engine) == 30);

        var prepared = 0;
        engine.FrameReady += () => Interlocked.Increment(ref prepared);
        engine.Quality = PreviewQuality.Quarter;

        WaitUntil(() => prepared == 1);
        Assert.Equal(PreviewQuality.Quarter, engine.Quality);
        Assert.Equal(30, ShownFrame(engine));
    }

    [Fact]
    public void 재생_중에_클립을_추가해도_안전하고_길이가_늘어난다()
    {
        var sequence = SampleSequence();
        var output = new ManualOutput();
        using var engine = Engine(sequence, output);
        engine.Play();

        // 디코딩·오디오 스레드가 도는 동안 클립을 계속 추가한다.
        var asset = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2), HasAudio = true };
        for (var i = 0; i < 50; i++)
        {
            engine.Edit(() => sequence.AddToEnd(asset));
            output.Callback?.Invoke(new float[960]);
            output.Elapsed += Sec(0.05);
        }

        Assert.True(engine.IsPlaying);
        Assert.Equal(sequence.TimeAt(60 * 51 - 1), engine.LastFrameTime);
        engine.Pause();
        engine.Seek(Sec(2) + Sec(1)); // 두 번째 클립의 30번 프레임
        WaitUntil(() => ShownFrame(engine) == 30);
    }

    [Fact]
    public void 끝에_닿으면_마지막_프레임에서_멈추고_다시_누르면_처음부터_재생한다()
    {
        var sequence = SampleSequence();
        var output = new ManualOutput();
        using var engine = Engine(sequence, output);
        var ended = 0;
        engine.Ended += () => Interlocked.Increment(ref ended);

        engine.Play();
        output.Elapsed = Sec(2.5);
        WaitUntil(() => !engine.IsPlaying);

        Assert.Equal(sequence.TimeAt(59), engine.Position);
        WaitUntil(() => ShownFrame(engine) == 59 && ended == 1);

        engine.Play();
        Assert.True(engine.IsPlaying);
        Assert.Equal(TimeSpan.Zero, engine.Position);
    }

    [Fact]
    public void 반복_구간의_끝에_닿으면_시작으로_돌아가_계속_재생한다()
    {
        var sequence = SampleSequence();
        var output = new ManualOutput();
        using var engine = Engine(sequence, output);
        engine.LoopRange = new TimeRange(Sec(0.5), Sec(0.5));

        engine.Seek(Sec(0.5));
        engine.Play();
        output.Elapsed = Sec(0.6); // 1.1초 → 구간 끝(1.0초)을 넘음
        WaitUntil(() => output.StartCount == 2);

        Assert.True(engine.IsPlaying);
        Assert.Equal(Sec(0.5), engine.Position);
        WaitUntil(() => ShownFrame(engine) == 15);

        // 반복을 끄면 끝까지 간다
        engine.LoopRange = null;
        output.Elapsed = Sec(0.6);
        Thread.Sleep(50);
        Assert.True(engine.IsPlaying);
        Assert.Equal(Sec(1.1), engine.Position);
    }

    [Fact]
    public void 소리는_재생을_시작한_위치부터_나온다()
    {
        var output = new ManualOutput();
        using var engine = Engine(SampleSequence(), output);

        // 소리는 1초에 시작한다. 0.9초에서 재생하면 0.1초 뒤에 들려야 한다.
        engine.Seek(Sec(0.9));
        engine.Play();
        var fromPause = output.Pull(0.3);
        Assert.InRange(AudioDecoderTests.OnsetOf(fromPause), Rate / 10 - Tolerance, Rate / 10 + Tolerance);

        // 재생 중에 0.7초로 옮기면 0.3초 뒤에 들려야 한다.
        engine.Seek(Sec(0.7));
        Assert.True(engine.IsPlaying);
        var afterSeek = output.Pull(0.5);
        Assert.InRange(AudioDecoderTests.OnsetOf(afterSeek), Rate * 3 / 10 - Tolerance, Rate * 3 / 10 + Tolerance);
    }

    [Fact]
    public void 오디오_클립의_위치와_트리밍대로_소리가_놓인다()
    {
        // 타임라인 2초에 놓고 원본의 0.5초부터 쓰는 클립 → 소리는 타임라인 2.5초에 시작한다.
        var sequence = new Sequence();
        var track = new AudioTrack();
        track.Clips.Add(new AudioClip { Range = new TimeRange(Sec(2), Sec(1)), SourceStart = Sec(0.5) });
        sequence.Tracks.Add(track);
        using var reader = new SequenceAudioReader(sequence, _ => MediaTests.SamplePath);

        var whole = new float[Rate * 4 * 2];
        reader.Read(whole);
        var left = Enumerable.Range(0, whole.Length / 2).Select(i => whole[i * 2]).ToArray();

        Assert.InRange(AudioDecoderTests.OnsetOf(left), Rate * 5 / 2 - Tolerance, Rate * 5 / 2 + Tolerance);
        Assert.All(left[..(Rate * 2)], sample => Assert.Equal(0, sample)); // 클립 앞은 완전한 무음
        Assert.All(left[(Rate * 3)..], sample => Assert.Equal(0, sample)); // 클립 뒤도
        Assert.InRange(left.Max(), 0.4f, 0.6f);

        // 작게 나눠 읽어도 한 번에 읽은 것과 똑같다.
        // (44.1kHz 원본을 48kHz로 바꿀 때 디코딩을 시작하는 패킷에 따라 0.5샘플 이내로 달라질 수 있어서,
        //  같은 조건이 되도록 한 번에 읽는 쪽도 다시 읽어서 비교한다.)
        reader.Seek(TimeSpan.Zero);
        reader.Read(whole);
        reader.Seek(TimeSpan.Zero);
        var chunk = new float[480 * 2];
        for (var offset = 0; offset < whole.Length; offset += chunk.Length)
        {
            reader.Read(chunk);
            Assert.True(chunk.AsSpan().SequenceEqual(whole.AsSpan(offset, chunk.Length)), $"{offset / 2}번째 샘플부터 다릅니다.");
        }

        // 중간으로 탐색해도 같은 위치에 놓인다.
        reader.Seek(Sec(2.4));
        var part = new float[Rate * 2];
        reader.Read(part);
        var partLeft = Enumerable.Range(0, part.Length / 2).Select(i => part[i * 2]).ToArray();
        Assert.InRange(AudioDecoderTests.OnsetOf(partLeft), Rate / 10 - Tolerance, Rate / 10 + Tolerance);
    }

    [Fact]
    public void 겹친_오디오_클립은_더해지고_열_수_없는_원본은_무음이다()
    {
        var sequence = new Sequence();
        var first = new AudioTrack();
        var second = new AudioTrack();
        first.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)) });
        second.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)) });
        sequence.Tracks.AddRange([first, second]);

        using var reader = new SequenceAudioReader(sequence, _ => MediaTests.SamplePath);
        var mixed = new float[Rate * 2 * 2];
        reader.Read(mixed);
        Assert.InRange(mixed.Max(), 0.9f, 1.1f); // 0.5 + 0.5

        using var missing = new SequenceAudioReader(sequence, _ => null);
        var silent = new float[Rate * 2];
        Array.Fill(silent, 1f);
        missing.Read(silent);
        Assert.All(silent, sample => Assert.Equal(0, sample));
    }
}
