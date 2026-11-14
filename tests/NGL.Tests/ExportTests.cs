using NGL.Core;
using NGL.Export;
using NGL.Media;

namespace NGL.Tests;

public class ExportTests : IDisposable
{
    private const int Rate = IAudioDecoder.SampleRate;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ngvid-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>시험 영상(2초)을 media에 넣고, 영상과 소리를 타임라인에 놓은 프로젝트.</summary>
    private Project SampleProject(out VideoClip video, out AudioClip audio)
    {
        var project = new Project();
        var asset = MediaImporter.Import(_folder, MediaTests.SamplePath);
        project.Assets.Add(asset);
        var clips = project.Sequence.Add(asset);
        video = (VideoClip)clips[0];
        audio = (AudioClip)clips[1];
        return project;
    }

    [Fact]
    public void 내보낸_MP4는_시퀀스_규격이고_화면과_소리가_타임라인과_같다()
    {
        // 원본의 10번 프레임부터 1.5초를 쓰고, 소리는 절반 크기로.
        var project = SampleProject(out var video, out var audio);
        var sequence = project.Sequence;
        video.SourceStart = audio.SourceStart = sequence.TimeAt(10);
        video.Range = audio.Range = new TimeRange(Sec(0), Sec(1.5));
        audio.Volume = 0.5;

        var output = Path.Combine(_folder, "out", "결과.mp4");
        var reported = new List<double>();
        new Mp4Exporter().Export(project, _folder,
            new ExportSettings(output, ExportSettings.BitrateFor(ExportQuality.Medium, sequence)),
            new SynchronousProgress(reported.Add));

        // 규격: 1920×1080, 30fps, H.264 + AAC, 1.5초
        var info = MediaProbe.Probe(output);
        Assert.Equal(new VideoStreamInfo(1920, 1080, 30, "h264"), info.Video);
        Assert.Equal(new AudioStreamInfo(Rate, 2, "aac"), info.Audio);
        Assert.InRange(info.Duration.TotalSeconds, 1.45, 1.6);

        // 화면: 내보낸 n번 프레임은 원본의 n+10번 프레임이다. 마지막 프레임(44번)까지 들어 있다.
        // (프레임 사이의 밝기 차이는 3~4단계이고, RGB를 거쳐 다시 압축하면서 1~2단계 어두워질 수 있다.)
        using (var decoder = new FFmpegVideoDecoder(output))
        {
            foreach (var n in new[] { 0, 1, 20, 43, 44 })
            {
                var gray = MediaTests.GrayOf(decoder.GetFrameAt(sequence.TimeAt(n))!.Value);
                Assert.InRange(gray, MediaTests.ExpectedGray(n + 10) - 2, MediaTests.ExpectedGray(n + 10) + 1);
            }
        }

        // 소리: 원본은 1초에 시작하므로(10프레임 = 1/3초 앞당김) 약 0.667초에 시작하고 크기는 0.5 × 0.5.
        using (var decoder = new FFmpegAudioDecoder(output))
        {
            var buffer = new float[Rate * 2 * 2];
            var frames = decoder.Read(buffer);
            var left = Enumerable.Range(0, frames).Select(i => buffer[i * 2]).ToArray();
            Assert.InRange(frames, (int)(Rate * 1.45), (int)(Rate * 1.6));
            Assert.InRange(AudioDecoderTests.OnsetOf(left, 0.03f), Rate * 2 / 3 - 480, Rate * 2 / 3 + 480);
            Assert.InRange(left.Max(), 0.2f, 0.3f);
        }

        // 진행률은 늘어나기만 하고 1로 끝난다.
        Assert.Equal(45, reported.Count);
        Assert.Equal(reported.Order(), reported);
        Assert.Equal(1.0, reported[^1]);
    }

    [Fact]
    public void 취소하면_파일을_남기지_않는다()
    {
        var project = SampleProject(out _, out _);
        var output = Path.Combine(_folder, "취소.mp4");
        using var cancel = new CancellationTokenSource();

        // 5번째 프레임에서 취소
        var progress = new SynchronousProgress(value =>
        {
            if (value >= 5 / 60.0) cancel.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() => new Mp4Exporter().Export(project, _folder,
            new ExportSettings(output, 8_000_000), progress, cancel.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void 빈_시퀀스는_내보내지_않는다()
    {
        var output = Path.Combine(_folder, "빈.mp4");

        Assert.Throws<InvalidOperationException>(() => new Mp4Exporter().Export(new Project(), _folder,
            new ExportSettings(output, 8_000_000)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void 품질_단계와_규격에_따라_비트레이트가_정해진다()
    {
        var sequence = new Sequence();
        Assert.Equal(8_000_000, ExportSettings.BitrateFor(ExportQuality.Low, sequence));
        Assert.Equal(12_000_000, ExportSettings.BitrateFor(ExportQuality.Medium, sequence));
        Assert.Equal(20_000_000, ExportSettings.BitrateFor(ExportQuality.High, sequence));

        sequence.FrameRate = 60;
        Assert.Equal(18_000_000, ExportSettings.BitrateFor(ExportQuality.Medium, sequence));

        sequence.Preset = SequencePreset.Square; // 1080×1080은 화소 수가 9/16
        Assert.Equal(18_000_000 * 9 / 16, ExportSettings.BitrateFor(ExportQuality.Medium, sequence));
    }

    /// <summary>알림을 받는 즉시 그 자리에서 처리한다. (Progress&lt;T&gt;는 다른 스레드로 미뤄서 순서를 확인할 수 없다.)</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
