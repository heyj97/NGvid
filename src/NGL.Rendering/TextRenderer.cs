using NGL.Core;
using SkiaSharp;

namespace NGL.Rendering;

/// <summary>텍스트 클립을 그린다. 글자는 화면 가운데(+스타일의 위치)를 중심으로 가운데 정렬된다.</summary>
public static class TextRenderer
{
    private static readonly Dictionary<(string Font, bool Bold), SKTypeface> Typefaces = [];

    /// <summary>글자가 차지하는 사각형의 네 귀퉁이가 시퀀스의 어디에 놓이는지(왼쪽 위부터 시계 방향). 문구가 비었으면 빈 배열.</summary>
    public static SKPoint[] Corners(TextualClip clip, SKRect frame, TimeSpan time)
    {
        if (string.IsNullOrWhiteSpace(clip.Text)) return [];

        // Draw와 같은 방식으로 크기를 재고 같은 변환(이동 → 회전 → 크기)을 건다.
        var style = clip.Style;
        using var font = new SKFont(TypefaceOf(style.Font, style.Bold), (float)style.Size);
        var lines = clip.Text.Replace("\r", "").Split('\n');
        var (w, h) = (lines.Max(line => font.MeasureText(line)) / 2, font.Spacing * lines.Length / 2);

        var motion = TextEffects.At(clip.Effects, (time - clip.Range.Start).TotalSeconds);
        var scale = (float)(Math.Clamp(clip.ValueAt(AnimatedProperty.Scale, 1, time), 0.1, 4) * motion.Scale);
        var matrix = SKMatrix.CreateTranslation(
            frame.MidX + (float)(clip.ValueAt(AnimatedProperty.X, style.X, time) + motion.X / 100 * frame.Width),
            frame.MidY + (float)(clip.ValueAt(AnimatedProperty.Y, style.Y, time) + motion.Y / 100 * frame.Height));
        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateRotationDegrees((float)clip.ValueAt(AnimatedProperty.Rotation, 0, time)));
        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateScale(scale, scale));
        return matrix.MapPoints([new(-w, -h), new(w, -h), new(w, h), new(-w, h)]);
    }

    public static void Draw(SKCanvas canvas, TextualClip clip, SKRect frame, TimeSpan time)
    {
        if (string.IsNullOrWhiteSpace(clip.Text)) return;

        var style = clip.Style;
        // 키프레임이 있으면 이 시각의 값을 쓴다. 크기·회전·불투명도의 고정된 값은 100%, 0도, 100%다.
        // 효과는 그 위에 더해진다: 위치는 클립의 자리에서 화면 크기의 %만큼 옮기고, 크기와 불투명도는 곱한다.
        var motion = TextEffects.At(clip.Effects, (time - clip.Range.Start).TotalSeconds);
        var x = clip.ValueAt(AnimatedProperty.X, style.X, time) + motion.X / 100 * frame.Width;
        var y = clip.ValueAt(AnimatedProperty.Y, style.Y, time) + motion.Y / 100 * frame.Height;
        var scale = Math.Clamp(clip.ValueAt(AnimatedProperty.Scale, 1, time), 0.1, 4) * motion.Scale;
        var rotation = clip.ValueAt(AnimatedProperty.Rotation, 0, time);
        var opacity = Math.Clamp(clip.ValueAt(AnimatedProperty.Opacity, 1, time), 0, 1) * Math.Clamp(motion.Opacity, 0, 1);
        if (opacity <= 0 || scale <= 0) return;

        using var font = new SKFont(TypefaceOf(style.Font, style.Bold), (float)style.Size);
        var lines = clip.Text.Replace("\r", "").Split('\n');
        var widths = lines.Select(line => font.MeasureText(line)).ToArray();
        var lineHeight = font.Spacing;
        var height = lineHeight * lines.Length;

        canvas.Save();
        canvas.Translate(frame.MidX + (float)x, frame.MidY + (float)y);
        canvas.RotateDegrees((float)rotation);
        canvas.Scale((float)scale);

        // 외곽선과 글자가 겹친 채로 함께 흐려지도록, 불투명도는 전체를 한 겹으로 그린 뒤에 적용한다.
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
        if (opacity < 1) canvas.SaveLayer(layer);

        using var paint = new SKPaint { IsAntialias = true };
        if (ColorOf(style.BoxColor) is { } box)
        {
            var padding = (float)style.Size * 0.3f;
            var width = widths.Max();
            paint.Color = box;
            canvas.DrawRoundRect(SKRect.Create(-width / 2 - padding, -height / 2 - padding / 2, width + padding * 2, height + padding), padding / 2, padding / 2, paint);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var left = -widths[i] / 2;
            var baseline = -height / 2 + lineHeight * i - font.Metrics.Ascent;

            if (style.ShadowOffset > 0 && ColorOf(style.ShadowColor) is { } shadow)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = shadow;
                canvas.DrawText(lines[i], left + (float)style.ShadowOffset, baseline + (float)style.ShadowOffset, SKTextAlign.Left, font, paint);
            }

            if (style.OutlineWidth > 0 && ColorOf(style.OutlineColor) is { } outline)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)style.OutlineWidth;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = outline;
                canvas.DrawText(lines[i], left, baseline, SKTextAlign.Left, font, paint);
            }

            paint.Style = SKPaintStyle.Fill;
            paint.Color = ColorOf(style.Color) ?? SKColors.White;
            canvas.DrawText(lines[i], left, baseline, SKTextAlign.Left, font, paint);
        }

        if (opacity < 1) canvas.Restore();
        canvas.Restore();
    }

    private static SKTypeface TypefaceOf(string font, bool bold)
    {
        lock (Typefaces)
        {
            if (!Typefaces.TryGetValue((font, bold), out var typeface))
            {
                // 없는 글꼴이면 시스템 기본 글꼴이 대신 쓰인다.
                typeface = SKTypeface.FromFamilyName(font, bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
                Typefaces[(font, bold)] = typeface;
            }

            return typeface;
        }
    }

    /// <summary>#RRGGBB·#AARRGGBB를 색으로 바꾼다. 비어 있거나 잘못된 글자면 null.</summary>
    public static SKColor? ColorOf(string? text) =>
        !string.IsNullOrWhiteSpace(text) && SKColor.TryParse(text, out var color) ? color : null;
}
