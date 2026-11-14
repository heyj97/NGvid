using System.Runtime.InteropServices;
using NGL.Media;

namespace NGL.Tests;

/// <summary>
/// media/frames-h264.mp4: 320×180, 30fps, 2초(60프레임), H.264 + AAC 440Hz.
/// 프레임 N은 밝기(Y)가 16 + 3N인 단색이라서 픽셀 값으로 몇 번째 프레임인지 알 수 있다.
/// 만든 명령:
/// ffmpeg -f lavfi -i "color=c=black:s=320x180:r=30:d=2,geq=lum='16+3*N':cb=128:cr=128"
///        -f lavfi -i "sine=frequency=440:duration=2"
///        -c:v libx264 -crf 4 -g 15 -pix_fmt yuv420p -c:a aac -shortest frames-h264.mp4
/// </summary>
public class MediaTests
{
    public static string SamplePath { get; } = Path.Combine(AppContext.BaseDirectory, "media", "frames-h264.mp4");

    private static TimeSpan FrameTime(int frame) => TimeSpan.FromSeconds(frame / 30.0);

    /// <summary>프레임 가운데 픽셀의 밝기에서 프레임 번호를 되찾는다.</summary>
    public static int FrameNumberOf(VideoFrame frame)
    {
        var offset = frame.Height / 2 * frame.Stride + frame.Width / 2 * 4;
        var blue = Marshal.ReadByte(frame.Pixels, offset);
        var green = Marshal.ReadByte(frame.Pixels, offset + 1);
        var red = Marshal.ReadByte(frame.Pixels, offset + 2);
        Assert.InRange(Math.Abs(blue - green) + Math.Abs(green - red), 0, 4); // 회색이어야 한다

        var luma = green * 219.0 / 255.0; // RGB 0~255 → Y 16~235에서 16을 뺀 값
        return (int)Math.Round(luma / 3);
    }

    [Fact]
    public void Probe는_길이와_영상_오디오_정보를_읽는다()
    {
        var info = MediaProbe.Probe(SamplePath);

        Assert.InRange(info.Duration.TotalSeconds, 1.95, 2.1);
        Assert.Equal(new VideoStreamInfo(320, 180, 30, "h264"), info.Video);
        Assert.Equal(new AudioStreamInfo(44100, 1, "aac"), info.Audio);
    }

    [Fact]
    public void Probe는_미디어가_아닌_파일을_거부한다()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "영상이 아닌 텍스트 파일");
            Assert.Throws<InvalidDataException>(() => MediaProbe.Probe(path));
            Assert.Throws<InvalidDataException>(() => new FFmpegVideoDecoder(path));
            Assert.Throws<InvalidDataException>(() => MediaProbe.Probe(path + ".없는파일"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 디코더는_영상_정보를_알려준다()
    {
        using var decoder = new FFmpegVideoDecoder(SamplePath);

        Assert.Equal(320, decoder.Width);
        Assert.Equal(180, decoder.Height);
        Assert.Equal(30, decoder.FrameRate);
        Assert.InRange(decoder.Duration.TotalSeconds, 1.95, 2.05);
    }

    [Fact]
    public void 처음부터_차례로_읽으면_모든_프레임이_순서대로_나온다()
    {
        using var decoder = new FFmpegVideoDecoder(SamplePath);

        for (var n = 0; n < 60; n++)
        {
            var frame = decoder.GetFrameAt(FrameTime(n));
            Assert.NotNull(frame);
            Assert.Equal(n, FrameNumberOf(frame.Value));
            Assert.Equal(320, frame.Value.Width);
            Assert.Equal(180, frame.Value.Height);
            Assert.InRange(Math.Abs((frame.Value.Time - FrameTime(n)).TotalMilliseconds), 0, 1);
        }
    }

    [Fact]
    public void 아무_시각으로_건너뛰어도_정확한_프레임이_나온다()
    {
        using var decoder = new FFmpegVideoDecoder(SamplePath);

        // 뒤로, 앞으로, 키프레임(15의 배수) 바로 앞뒤, 같은 프레임 반복
        foreach (var n in new[] { 45, 3, 59, 0, 30, 29, 14, 15, 16, 16, 58, 1, 44, 46 })
        {
            var frame = decoder.GetFrameAt(FrameTime(n));
            Assert.Equal(n, FrameNumberOf(frame!.Value));
        }
    }

    [Fact]
    public void 프레임_사이의_시각은_그때_보이는_프레임을_돌려준다()
    {
        using var decoder = new FFmpegVideoDecoder(SamplePath);

        Assert.Equal(20, FrameNumberOf(decoder.GetFrameAt(FrameTime(20) + TimeSpan.FromMilliseconds(15))!.Value));
        Assert.Equal(20, FrameNumberOf(decoder.GetFrameAt(FrameTime(21) - TimeSpan.FromMilliseconds(1))!.Value));
        Assert.Equal(0, FrameNumberOf(decoder.GetFrameAt(TimeSpan.FromSeconds(-5))!.Value));
    }

    [Fact]
    public void 끝을_넘으면_마지막_프레임이고_다시_앞으로_갈_수_있다()
    {
        using var decoder = new FFmpegVideoDecoder(SamplePath);

        Assert.Equal(59, FrameNumberOf(decoder.GetFrameAt(TimeSpan.FromSeconds(10))!.Value));
        Assert.Equal(59, FrameNumberOf(decoder.GetFrameAt(TimeSpan.FromSeconds(11))!.Value));
        Assert.Equal(10, FrameNumberOf(decoder.GetFrameAt(FrameTime(10))!.Value));

        // 차례로 읽다가 끝을 넘는 경우
        decoder.GetFrameAt(FrameTime(58));
        Assert.Equal(59, FrameNumberOf(decoder.GetFrameAt(FrameTime(60))!.Value));
    }

    [Fact]
    public void 닫은_디코더는_쓸_수_없다()
    {
        var decoder = new FFmpegVideoDecoder(SamplePath);
        decoder.Dispose();
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.GetFrameAt(TimeSpan.Zero));
    }
}
