using NGL.Core;
using NGL.Playback;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

public class CompositorTests
{
    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static SKImage Solid(int width, int height, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    /// <summary>시퀀스 크기의 그림에 한 프레임을 그려 돌려준다.</summary>
    private static SKBitmap Render(Sequence sequence, TimeSpan time, Func<Clip, SKImage?> imageOf)
    {
        var bitmap = new SKBitmap(new SKImageInfo(sequence.Preset.Width, sequence.Preset.Height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta); // 합성기가 프레임 전체를 덮는지 확인하기 위한 색
        new SkiaCompositor().Render(canvas, sequence, time, imageOf);
        return bitmap;
    }

    private static (Sequence Sequence, VideoClip Clip) OneClip(SequencePreset preset, TimeRange range)
    {
        var sequence = new Sequence { Preset = preset };
        var track = new VideoTrack();
        var clip = new VideoClip { Range = range };
        track.Clips.Add(clip);
        sequence.Tracks.Add(track);
        return (sequence, clip);
    }

    [Fact]
    public void 같은_비율의_영상은_프레임을_꽉_채운다()
    {
        var (sequence, _) = OneClip(SequencePreset.Landscape, new TimeRange(Sec(0), Sec(5)));
        using var red = Solid(320, 180, SKColors.Red);

        using var result = Render(sequence, Sec(1), _ => red);

        Assert.Equal(SKColors.Red, result.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, result.GetPixel(960, 540));
        Assert.Equal(SKColors.Red, result.GetPixel(1919, 1079));
    }

    [Fact]
    public void 비율이_다른_영상은_전체가_보이게_맞추고_남는_곳은_검다()
    {
        // 9:16 시퀀스(1080×1920)에 16:9 영상 → 가운데 1080×607.5 영역
        var (sequence, _) = OneClip(SequencePreset.Portrait, new TimeRange(Sec(0), Sec(5)));
        using var red = Solid(320, 180, SKColors.Red);

        using var result = Render(sequence, Sec(1), _ => red);

        Assert.Equal(SKColors.Black, result.GetPixel(540, 100));
        Assert.Equal(SKColors.Black, result.GetPixel(540, 650));
        Assert.Equal(SKColors.Red, result.GetPixel(540, 662));
        Assert.Equal(SKColors.Red, result.GetPixel(5, 960));
        Assert.Equal(SKColors.Red, result.GetPixel(1075, 1258));
        Assert.Equal(SKColors.Black, result.GetPixel(540, 1270));
    }

    [Fact]
    public void 클립_구간_밖에서는_검은_화면이다()
    {
        var (sequence, _) = OneClip(SequencePreset.Square, new TimeRange(Sec(2), Sec(3)));
        using var red = Solid(100, 100, SKColors.Red);

        using var before = Render(sequence, Sec(1.99), _ => red);
        using var first = Render(sequence, Sec(2), _ => red);
        using var end = Render(sequence, Sec(5), _ => red);

        Assert.Equal(SKColors.Black, before.GetPixel(540, 540));
        Assert.Equal(SKColors.Red, first.GetPixel(540, 540));
        Assert.Equal(SKColors.Black, end.GetPixel(540, 540)); // 끝 시각은 포함하지 않는다
    }

    [Fact]
    public void 목록의_첫_트랙이_맨_위에_그려진다()
    {
        var sequence = new Sequence();
        var top = new VideoTrack();
        var bottom = new VideoTrack();
        var topClip = new VideoClip { Range = new TimeRange(Sec(0), Sec(2)) };
        var bottomClip = new VideoClip { Range = new TimeRange(Sec(0), Sec(4)) };
        top.Clips.Add(topClip);
        bottom.Clips.Add(bottomClip);
        sequence.Tracks.AddRange([top, new AudioTrack(), bottom]);

        using var red = Solid(320, 180, SKColors.Red);
        using var blue = Solid(320, 180, SKColors.Blue);
        SKImage ImageOf(Clip clip) => clip == topClip ? red : blue;

        using var both = Render(sequence, Sec(1), ImageOf);
        using var onlyBottom = Render(sequence, Sec(3), ImageOf);

        Assert.Equal(SKColors.Red, both.GetPixel(960, 540));
        Assert.Equal(SKColors.Blue, onlyBottom.GetPixel(960, 540));
    }

    [Fact]
    public void 이미지가_없는_클립은_건너뛴다()
    {
        var (sequence, _) = OneClip(SequencePreset.Landscape, new TimeRange(Sec(0), Sec(5)));

        using var result = Render(sequence, Sec(1), _ => null);

        Assert.Equal(SKColors.Black, result.GetPixel(960, 540));
    }

    [Fact]
    public void 영상_파일의_프레임이_클립_위치와_트리밍에_맞게_그려진다()
    {
        // 타임라인 1초에 놓고 원본의 10번째 프레임부터 쓰는 클립
        var (sequence, clip) = OneClip(SequencePreset.Landscape, new TimeRange(Sec(1), Sec(1)));
        clip.SourceStart = sequence.TimeAt(10);
        using var frames = new VideoFrameSource(_ => MediaTests.SamplePath);

        foreach (var (timelineFrame, sourceFrame) in new[] { (30, 10), (35, 15), (59, 39), (31, 11) })
        {
            var time = sequence.TimeAt(timelineFrame);
            using var result = Render(sequence, time, c => frames.GetImage((VideoClip)c, time));

            var gray = result.GetPixel(960, 540);
            Assert.Equal(sourceFrame, (int)Math.Round(gray.Green * 219.0 / 255.0 / 3));
            Assert.Equal(gray, result.GetPixel(3, 3));
        }
    }

    [Fact]
    public void 열_수_없는_원본은_이미지가_없다()
    {
        var clip = new VideoClip { Range = new TimeRange(Sec(0), Sec(1)) };
        using var missing = new VideoFrameSource(_ => Path.Combine(Path.GetTempPath(), "없는파일.mp4"));
        using var unknown = new VideoFrameSource(_ => null);

        Assert.Null(missing.GetImage(clip, Sec(0)));
        Assert.Null(missing.GetImage(clip, Sec(0.5)));
        Assert.Null(unknown.GetImage(clip, Sec(0)));
    }
}
