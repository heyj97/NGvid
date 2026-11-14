using NGL.Commands;
using NGL.Core;
using NGL.IO;
using NGL.Media;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

/// <summary>위 트랙의 클립에 크로마키·마스크를 걸고, 그 아래 트랙에는 파란 배경을 둔다.</summary>
public class MaskTests
{
    private static readonly SKColor Under = SKColors.Blue;

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static SKImage Solid(SKColor color, int width = 16, int height = 9)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static SKBitmap Render(SKImage image, Action<VisualClip> configure, double seconds = 0)
    {
        var sequence = new Sequence();
        var bottom = sequence.AddTrack<VideoTrack>();
        var top = sequence.AddTrack<VideoTrack>();
        var under = new ImageClip { Range = new TimeRange(Sec(0), Sec(10)) };
        var clip = new ImageClip { Range = new TimeRange(Sec(0), Sec(10)) };
        configure(clip);
        bottom.Clips.Add(under);
        top.Clips.Add(clip);

        using var background = Solid(Under);
        var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
        using var canvas = new SKCanvas(bitmap);
        new SkiaCompositor().Render(canvas, sequence, Sec(seconds), each => each == under ? background : image);
        return bitmap;
    }

    private static void Near(SKColor pixel, SKColor expected, int tolerance = 3)
    {
        Assert.InRange(pixel.Red, expected.Red - tolerance, expected.Red + tolerance);
        Assert.InRange(pixel.Green, expected.Green - tolerance, expected.Green + tolerance);
        Assert.InRange(pixel.Blue, expected.Blue - tolerance, expected.Blue + tolerance);
    }

    // ───── 크로마키 ─────

