using NGL.Commands;
using NGL.Core;
using NGL.IO;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

public class TextTests
{
    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TimeRange Range(double start, double duration) => new(Sec(start), Sec(duration));

    // ───── 글자 그리기 ─────

    private static SKBitmap Render(Sequence sequence, double seconds = 0.5)
    {
        var bitmap = new SKBitmap(new SKImageInfo(sequence.Preset.Width, sequence.Preset.Height));
        using var canvas = new SKCanvas(bitmap);
        new SkiaCompositor().Render(canvas, sequence, Sec(seconds), _ => null);
        return bitmap;
    }

    /// <summary>color와 같은 색인 픽셀들의 개수와 범위. (8픽셀 간격으로 훑는다.)</summary>
    private static (int Count, int Left, int Top, int Right, int Bottom) Area(SKBitmap bitmap, SKColor color)
    {
        var (count, left, top, right, bottom) = (0, int.MaxValue, int.MaxValue, -1, -1);
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                if (bitmap.GetPixel(x, y) != color) continue;
                count++;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        }

        return (count, left, top, right, bottom);
    }

    private static (Sequence Sequence, TextClip Clip) TextSequence(string text, Action<TextStyle>? configure = null)
    {
        var sequence = new Sequence();
        var clip = sequence.AddText(Sec(0), text);
        clip.Style.Size = 200;
        clip.Style.OutlineWidth = 0;
        configure?.Invoke(clip.Style);
        return (sequence, clip);
    }

    [Fact]
    public void 텍스트는_화면_가운데에_글자색으로_그려지고_구간_밖에서는_보이지_않는다()
    {
        var (sequence, _) = TextSequence("NGvid 제목", style => style.Color = "#FF0000");

        using var shown = Render(sequence);
        var area = Area(shown, SKColors.Red);
        Assert.True(area.Count > 500);
        Assert.InRange((area.Left + area.Right) / 2, 900, 1020);   // 가로 가운데
        Assert.InRange((area.Top + area.Bottom) / 2, 480, 600);    // 세로 가운데

        using var after = Render(sequence, 3.5); // 텍스트 클립의 기본 길이는 3초
        Assert.Equal(0, Area(after, SKColors.Red).Count);
    }

    [Fact]
    public void 위치와_크기와_줄바꿈이_반영된다()
    {
        var (sequence, clip) = TextSequence("가나다", style => style.Color = "#FF0000");
        using var one = Render(sequence);
        var before = Area(one, SKColors.Red);

        clip.Style.X = 400;
        clip.Style.Y = -300;
        using var moved = Render(sequence);
        var after = Area(moved, SKColors.Red);
        Assert.InRange(after.Left - before.Left, 392, 408);
        Assert.InRange(after.Top - before.Top, -308, -292);

        clip.Style.X = clip.Style.Y = 0;
        clip.Style.Size = 100;
        using var small = Render(sequence);
        var smaller = Area(small, SKColors.Red);
        Assert.InRange((smaller.Right - smaller.Left) / (double)(before.Right - before.Left), 0.45, 0.55);

        clip.Text = "가나다\n가나다";
        using var two = Render(sequence);
        var twoLines = Area(two, SKColors.Red);
        Assert.InRange((twoLines.Bottom - twoLines.Top) / (double)(smaller.Bottom - smaller.Top), 1.8, 3.0);
        Assert.InRange((twoLines.Top + twoLines.Bottom) / 2, 500, 580); // 두 줄 전체가 가운데
    }

    [Fact]
    public void 외곽선과_그림자와_배경_박스가_그려진다()
    {
        var (sequence, clip) = TextSequence("글자", style =>
        {
            style.Color = "#FF0000";
            style.OutlineColor = "#00FF00";
            style.OutlineWidth = 24;
        });
        using var outlined = Render(sequence);
        var fill = Area(outlined, SKColors.Red);
        var outline = Area(outlined, SKColors.Lime);
        Assert.True(outline.Count > 100);
        Assert.True(outline.Left < fill.Left && outline.Right > fill.Right); // 외곽선은 글자 바깥으로 나온다

        clip.Style.OutlineWidth = 0;
        clip.Style.ShadowColor = "#0000FF";
        clip.Style.ShadowOffset = 30;
        using var shadowed = Render(sequence);
        var shadow = Area(shadowed, SKColors.Blue);
        Assert.True(shadow.Count > 50);
        Assert.True(shadow.Right > fill.Right && shadow.Bottom > fill.Bottom); // 오른쪽 아래로

        clip.Style.ShadowOffset = 0;
        clip.Style.BoxColor = "#0000FF";
        using var boxed = Render(sequence);
        var box = Area(boxed, SKColors.Blue);
        Assert.True(box.Left < fill.Left && box.Right > fill.Right && box.Top < fill.Top && box.Bottom > fill.Bottom);
        Assert.Equal(SKColors.Black, boxed.GetPixel(100, 100)); // 박스는 글자 주변만 덮는다

        // 잘못된 색은 무시한다.
        clip.Style.BoxColor = "색이 아님";
        using var invalid = Render(sequence);
        Assert.Equal(0, Area(invalid, SKColors.Blue).Count);
    }

    [Fact]
    public void 자막은_트랙_순서대로_영상_위에_겹치고_숨긴_트랙은_그리지_않는다()
    {
        var sequence = new Sequence();
        var upper = sequence.AddCaption(null, Range(0, 2), "위 자막")!;
        var second = sequence.AddTrack<CaptionTrack>();
        var lower = sequence.AddCaption(second, Range(0, 2), "아래 자막")!;
        upper.Style.Color = "#FF0000";
        lower.Style.Color = "#00FF00";
        lower.Style.Y = -200;
        upper.Style.OutlineWidth = lower.Style.OutlineWidth = 0;

        using var both = Render(sequence);
        Assert.True(Area(both, SKColors.Red).Count > 50);
        Assert.True(Area(both, SKColors.Lime).Count > 50);
        Assert.True(Area(both, SKColors.Red).Top > 800); // 기본 위치는 화면 아래쪽

        sequence.TrackOf(lower)!.Hidden = true;
        using var one = Render(sequence);
        Assert.Equal(0, Area(one, SKColors.Lime).Count);
        Assert.True(Area(one, SKColors.Red).Count > 50);
    }

    [Fact]
    public void 단어_강조는_글자_수_비율로_시간을_나눠_차례로_옮겨간다()
    {
        // 글자 수 2 + 4 + 2 = 8 → 4초 동안 "가나" 0~1초, "다라마바" 1~3초, "사아" 3~4초
        var clip = new CaptionClip { Text = "가나 다라마바\n사아", Range = Range(10, 4), Style = { HighlightColor = "#FFFF00" } };
        var lines = clip.Text.Split('\n');

        Assert.Equal((0, 0, 2), TextRenderer.HighlightedWord(clip, lines, Sec(10)));
        Assert.Equal((0, 0, 2), TextRenderer.HighlightedWord(clip, lines, Sec(10.9)));
        Assert.Equal((0, 3, 4), TextRenderer.HighlightedWord(clip, lines, Sec(11.1)));
        Assert.Equal((0, 3, 4), TextRenderer.HighlightedWord(clip, lines, Sec(12.9)));
        Assert.Equal((1, 0, 2), TextRenderer.HighlightedWord(clip, lines, Sec(13.1)));
        Assert.Equal((1, 0, 2), TextRenderer.HighlightedWord(clip, lines, Sec(14)));

        clip.Style.HighlightColor = null;
        Assert.Null(TextRenderer.HighlightedWord(clip, lines, Sec(11)));

        // 화면에서도 강조색이 왼쪽 단어에서 오른쪽 단어로 옮겨간다.
        var sequence = new Sequence();
        var caption = sequence.AddCaption(null, Range(0, 4), "가나 다라마바")!;
        caption.Style.Size = 150;
        caption.Style.OutlineWidth = 0;
        caption.Style.HighlightColor = "#FFFF00";
        using var early = Render(sequence, 0.3);
        using var late = Render(sequence, 3);
        var (first, secondWord) = (Area(early, SKColors.Yellow), Area(late, SKColors.Yellow));
        Assert.True(first.Count > 30 && secondWord.Count > 30);
        Assert.True(first.Right < secondWord.Left);
        Assert.True(Area(early, SKColors.White).Count > 30); // 나머지 단어는 원래 색
    }

    [Fact]
    public void 글자도_키프레임으로_움직인다()
    {
        var (sequence, clip) = TextSequence("이동", style => style.Color = "#FF0000");
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(0), -500));
        clip.SetKeyframe(AnimatedProperty.X, new Keyframe(Sec(2), 500));
        clip.SetKeyframe(AnimatedProperty.Opacity, new Keyframe(Sec(2), 1));
        clip.SetKeyframe(AnimatedProperty.Opacity, new Keyframe(Sec(3), 0));
        Assert.True(SequenceEdits.CanAnimate(clip, AnimatedProperty.X));
        Assert.False(SequenceEdits.CanAnimate(clip, AnimatedProperty.Volume));

        using var start = Render(sequence, 0);
        using var middle = Render(sequence, 1);
        using var gone = Render(sequence, 2.99);
        var (a, b) = (Area(start, SKColors.Red), Area(middle, SKColors.Red));
        Assert.InRange(b.Left - a.Left, 492, 508);
        Assert.Equal(0, Area(gone, SKColors.Red).Count); // 거의 투명
    }

    // ───── 추가 규칙과 스타일 ─────

    [Fact]
    public void 텍스트는_빈_영상_트랙에_놓고_자리가_없으면_맨_위에_새_트랙을_만든다()
    {
        var sequence = new Sequence();
        var video = sequence.AddTrack<VideoTrack>();
        video.Clips.Add(new VideoClip { Range = Range(0, 10) });

        var first = sequence.AddText(Sec(2.01), "제목");
        Assert.Equal(2, sequence.Tracks.Count);
        Assert.Same(first, Assert.Single(sequence.Tracks[0].Clips)); // 영상 위의 새 트랙
        Assert.Equal(Range(2, 3), first.Range);

        var second = sequence.AddText(Sec(6), "부제"); // 새 트랙의 빈자리에
        Assert.Equal(2, sequence.Tracks.Count);
        Assert.Equal([first, second], sequence.Tracks[0].Clips);

        sequence.AddText(Sec(3), "겹침"); // 두 트랙 모두 자리가 없다
        Assert.Equal(3, sequence.Tracks.Count);
    }

    [Fact]
    public void 새_자막은_트랙의_첫_자막_스타일을_따르고_겹치는_자리에는_놓지_않는다()
    {
        var sequence = new Sequence { Preset = SequencePreset.Portrait };
        var first = sequence.AddCaption(null, Range(0, 2), "첫 자막")!;
        var track = Assert.IsType<CaptionTrack>(sequence.Tracks[0]);
        Assert.Equal(1920 * 0.36, first.Style.Y); // 화면 아래쪽 기본 위치

        first.Style.Color = "#FFE14D";
        first.Style.Size = 90;
        var second = sequence.AddCaption(track, Range(2, 1.5), "둘째")!;
        Assert.Equal(("#FFE14D", 90.0, first.Style.Y), (second.Style.Color, second.Style.Size, second.Style.Y));
        Assert.NotSame(first.Style, second.Style); // 복사본이라 따로 고칠 수 있다

        Assert.Null(sequence.AddCaption(track, Range(1, 2), "겹침"));
        Assert.Null(sequence.AddCaption(track, Range(5, 0), "길이 없음"));
        Assert.Equal(2, track.Clips.Count);
    }

    [Fact]
    public void 모든_자막에_적용은_문구와_시간은_두고_스타일만_복사한다()
    {
        var sequence = new Sequence();
        var a = sequence.AddCaption(null, Range(0, 1), "하나")!;
        var b = sequence.AddCaption(null, Range(1, 1), "둘")!;
        var other = sequence.AddCaption(sequence.AddTrack<CaptionTrack>(), Range(0, 1), "다른 트랙")!;
        b.Style = new TextStyle { Color = "#00FF00", Size = 100, BoxColor = "#CC000000", X = 50 };

        Assert.True(SequenceEdits.ApplyStyleToTrack(sequence, b));

        Assert.Equal(("하나", Range(0, 1)), (a.Text, a.Range));
        Assert.Equal(("#00FF00", 100.0, "#CC000000", 50.0), (a.Style.Color, a.Style.Size, a.Style.BoxColor, a.Style.X));
        Assert.NotSame(a.Style, b.Style);
        Assert.Equal("#FFFFFF", other.Style.Color); // 다른 트랙은 그대로

        sequence.TrackOf(b)!.Locked = true;
        Assert.False(SequenceEdits.ApplyStyleToTrack(sequence, b));
    }

    [Fact]
    public void 글자_클립과_스타일이_저장된다()
    {
        var project = new Project();
        var text = project.Sequence.AddText(Sec(1), "제목 \"1편\"\n둘째 줄");
        text.Style = new TextStyle
        {
            Font = "Gulim", Size = 120, Bold = false, Color = "#112233", OutlineColor = "#445566", OutlineWidth = 3,
            ShadowColor = "#778899", ShadowOffset = 4, BoxColor = "#80000000", X = -10, Y = 20, HighlightColor = "#FFFF00",
        };
        project.Sequence.AddCaption(null, Range(0, 2), "자막");

        var json = ProjectSerializer.Serialize(project);
        var loaded = ProjectSerializer.Deserialize(json);

        Assert.Equal(json, ProjectSerializer.Serialize(loaded));
        var clip = Assert.IsType<TextClip>(loaded.Sequence.Tracks[1].Clips[0]);
        Assert.Equal("제목 \"1편\"\n둘째 줄", clip.Text);
        Assert.Equal(("Gulim", 120.0, false, "#80000000", "#FFFF00"), (clip.Style.Font, clip.Style.Size, clip.Style.Bold, clip.Style.BoxColor, clip.Style.HighlightColor));
        Assert.Null(Assert.IsType<CaptionClip>(loaded.Sequence.Tracks[0].Clips[0]).Style.BoxColor);
        Assert.Equal(["기본", "팝업", "박스"], TextStylePreset.Defaults().Select(preset => preset.Name));
    }

    // ───── 자막 파일 ─────

    [Fact]
    public void SRT를_읽는다()
    {
        var cues = Subtitles.Parse("""
            1
            00:00:01,000 --> 00:00:03,500
            안녕하세요

            2
            00:01:02,250 --> 00:01:04,000
            두 줄짜리
            <i>자막</i>입니다

            3
            00:00:09,000 --> 00:00:08,000
            끝이 시작보다 앞서면 버린다

            4
            01:00:00,000 --> 01:00:01,000
            한 시간 뒤
            """);

        Assert.Equal(
        [
            new Cue(Range(1, 2.5), "안녕하세요"),
            new Cue(Range(62.25, 1.75), "두 줄짜리\n자막입니다"),
            new Cue(Range(3600, 1), "한 시간 뒤"),
        ], cues);
        Assert.Empty(Subtitles.Parse("자막이 아닌 글"));
        Assert.Empty(Subtitles.Parse(""));
    }

    [Fact]
    public void VTT를_읽는다()
    {
        var cues = Subtitles.Parse("""
            WEBVTT

            NOTE 주석은 건너뛴다

            intro
            00:01.000 --> 00:03.500 line:90% align:center
            시간에 '시'가 없는 형식

            00:00:05.5 --> 00:00:06.25
            소수 자릿수가 짧은 경우
            """);

        Assert.Equal(
        [
            new Cue(Range(1, 2.5), "시간에 '시'가 없는 형식"),
            new Cue(Range(5.5, 0.75), "소수 자릿수가 짧은 경우"),
        ], cues);
    }

    [Fact]
    public void SRT로_쓰고_다시_읽으면_같다()
    {
        var cues = new List<Cue>
        {
            new(Range(62.25, 1.75), "둘째\n두 줄"),
            new(Range(1, 2.5), "첫째"),
            new(Range(3661.007, 1), "한 시간 넘어서"),
        };

        var srt = Subtitles.ToSrt(cues);

        Assert.StartsWith("1\n00:00:01,000 --> 00:00:03,500\n첫째\n\n2\n00:01:02,250 --> 00:01:04,000\n둘째\n두 줄\n\n3\n01:01:01,007 --> 01:01:02,007\n", srt);
        Assert.Equal(cues.OrderBy(cue => cue.Range.Start), Subtitles.Parse(srt));

        var path = Path.Combine(Path.GetTempPath(), "ngvid-" + Guid.NewGuid().ToString("N") + ".srt");
        try
        {
            Subtitles.SaveSrt(path, cues);
            Assert.Equal(3, Subtitles.Load(path).Count);
            Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]); // BOM
        }
        finally
        {
            File.Delete(path);
        }
    }
}
