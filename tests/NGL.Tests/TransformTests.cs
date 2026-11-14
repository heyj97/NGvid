using NGL.Core;
using NGL.IO;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

/// <summary>
/// 시험 그림은 200×100이고 왼쪽 절반이 빨강, 오른쪽 절반이 파랑이다.
/// 16:9 시퀀스(1920×1080)에 맞추면 1920×960으로 놓여 위아래 60픽셀씩 검게 남는다.
/// </summary>
public class TransformTests
{
    private static readonly SKColor Red = SKColors.Red;
    private static readonly SKColor Blue = SKColors.Blue;
    private static readonly SKColor Black = SKColors.Black;

    private static SKImage RedBlue()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 100));
        surface.Canvas.Clear(Blue);
        using var paint = new SKPaint { Color = Red };
        surface.Canvas.DrawRect(0, 0, 100, 100, paint);
        return surface.Snapshot();
    }

    private static SKImage Solid(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 9));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    /// <summary>클립 하나를 설정대로 그린 화면.</summary>
    private static SKBitmap Render(Action<ImageClip> configure, SKColor? backdrop = null)
    {
        var sequence = new Sequence();
        var clip = new ImageClip { Range = new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(1)) };
        configure(clip);
        sequence.AddTrack<VideoTrack>().Clips.Add(clip);

        // 아래 트랙에 단색 배경을 깐다(블렌드 확인용).
        ImageClip? under = null;
        if (backdrop is not null)
        {
            under = new ImageClip { Range = clip.Range };
            sequence.Tracks[^1].Clips.Clear();
            var bottom = sequence.AddTrack<VideoTrack>();
            sequence.MoveTrack(bottom, 1);
            sequence.Tracks[0].Clips.Add(clip);
            bottom.Clips.Add(under);
        }

        using var image = RedBlue();
        using var back = Solid(backdrop ?? Black);
        var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
        using var canvas = new SKCanvas(bitmap);
        new SkiaCompositor().Render(canvas, sequence, TimeSpan.Zero, each => each == under ? back : image);
        return bitmap;
    }

    [Fact]
    public void 기본값은_전체가_보이게_가운데에_맞춘다()
    {
        using var result = Render(_ => { });

        Assert.Equal(Red, result.GetPixel(480, 540));
        Assert.Equal(Blue, result.GetPixel(1440, 540));
        Assert.Equal(Black, result.GetPixel(960, 30));
        Assert.Equal(Black, result.GetPixel(960, 1050));
        Assert.Equal(Red, result.GetPixel(5, 70));
    }

    [Fact]
    public void 채우기는_화면을_꽉_채우고_넘치는_부분은_잘린다()
    {
        using var result = Render(clip => clip.Fit = FitMode.Fill);

        // 2160×1080으로 커져 좌우 120픽셀씩 잘리고 위아래 여백이 없다.
        Assert.Equal(Red, result.GetPixel(480, 5));
        Assert.Equal(Blue, result.GetPixel(1440, 1075));
        Assert.Equal(Red, result.GetPixel(930, 540));
        Assert.Equal(Blue, result.GetPixel(990, 540));
    }

    [Fact]
    public void 위치는_가운데에서_옮긴_거리이고_프레임_밖은_잘린다()
    {
        using var result = Render(clip =>
        {
            clip.X = 960;
            clip.Y = -100;
        });

        Assert.Equal(Black, result.GetPixel(480, 540));
        Assert.Equal(Red, result.GetPixel(1440, 440));   // 빨간 절반이 오른쪽 절반으로
        Assert.Equal(Red, result.GetPixel(1440, 5));     // 위로 100 올라가 위쪽 여백이 사라짐
        Assert.Equal(Black, result.GetPixel(1440, 960)); // 아래 여백이 160으로 늘어남
    }

    [Fact]
    public void 크기는_가운데를_기준으로_바뀐다()
    {
        using var half = Render(clip => clip.Scale = 0.5); // 960×480
        Assert.Equal(Red, half.GetPixel(700, 540));
        Assert.Equal(Blue, half.GetPixel(1200, 540));
        Assert.Equal(Black, half.GetPixel(400, 540));
        Assert.Equal(Black, half.GetPixel(960, 250));

        using var twice = Render(clip => clip.Scale = 2); // 3840×1920 → 가운데 부분만 보인다
        Assert.Equal(Red, twice.GetPixel(5, 5));
        Assert.Equal(Blue, twice.GetPixel(1915, 1075));

        Assert.Equal(4, new ImageClip { Scale = 99 }.Scale);
        Assert.Equal(0.1, new ImageClip { Scale = 0 }.Scale);
    }

    [Fact]
    public void 회전은_시계_방향이다()
    {
        using var result = Render(clip => clip.Rotation = 90);

        // 왼쪽(빨강)이 위로, 오른쪽(파랑)이 아래로 간다. 960×1920이 되어 위아래가 잘린다.
        Assert.Equal(Red, result.GetPixel(960, 100));
        Assert.Equal(Blue, result.GetPixel(960, 980));
        Assert.Equal(Black, result.GetPixel(300, 540));
        Assert.Equal(Black, result.GetPixel(1620, 540));
    }

    [Fact]
    public void 불투명도는_아래_화면과_섞는_비율이다()
    {
        using var result = Render(clip => clip.Opacity = 0.5);
        var pixel = result.GetPixel(480, 540);

        Assert.InRange(pixel.Red, 126, 129);
        Assert.Equal(0, pixel.Green);

        using var invisible = Render(clip => clip.Opacity = 0);
        Assert.Equal(Black, invisible.GetPixel(480, 540));
        Assert.Equal(1, new ImageClip { Opacity = 5 }.Opacity);
    }

    [Theory]
    [InlineData(BlendMode.Normal, 255, 0, 0)]
    [InlineData(BlendMode.Multiply, 128, 0, 0)]     // 빨강 × 회색
    [InlineData(BlendMode.Screen, 255, 128, 128)]   // 밝아지기만 한다
    [InlineData(BlendMode.Add, 255, 128, 128)]      // 더해서 최대치에서 멈춘다
    public void 블렌드_모드는_아래_화면과_색을_섞는다(BlendMode mode, int red, int green, int blue)
    {
        using var result = Render(clip => clip.Blend = mode, backdrop: new SKColor(128, 128, 128));
        var pixel = result.GetPixel(480, 540); // 위 클립의 빨간 부분

        Assert.InRange(pixel.Red, red - 1, red + 1);
        Assert.InRange(pixel.Green, green - 1, green + 1);
        Assert.InRange(pixel.Blue, blue - 1, blue + 1);
        Assert.Equal(new SKColor(128, 128, 128), result.GetPixel(960, 30)); // 위 클립이 없는 곳은 배경 그대로
    }

    [Fact]
    public void 자르기는_남은_부분을_다시_화면에_맞춘다()
    {
        // 왼쪽 절반을 잘라내면 파란 100×100이 남아 1080×1080으로 놓인다.
        using var result = Render(clip => clip.CropLeft = 0.5);

        Assert.Equal(Blue, result.GetPixel(960, 540));
        Assert.Equal(Blue, result.GetPixel(430, 5));
        Assert.Equal(Black, result.GetPixel(400, 540));
        Assert.Equal(Black, result.GetPixel(1520, 540));

        // 마주 보는 두 쪽을 합쳐 전부 잘라낼 수는 없다.
        var clip = new ImageClip { CropLeft = 0.7 };
        clip.CropRight = 0.9;
        Assert.Equal(0.25, clip.CropRight, 3);
    }

    [Fact]
    public void 뒤집기는_좌우와_상하를_바꾼다()
    {
        using var flipped = Render(clip => clip.FlipHorizontal = true);
        Assert.Equal(Blue, flipped.GetPixel(480, 540));
        Assert.Equal(Red, flipped.GetPixel(1440, 540));

        // 상하로 뒤집어도 좌우는 그대로다. 회전과 함께 쓰면 위아래가 바뀐다.
        using var vertical = Render(clip =>
        {
            clip.Rotation = 90;
            clip.FlipHorizontal = true;
        });
        Assert.Equal(Blue, vertical.GetPixel(960, 100));
        Assert.Equal(Red, vertical.GetPixel(960, 980));
    }

    [Fact]
    public void 변형_설정이_저장된다()
    {
        var project = new Project();
        var clip = new VideoClip
        {
            Range = new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            X = -120.5, Y = 40, Scale = 1.5, Rotation = -15, Opacity = 0.8,
            Blend = BlendMode.Screen, Fit = FitMode.Fill,
            CropLeft = 0.1, CropTop = 0.2, CropRight = 0.3, CropBottom = 0.05,
            FlipHorizontal = true, FlipVertical = true,
        };
        project.Sequence.AddTrack<VideoTrack>().Clips.Add(clip);

        var json = ProjectSerializer.Serialize(project);
        var loaded = Assert.IsType<VideoClip>(ProjectSerializer.Deserialize(json).Sequence.Tracks[0].Clips[0]);

        Assert.Contains("\"blend\": \"Screen\"", json);
        Assert.Contains("\"fit\": \"Fill\"", json);
        Assert.Equal((-120.5, 40.0, 1.5, -15.0, 0.8), (loaded.X, loaded.Y, loaded.Scale, loaded.Rotation, loaded.Opacity));
        Assert.Equal((BlendMode.Screen, FitMode.Fill, true, true), (loaded.Blend, loaded.Fit, loaded.FlipHorizontal, loaded.FlipVertical));
        Assert.Equal((0.1, 0.2, 0.3, 0.05), (loaded.CropLeft, loaded.CropTop, loaded.CropRight, loaded.CropBottom));
    }
}