    [Fact]
    public void 크로마키는_지정한_색을_투명하게_해서_아래_화면이_보이게_한다()
    {
        // 왼쪽 절반은 그린스크린, 오른쪽 절반은 빨간 피사체
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080));
        surface.Canvas.Clear(SKColors.Red);
        using var green = new SKPaint { Color = new SKColor(0, 255, 0) };
        surface.Canvas.DrawRect(0, 0, 960, 1080, green);
        using var image = surface.Snapshot();

        using var plain = Render(image, _ => { });
        Assert.Equal(new SKColor(0, 255, 0), plain.GetPixel(480, 540));

        using var keyed = Render(image, clip => clip.Effects.KeyColor = "#00FF00");
        Near(keyed.GetPixel(480, 540), Under);           // 초록은 사라지고 배경이 보인다
        Near(keyed.GetPixel(1440, 540), SKColors.Red);   // 피사체는 그대로
    }

    [Fact]
    public void 밝기가_다른_같은_색상도_지우고_허용_범위로_비슷한_색까지_조절한다()
    {
        using var darkGreen = Solid(new SKColor(0, 150, 0));     // 어두운 초록 (조명이 약한 곳)
        using var yellowGreen = Solid(new SKColor(140, 255, 0)); // 노랑이 섞인 초록

        using var dark = Render(darkGreen, clip =>
        {
            clip.Effects.KeyColor = "#00FF00";
            clip.Effects.KeyTolerance = 0.5;
        });
        Near(dark.GetPixel(960, 540), Under);

        using var narrow = Render(yellowGreen, clip =>
        {
            clip.Effects.KeyColor = "#00FF00";
            clip.Effects.KeyTolerance = 0.1;
            clip.Effects.KeySoftness = 0;
        });
        Near(narrow.GetPixel(960, 540), new SKColor(140, 255, 0)); // 좁은 범위에서는 남는다

        using var wide = Render(yellowGreen, clip =>
        {
            clip.Effects.KeyColor = "#00FF00";
            clip.Effects.KeyTolerance = 0.8;
        });
        Near(wide.GetPixel(960, 540), Under); // 넓히면 지워진다
    }

    [Fact]
    public void 부드럽게는_경계의_색을_반투명하게_한다()
    {
        using var yellowGreen = Solid(new SKColor(140, 255, 0));

        using var soft = Render(yellowGreen, clip =>
        {
            clip.Effects.KeyColor = "#00FF00";
            clip.Effects.KeyTolerance = 0.1;
            clip.Effects.KeySoftness = 1;
        });
        var pixel = soft.GetPixel(960, 540);

        // 완전히 남지도, 완전히 지워지지도 않고 배경과 섞인다.
        Assert.InRange(pixel.Blue, 40, 215);
        Assert.InRange(pixel.Green, 40, 215);

        // 색이 잘못 적혀 있으면 크로마키를 쓰지 않는다.
        using var invalid = Render(yellowGreen, clip => clip.Effects.KeyColor = "초록");
        Assert.Equal(new SKColor(140, 255, 0), invalid.GetPixel(960, 540));
    }

    // ───── 마스크 ─────

    [Fact]
    public void 사각형_마스크는_안쪽만_보이고_반전하면_바깥쪽만_보인다()
    {
        using var red = Solid(SKColors.Red);

        // 기본 크기는 클립의 절반: 가운데 960×540
        using var masked = Render(red, clip => clip.Mask.Shape = MaskShape.Rectangle);
        Assert.Equal(SKColors.Red, masked.GetPixel(960, 540));
        Assert.Equal(SKColors.Red, masked.GetPixel(490, 280));
        Assert.Equal(Under, masked.GetPixel(470, 540));
        Assert.Equal(Under, masked.GetPixel(960, 260));

        using var inverted = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Rectangle;
            clip.Mask.Inverted = true;
        });
        Assert.Equal(Under, inverted.GetPixel(960, 540));
        Assert.Equal(SKColors.Red, inverted.GetPixel(470, 540));

        using var none = Render(red, clip => clip.Mask.Shape = MaskShape.None);
        Assert.Equal(SKColors.Red, none.GetPixel(100, 100));
    }

    [Fact]
    public void 마스크의_위치와_크기는_클립_그림에_대한_비율이다()
    {
        using var red = Solid(SKColors.Red);

        // 왼쪽 위 1/4 지점을 가운데로, 너비 20% 높이 40%: x 288~672, y 54~486
        using var result = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Rectangle;
            (clip.Mask.X, clip.Mask.Y, clip.Mask.Width, clip.Mask.Height) = (0.25, 0.25, 0.2, 0.4);
        });
        Assert.Equal(SKColors.Red, result.GetPixel(480, 270));
        Assert.Equal(SKColors.Red, result.GetPixel(300, 70));
        Assert.Equal(Under, result.GetPixel(270, 270));
        Assert.Equal(Under, result.GetPixel(690, 270));
        Assert.Equal(Under, result.GetPixel(480, 500));

        // 클립을 절반 크기로 줄이면 마스크도 따라서 줄어든다.
        using var scaled = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Rectangle;
            clip.Scale = 0.5;
        });
        Assert.Equal(SKColors.Red, scaled.GetPixel(960, 540));
        Assert.Equal(Under, scaled.GetPixel(700, 540)); // 줄기 전이라면 빨강이었을 곳
    }

    [Fact]
    public void 원_마스크는_모서리를_가린다()
    {
        using var red = Solid(SKColors.Red);

        using var result = Render(red, clip => clip.Mask.Shape = MaskShape.Ellipse);

        Assert.Equal(SKColors.Red, result.GetPixel(960, 540));
        Assert.Equal(SKColors.Red, result.GetPixel(500, 540));  // 가로 끝 근처
        Assert.Equal(Under, result.GetPixel(500, 290));         // 사각형이었다면 보였을 모서리
        Assert.Equal(Under, result.GetPixel(1420, 790));
    }

    [Fact]
    public void 자유_도형은_점을_이은_안쪽만_보이고_점이_모자라면_적용하지_않는다()
    {
        using var red = Solid(SKColors.Red);

        // 왼쪽 위, 오른쪽 위, 왼쪽 아래를 잇는 삼각형
        using var triangle = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Polygon;
            clip.Mask.Points.AddRange([new MaskPoint(0, 0), new MaskPoint(1, 0), new MaskPoint(0, 1)]);
        });
        Assert.Equal(SKColors.Red, triangle.GetPixel(300, 200));
        Assert.Equal(Under, triangle.GetPixel(1600, 900));

        using var twoPoints = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Polygon;
            clip.Mask.Points.AddRange([new MaskPoint(0, 0), new MaskPoint(1, 1)]);
        });
        Assert.Equal(SKColors.Red, twoPoints.GetPixel(1600, 200)); // 마스크 없이 전체가 보인다
    }

    [Fact]
    public void 가장자리_흐림은_경계를_서서히_바꾼다()
    {
        using var red = Solid(SKColors.Red);

        using var result = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Rectangle;
            clip.Mask.Feather = 80;
        });

        Assert.Equal(SKColors.Red, result.GetPixel(960, 540));
        var edge = result.GetPixel(480, 540); // 경계 위: 빨강과 배경이 반쯤 섞인다
        Assert.InRange(edge.Red, 80, 175);
        Assert.InRange(edge.Blue, 80, 175);
        Assert.Equal(Under, result.GetPixel(200, 540));
    }

    [Fact]
    public void 마스크는_불투명도와_함께_쓸_수_있고_키프레임으로_움직인다()
    {
        using var red = Solid(SKColors.Red);

        using var half = Render(red, clip =>
        {
            clip.Mask.Shape = MaskShape.Rectangle;
            clip.Opacity = 0.5;
        });
        Near(half.GetPixel(960, 540), new SKColor(128, 0, 127));
        Assert.Equal(Under, half.GetPixel(300, 540));

        // 마스크가 2초 동안 왼쪽 끝(0.25)에서 오른쪽 끝(0.75)으로 간다.
        void Moving(VisualClip clip)
        {
            clip.Mask.Shape = MaskShape.Rectangle;
            clip.Mask.Width = 0.2;
            clip.SetKeyframe(AnimatedProperty.MaskX, new Keyframe(Sec(0), 0.25));
            clip.SetKeyframe(AnimatedProperty.MaskX, new Keyframe(Sec(2), 0.75));
        }

        using var start = Render(red, Moving, 0);
        using var end = Render(red, Moving, 2);
        Assert.Equal(SKColors.Red, start.GetPixel(480, 540));
        Assert.Equal(Under, start.GetPixel(1440, 540));
        Assert.Equal(Under, end.GetPixel(480, 540));
        Assert.Equal(SKColors.Red, end.GetPixel(1440, 540));

        Assert.True(SequenceEdits.CanAnimate(new VideoClip(), AnimatedProperty.MaskX));
        Assert.False(SequenceEdits.CanAnimate(new TextClip(), AnimatedProperty.MaskWidth));
        Assert.False(SequenceEdits.CanAnimate(new AudioClip(), AnimatedProperty.MaskX));
    }

    [Fact]
    public void 화면의_점을_클립_그림의_좌표로_바꾼다()
    {
        var frame = SKRect.Create(1920, 1080);
        var image = new SKSize(1920, 1080);
        var clip = new VideoClip { Range = new TimeRange(Sec(0), Sec(5)) };

        void Maps(float x, float y, float expectedX, float expectedY)
        {
            var point = SkiaCompositor.ToClipSpace(clip, image, frame, Sec(0), new SKPoint(x, y));
            Assert.InRange(point.X, expectedX - 0.001f, expectedX + 0.001f);
            Assert.InRange(point.Y, expectedY - 0.001f, expectedY + 0.001f);
        }

        Maps(960, 540, 0.5f, 0.5f);
        Maps(0, 0, 0, 0);
        Maps(1920, 1080, 1, 1);

        clip.X = 200;
        clip.Scale = 0.5; // 그림은 960×540, 가운데가 (1160, 540)
        Maps(1160, 540, 0.5f, 0.5f);
        Maps(680, 270, 0, 0);
        Maps(1640, 810, 1, 1);

        clip.Rotation = 90; // 그림의 위쪽 가운데가 화면에서는 오른쪽으로 간다
        Maps(1160 + 270, 540, 0.5f, 0);

        clip.Rotation = 0;
        clip.FlipHorizontal = true;
        Maps(680, 270, 1, 0);
    }

    [Fact]
    public void 크로마키와_마스크_설정이_저장되고_원본의_화면_크기를_기록한다()
    {
        var project = new Project();
        var clip = new VideoClip { Range = new TimeRange(Sec(0), Sec(3)) };
        clip.Effects.KeyColor = "#00FF00";
        clip.Effects.KeyTolerance = 0.45;
        clip.Mask.Shape = MaskShape.Polygon;
        clip.Mask.Feather = 12;
        clip.Mask.Inverted = true;
        clip.Mask.Points.AddRange([new MaskPoint(0.1, 0.2), new MaskPoint(0.9, 0.2), new MaskPoint(0.5, 0.8)]);
        project.Sequence.AddTrack<VideoTrack>().Clips.Add(clip);

        var json = ProjectSerializer.Serialize(project);
        var loaded = Assert.IsType<VideoClip>(ProjectSerializer.Deserialize(json).Sequence.Tracks[0].Clips[0]);

        Assert.DoesNotContain("isActive", json);
        Assert.Equal(("#00FF00", 0.45, 0.1), (loaded.Effects.KeyColor, loaded.Effects.KeyTolerance, loaded.Effects.KeySoftness));
        Assert.Equal((MaskShape.Polygon, 12.0, true, true), (loaded.Mask.Shape, loaded.Mask.Feather, loaded.Mask.Inverted, loaded.Mask.IsActive));
        Assert.Equal(clip.Mask.Points, loaded.Mask.Points);

        var folder = Path.Combine(Path.GetTempPath(), "ngvid-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var asset = MediaImporter.Import(folder, Path.Combine(AppContext.BaseDirectory, "media", "rotated-90.mp4"));
            Assert.Equal((180, 320), (asset.Width, asset.Height)); // 회전을 반영한, 화면에 보이는 크기
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
