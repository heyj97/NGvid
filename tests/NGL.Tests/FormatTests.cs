using NGL.Core;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.Tests;

/// <summary>
/// 컨테이너·코덱별 시험 영상. 모두 frames-h264.mp4와 같은 내용(프레임 N의 밝기 16+3N, 1초부터 440Hz)이다.
///   frames-h265.mp4  libx265 + aac      frames-h264.mov  frames-h264.mp4를 MOV로 옮긴 것
///   frames-vp9.webm  libvpx-vp9 + opus  frames-av1.mp4   libsvtav1 + aac
/// rotated-90/270.mp4는 320×180의 왼쪽 위 1/4만 흰 영상에 회전 정보(ffmpeg -display_rotation 90/270)를 붙인 것이다.
/// </summary>
public class FormatTests
{
    private const int Rate = IAudioDecoder.SampleRate;

    private static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "media", file);

    private static TimeSpan FrameTime(int frame) => TimeSpan.FromSeconds(frame / 30.0);

    [Theory]
    [InlineData("frames-h264.mp4", "h264")]
    [InlineData("frames-h264.mov", "h264")]
    [InlineData("frames-h265.mp4", "hevc")]
    [InlineData("frames-vp9.webm", "vp9")]
    [InlineData("frames-av1.mp4", "av1")]
    public void 형식마다_정보를_읽고_모든_프레임과_탐색이_정확하다(string file, string codec)
    {
        var info = MediaProbe.Probe(PathOf(file));
        Assert.Equal(new VideoStreamInfo(320, 180, 30, codec), info.Video);
        Assert.NotNull(info.Audio);
        Assert.InRange(info.Duration.TotalSeconds, 1.95, 2.1);

        using var decoder = new FFmpegVideoDecoder(PathOf(file));
        for (var n = 0; n < 60; n++)
            Assert.Equal(n, MediaTests.FrameNumberOf(decoder.GetFrameAt(FrameTime(n))!.Value));

        foreach (var n in new[] { 45, 3, 59, 0, 30, 29, 14, 15, 16, 58, 1 })
            Assert.Equal(n, MediaTests.FrameNumberOf(decoder.GetFrameAt(FrameTime(n))!.Value));

        Assert.Equal(59, MediaTests.FrameNumberOf(decoder.GetFrameAt(TimeSpan.FromSeconds(10))!.Value));
    }

    [Theory]
    [InlineData("frames-h264.mov")]
    [InlineData("frames-h265.mp4")]
    [InlineData("frames-vp9.webm")]
    [InlineData("frames-av1.mp4")]
    public void 형식마다_소리가_제자리에서_시작한다(string file)
    {
        using var decoder = new FFmpegAudioDecoder(PathOf(file));
        var buffer = new float[Rate * 2 * 2];

        var fromStart = Left(buffer, decoder.Read(buffer));
        decoder.Seek(Rate * 9 / 10);
        var fromSeek = Left(buffer, decoder.Read(buffer));

        Assert.InRange(AudioDecoderTests.OnsetOf(fromStart), Rate - 240, Rate + 240);
        Assert.InRange(AudioDecoderTests.OnsetOf(fromSeek), Rate / 10 - 240, Rate / 10 + 240);
        Assert.InRange(fromStart.Max(), 0.4f, 0.6f);
    }

    private static float[] Left(float[] interleaved, int frames) =>
        Enumerable.Range(0, frames).Select(i => interleaved[i * 2]).ToArray();

    [Theory]
    [InlineData("rotated-90.mp4", 270, 10, 310)]  // ffmpeg로 변환했을 때와 같은 자리: 왼쪽 아래
    [InlineData("rotated-270.mp4", 90, 170, 10)]  // 오른쪽 위
    public void 회전_정보가_있는_영상은_돌려서_보여준다(string file, int rotation, int whiteX, int whiteY)
    {
        Assert.Equal(new VideoStreamInfo(180, 320, 30, "h264"), MediaProbe.Probe(PathOf(file)).Video);

        using (var decoder = new FFmpegVideoDecoder(PathOf(file)))
            Assert.Equal(rotation, decoder.Rotation);

        using var frames = new VideoFrameSource(_ => PathOf(file));
        using var image = frames.GetImage(new VideoClip { Range = new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(1)) }, TimeSpan.Zero);
        using var bitmap = SKBitmap.FromImage(image);

        Assert.Equal((180, 320), (bitmap.Width, bitmap.Height));
        foreach (var (x, y) in new[] { (10, 10), (170, 10), (10, 310), (170, 310) })
        {
            var expectWhite = x == whiteX && y == whiteY;
            Assert.InRange(bitmap.GetPixel(x, y).Green, expectWhite ? 200 : 0, expectWhite ? 255 : 50);
        }
    }

    [Fact]
    public void 화질을_낮추면_프레임이_작아지고_내용은_같다()
    {
        using var decoder = new FFmpegVideoDecoder(MediaTests.SamplePath);

        var full = decoder.GetFrameAt(FrameTime(20))!.Value;
        Assert.Equal((320, 180), (full.Width, full.Height));

        decoder.Downscale = 2; // 같은 프레임을 다시 요청해도 새 크기로 나온다
        var half = decoder.GetFrameAt(FrameTime(20))!.Value;
        Assert.Equal((160, 90), (half.Width, half.Height));
        Assert.Equal(20, MediaTests.FrameNumberOf(half));

        decoder.Downscale = 4;
        var quarter = decoder.GetFrameAt(FrameTime(21))!.Value;
        Assert.Equal((80, 45), (quarter.Width, quarter.Height));
        Assert.Equal(21, MediaTests.FrameNumberOf(quarter));

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Downscale = 3);
    }
}
