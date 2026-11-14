using NGL.Media;

namespace NGL.Tests;

/// <summary>
/// media/frames-h264.mp4의 소리는 44.1kHz 모노 AAC이고, 1초까지 무음이다가 1초부터 440Hz가 난다.
/// 소리가 시작되는 위치로 디코더의 위치 계산이 맞는지 확인한다.
/// </summary>
public class AudioDecoderTests
{
    private const int Rate = IAudioDecoder.SampleRate;
    private const int Tolerance = Rate * 5 / 1000; // 5ms

    /// <summary>끝까지 읽어서 왼쪽 채널만 돌려준다.</summary>
    private static float[] ReadAll(IAudioDecoder decoder, out bool channelsMatch)
    {
        var left = new List<float>();
        var buffer = new float[4096 * 2];
        channelsMatch = true;
        int frames;
        do
        {
            frames = decoder.Read(buffer);
            for (var i = 0; i < frames; i++)
            {
                left.Add(buffer[i * 2]);
                if (Math.Abs(buffer[i * 2] - buffer[i * 2 + 1]) > 1e-4) channelsMatch = false;
            }
        } while (frames == buffer.Length / 2);

        return left.ToArray();
    }

    /// <summary>소리가 시작되는 샘플 프레임 위치.</summary>
    public static int OnsetOf(ReadOnlySpan<float> samples, float threshold = 0.05f)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) > threshold) return i;
        }

        return -1;
    }

    [Fact]
    public void 처음부터_읽으면_48kHz_스테레오로_전체_길이가_나온다()
    {
        using var decoder = new FFmpegAudioDecoder(MediaTests.SamplePath);

        var samples = ReadAll(decoder, out var channelsMatch);

        Assert.InRange(decoder.Duration.TotalSeconds, 1.95, 2.1);
        Assert.InRange(samples.Length, Rate * 2 - 2400, Rate * 2 + 2400);
        Assert.True(channelsMatch); // 모노 원본은 좌우가 같다
        Assert.InRange(OnsetOf(samples), Rate - Tolerance, Rate + Tolerance);
        Assert.InRange(samples.Max(), 0.4f, 0.6f); // 진폭 0.5
        Assert.Equal(0, decoder.Read(new float[64])); // 끝난 뒤에는 0
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0.5)]
    [InlineData(0.013)]
    public void 탐색한_위치부터_정확히_이어진다(double seconds)
    {
        using var decoder = new FFmpegAudioDecoder(MediaTests.SamplePath);
        var start = (long)(seconds * Rate);

        decoder.Seek(start);
        var samples = ReadAll(decoder, out _);

        var expected = Rate - (int)start;
        Assert.InRange(OnsetOf(samples), expected - Tolerance, expected + Tolerance);
        Assert.InRange(samples.Length, Rate * 2 - (int)start - 2400, Rate * 2 - (int)start + 2400);
    }

    [Fact]
    public void 소리_한가운데로_탐색하면_바로_소리가_난다()
    {
        using var decoder = new FFmpegAudioDecoder(MediaTests.SamplePath);

        decoder.Seek((long)(1.5 * Rate));
        var samples = ReadAll(decoder, out _);

        Assert.InRange(OnsetOf(samples), 0, Rate / 440); // 한 주기 안에
    }

    [Fact]
    public void 끝까지_읽은_뒤에도_다시_탐색해서_읽을_수_있다()
    {
        using var decoder = new FFmpegAudioDecoder(MediaTests.SamplePath);
        ReadAll(decoder, out _);

        decoder.Seek(0);
        var again = ReadAll(decoder, out _);
        decoder.Seek(Rate * 100L);
        var beyond = ReadAll(decoder, out _);

        Assert.InRange(OnsetOf(again), Rate - Tolerance, Rate + Tolerance);
        Assert.Empty(beyond);
    }

    [Fact]
    public void 소리가_없는_파일은_거부한다()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "소리가 아닌 텍스트 파일");
            Assert.Throws<InvalidDataException>(() => new FFmpegAudioDecoder(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
