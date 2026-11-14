using NGL.Core;
using NGL.IO;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

public class EffectTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ngvid-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static SKImage Solid(SKColor color, int width = 16, int height = 9)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    /// <summary>화면 크기의 그림: 왼쪽 절반은 left, 오른쪽 절반은 right 색.</summary>
    private static SKImage Halves(SKColor left, SKColor right)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080));
        surface.Canvas.Clear(right);
        using var paint = new SKPaint { Color = left };
        surface.Canvas.DrawRect(0, 0, 960, 1080, paint);
        return surface.Snapshot();
    }

    /// <summary>이미지 클립 하나에 효과를 걸어 그린 화면.</summary>
    private static SKBitmap Render(SKImage image, Action<ClipEffects> configure, Func<string, CubeLut?>? lutOf = null)
    {
        var sequence = new Sequence();
        var clip = new ImageClip { Range = new TimeRange(Sec(0), Sec(1)) };
        configure(clip.Effects);
        sequence.AddTrack<VideoTrack>().Clips.Add(clip);

        var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
        using var canvas = new SKCanvas(bitmap);
        new SkiaCompositor(lutOf).Render(canvas, sequence, Sec(0), _ => image);
        return bitmap;
    }

    private static void Near(SKColor pixel, int red, int green, int blue, int tolerance = 2)
    {
        Assert.InRange(pixel.Red, red - tolerance, red + tolerance);
        Assert.InRange(pixel.Green, green - tolerance, green + tolerance);
        Assert.InRange(pixel.Blue, blue - tolerance, blue + tolerance);
    }

    private static SKColor Gray(byte value) => new(value, value, value);

    // ───── 색보정 ─────

    [Fact]
    public void 밝기는_더하고_노출은_배로_키운다()
    {
        using var gray = Solid(Gray(64));

        using var brighter = Render(gray, effects => effects.Brightness = 0.2);
        Near(brighter.GetPixel(960, 540), 115, 115, 115); // 64 + 0.2 × 255

        using var exposed = Render(gray, effects => effects.Exposure = 1);
        Near(exposed.GetPixel(960, 540), 128, 128, 128); // 1 스톱 = 2배

        using var under = Render(gray, effects => effects.Exposure = -1);
        Near(under.GetPixel(960, 540), 32, 32, 32);

        using var untouched = Render(gray, _ => { });
        Assert.Equal(Gray(64), untouched.GetPixel(960, 540));
    }

    [Fact]
    public void 대비는_중간_밝기를_기준으로_벌린다()
    {
        using var dark = Solid(Gray(64));
        using var bright = Solid(Gray(191));
        using var middle = Solid(Gray(128));

        using var a = Render(dark, effects => effects.Contrast = 0.5);
        using var b = Render(bright, effects => effects.Contrast = 0.5);
        using var c = Render(middle, effects => effects.Contrast = 0.5);
        Near(a.GetPixel(960, 540), 32, 32, 32);      // 더 어둡게
        Near(b.GetPixel(960, 540), 223, 223, 223);   // 더 밝게
        Near(c.GetPixel(960, 540), 128, 128, 128);   // 중간은 그대로

        using var flat = Render(bright, effects => effects.Contrast = -1);
        Near(flat.GetPixel(960, 540), 128, 128, 128); // 대비가 없으면 모두 중간 밝기
    }

    [Fact]
    public void 채도와_색온도가_색을_바꾼다()
    {
        using var red = Solid(SKColors.Red);
        using var mono = Render(red, effects => effects.Saturation = -1);
        Near(mono.GetPixel(960, 540), 54, 54, 54); // 빨강의 밝기(21%)만 남은 회색

        using var gray = Solid(Gray(128));
        using var warm = Render(gray, effects => effects.Temperature = 1);
        using var cool = Render(gray, effects => effects.Temperature = -1);
        Near(warm.GetPixel(960, 540), 160, 128, 96);  // 따뜻하게: 빨강 늘고 파랑 줄고
        Near(cool.GetPixel(960, 540), 96, 128, 160);

        // 값은 정해진 범위로 제한된다.
        var effects = new ClipEffects { Brightness = 9, Contrast = -9, Exposure = 9, Blur = 999, Sharpen = 9, Vignette = -1 };
        Assert.Equal((1.0, -1.0, 2.0, 100.0, 1.0, 0.0), (effects.Brightness, effects.Contrast, effects.Exposure, effects.Blur, effects.Sharpen, effects.Vignette));
    }

    // ───── 영상 효과 ─────

    [Fact]
    public void 흐림은_경계를_번지게_한다()
    {
        using var image = Halves(SKColors.Black, SKColors.White);

        using var sharp = Render(image, _ => { });
        Assert.Equal(SKColors.Black, sharp.GetPixel(950, 540));
        Assert.Equal(SKColors.White, sharp.GetPixel(970, 540));

        using var blurred = Render(image, effects => effects.Blur = 20);
        Assert.InRange(blurred.GetPixel(950, 540).Red, 40, 120);   // 검은 쪽이 밝아지고
        Assert.InRange(blurred.GetPixel(970, 540).Red, 135, 215);  // 흰 쪽이 어두워진다
        Assert.InRange(blurred.GetPixel(700, 540).Red, 0, 3);      // 먼 곳은 그대로
    }

    [Fact]
    public void 선명하게는_경계의_차이를_키운다()
    {
        using var image = Halves(Gray(100), Gray(180));

        using var result = Render(image, effects => effects.Sharpen = 1);

        Assert.InRange(result.GetPixel(959, 540).Red, 0, 60);     // 경계의 어두운 쪽은 더 어둡게
        Assert.InRange(result.GetPixel(960, 540).Red, 220, 255);  // 밝은 쪽은 더 밝게
        Near(result.GetPixel(500, 540), 100, 100, 100);           // 평평한 곳은 그대로
        Near(result.GetPixel(1500, 540), 180, 180, 180);
    }

    [Fact]
    public void 비네팅은_가장자리만_어둡게_한다()
    {
        using var white = Solid(SKColors.White);

        using var result = Render(white, effects => effects.Vignette = 1);

        Assert.Equal(SKColors.White, result.GetPixel(960, 540));
        Assert.InRange(result.GetPixel(10, 10).Red, 0, 40);
        var edge = result.GetPixel(100, 540).Red;
        Assert.InRange(edge, 20, 200);
        Assert.True(edge < result.GetPixel(500, 540).Red); // 가운데로 갈수록 밝다
    }

    // ───── LUT ─────

    /// <summary>크기 2의 LUT: 각 꼭짓점 색을 map으로 바꾼 것.</summary>
    private static CubeLut Lut(Func<float, float, float, (float, float, float)> map)
    {
        var values = new List<float>();
        for (var blue = 0; blue < 2; blue++)
        for (var green = 0; green < 2; green++)
        for (var red = 0; red < 2; red++)
        {
            var (r, g, b) = map(red, green, blue);
            values.AddRange([r, g, b]);
        }

        return new CubeLut(2, values.ToArray());
    }

    [Fact]
    public void LUT는_색을_표대로_바꾸고_표_사이의_색은_잇는다()
    {
        using var color = Solid(new SKColor(200, 100, 50));

        var identity = Lut((r, g, b) => (r, g, b));
        using var same = Render(color, effects => effects.Lut = "a.cube", _ => identity);
        Near(same.GetPixel(960, 540), 200, 100, 50);

        var inverted = Lut((r, g, b) => (1 - r, 1 - g, 1 - b));
        using var negative = Render(color, effects => effects.Lut = "a.cube", _ => inverted);
        Near(negative.GetPixel(960, 540), 55, 155, 205);

        var swapped = Lut((r, g, b) => (b, g, r)); // 빨강과 파랑을 바꾼다
        using var result = Render(color, effects => effects.Lut = "a.cube", _ => swapped);
        Near(result.GetPixel(960, 540), 50, 100, 200);

        // LUT는 색보정 뒤에 적용된다: 흑백으로 만든 뒤 반전
        using var red = Solid(SKColors.Red);
        using var both = Render(red, effects =>
        {
            effects.Saturation = -1;
            effects.Lut = "a.cube";
        }, _ => inverted);
        Near(both.GetPixel(960, 540), 201, 201, 201);

        // 파일을 찾을 수 없으면 적용하지 않는다.
        using var missing = Render(color, effects => effects.Lut = "없는.cube", _ => null);
        using var noLoader = Render(color, effects => effects.Lut = "a.cube");
        Assert.Equal(new SKColor(200, 100, 50), missing.GetPixel(960, 540));
        Assert.Equal(new SKColor(200, 100, 50), noLoader.GetPixel(960, 540));
    }

    [Fact]
    public void cube_파일을_읽는다()
    {
        var lut = CubeLutParser.Parse("""
            # 주석
            TITLE "반전"
            LUT_3D_SIZE 2
            DOMAIN_MIN 0.0 0.0 0.0
            DOMAIN_MAX 1.0 1.0 1.0

            1 1 1
            0 1 1
            1 0 1
            0 0 1
            1 1 0
            0 1 0
            1 0 0
            0.0 0.0 1e-1
            """);

        Assert.Equal(2, lut.Size);
        Assert.Equal(24, lut.Rgb.Length);
        Assert.Equal([0f, 1f, 1f], lut.Rgb[3..6]);   // 빨강이 가장 빨리 바뀐다
        Assert.Equal(0.1f, lut.Rgb[23]);

        Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse("LUT_3D_SIZE 2\n0 0 0\n1 1 1"));  // 색이 모자람
        Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse("LUT_1D_SIZE 4\n0 0 0"));         // 3D가 아님
        Assert.Throws<InvalidDataException>(() => CubeLutParser.Parse("LUT 파일이 아닌 글"));
    }

    [Fact]
    public void LUT_파일을_프로젝트로_가져와_상대_경로로_읽는다()
    {
        var source = Path.Combine(_folder, "밖", "film.cube");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "LUT_3D_SIZE 2\n" + string.Join("\n", Enumerable.Repeat("0.5 0.5 0.5", 8)));
        var project = Path.Combine(_folder, "프로젝트");

        var relative = CubeLutParser.Import(project, source);
        Assert.Equal("luts/film.cube", relative);
        Assert.Equal("luts/film.cube", CubeLutParser.Import(project, source)); // 같은 내용은 다시 복사하지 않는다

        File.WriteAllText(source, "LUT_3D_SIZE 2\n" + string.Join("\n", Enumerable.Repeat("1 1 1", 8)));
        Assert.Equal("luts/film (2).cube", CubeLutParser.Import(project, source)); // 이름이 같고 내용이 다르면 번호

        var load = CubeLutParser.LoaderFor(project);
        Assert.Equal(0.5f, load("luts/film.cube")!.Rgb[0]);
        Assert.Equal(1f, load("luts/film (2).cube")!.Rgb[0]);
        Assert.Same(load("luts/film.cube"), load("luts/film.cube")); // 읽어 둔 것을 다시 쓴다
        Assert.Null(load("luts/없음.cube"));

        var text = Path.Combine(_folder, "메모.cube");
        File.WriteAllText(text, "LUT가 아님");
        Assert.Throws<InvalidDataException>(() => CubeLutParser.Import(project, text));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(project, "luts")).Length);
    }

    // ───── 전환 ─────

    /// <summary>빨간 클립(0~2초) 뒤에 파란 클립(2~4초)을 붙이고, 파란 클립에 1초짜리 전환을 건다.</summary>
    private static (Sequence Sequence, ImageClip Red, ImageClip Blue) TwoClips(TransitionKind kind)
    {
        var sequence = new Sequence();
        var track = sequence.AddTrack<VideoTrack>();
        var red = new ImageClip { Range = new TimeRange(Sec(0), Sec(2)) };
        var blue = new ImageClip { Range = new TimeRange(Sec(2), Sec(2)), Transition = kind, TransitionDuration = Sec(1) };
        track.Clips.Add(red);
        track.Clips.Add(blue);
        return (sequence, red, blue);
    }

    private static SKBitmap RenderAt(Sequence sequence, ImageClip red, double seconds)
    {
        using var redImage = Solid(SKColors.Red);
        using var blueImage = Solid(SKColors.Blue);
        var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
        using var canvas = new SKCanvas(bitmap);
        // 실제 재생처럼, 그 시각에 필요한 클립의 그림만 준다.
        var needed = sequence.VisualsAt(Sec(seconds));
        new SkiaCompositor().Render(canvas, sequence, Sec(seconds), clip => !needed.Contains(clip) ? null : clip == red ? redImage : blueImage);
        return bitmap;
    }

    [Fact]
    public void 디졸브는_앞_장면_위로_서서히_나타난다()
    {
        var (sequence, red, blue) = TwoClips(TransitionKind.Dissolve);

        Assert.Equal([red], sequence.VisualsAt(Sec(1.9)));
        Assert.Equal([blue, red], sequence.VisualsAt(Sec(2.5))); // 전환 중에는 앞 클립도 필요하다
        Assert.Equal([blue], sequence.VisualsAt(Sec(3)));

        using var before = RenderAt(sequence, red, 1.9);
        using var start = RenderAt(sequence, red, 2);
        using var quarter = RenderAt(sequence, red, 2.25);
        using var half = RenderAt(sequence, red, 2.5);
        using var after = RenderAt(sequence, red, 3);
        Assert.Equal(SKColors.Red, before.GetPixel(960, 540));
        Assert.Equal(SKColors.Red, start.GetPixel(960, 540));     // 전환의 첫 순간은 앞 장면 그대로
        Near(quarter.GetPixel(960, 540), 191, 0, 64);
        Near(half.GetPixel(960, 540), 127, 0, 128);
        Assert.Equal(SKColors.Blue, after.GetPixel(960, 540));
    }

    [Fact]
    public void 와이프는_왼쪽에서_오른쪽으로_밀고_들어온다()
    {
        var (sequence, red, _) = TwoClips(TransitionKind.Wipe);

        using var half = RenderAt(sequence, red, 2.5);
        using var most = RenderAt(sequence, red, 2.9);

        Assert.Equal(SKColors.Blue, half.GetPixel(900, 540));
        Assert.Equal(SKColors.Red, half.GetPixel(1020, 540));
        Assert.Equal(SKColors.Blue, most.GetPixel(1700, 540));
        Assert.Equal(SKColors.Red, most.GetPixel(1800, 540));
    }

    [Fact]
    public void 검은_화면_페이드는_앞_장면이_사라진_뒤_다음_장면이_나타난다()
    {
        var (sequence, red, _) = TwoClips(TransitionKind.DipToBlack);

        using var fadingOut = RenderAt(sequence, red, 2.25);
        using var black = RenderAt(sequence, red, 2.5);
        using var fadingIn = RenderAt(sequence, red, 2.75);

        Near(fadingOut.GetPixel(960, 540), 127, 0, 0);
        Near(black.GetPixel(960, 540), 0, 0, 0);
        Near(fadingIn.GetPixel(960, 540), 0, 0, 128);
    }

    [Fact]
    public void 앞_클립이_없거나_떨어져_있으면_검은_화면에서_나타난다()
    {
        var (sequence, red, blue) = TwoClips(TransitionKind.Dissolve);
        red.Range = new TimeRange(Sec(0), Sec(1.5)); // 0.5초 떨어뜨림

        Assert.Equal([blue], sequence.VisualsAt(Sec(2.5)));
        using var half = RenderAt(sequence, red, 2.5);
        Near(half.GetPixel(960, 540), 0, 0, 128);

        // 전환이 없으면 바로 바뀐다.
        blue.Transition = TransitionKind.None;
        using var cut = RenderAt(sequence, red, 2);
        Assert.Equal(SKColors.Blue, cut.GetPixel(960, 540));
    }

    [Fact]
    public void 효과와_전환_설정이_저장된다()
    {
        var project = new Project();
        var clip = new VideoClip { Range = new TimeRange(Sec(0), Sec(3)), Transition = TransitionKind.Wipe, TransitionDuration = Sec(0.8) };
        clip.Effects.Blur = 12;
        clip.Effects.Contrast = 0.3;
        clip.Effects.Temperature = -0.4;
        clip.Effects.Lut = "luts/film.cube";
        project.Sequence.AddTrack<VideoTrack>().Clips.Add(clip);

        var json = ProjectSerializer.Serialize(project);
        var loaded = Assert.IsType<VideoClip>(ProjectSerializer.Deserialize(json).Sequence.Tracks[0].Clips[0]);

        Assert.Contains("\"transition\": \"Wipe\"", json);
        Assert.Equal((TransitionKind.Wipe, Sec(0.8)), (loaded.Transition, loaded.TransitionDuration));
        Assert.Equal((12.0, 0.3, -0.4, "luts/film.cube"), (loaded.Effects.Blur, loaded.Effects.Contrast, loaded.Effects.Temperature, loaded.Effects.Lut));
        Assert.Equal(0, loaded.Effects.Sharpen);
    }
}
