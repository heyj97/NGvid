using System.Runtime.CompilerServices;
using NGL.Core;
using SkiaSharp;

namespace NGL.Rendering;

/// <summary>특정 시각의 시퀀스 한 프레임을 그린다. 미리보기와 내보내기가 같은 구현을 쓴다.</summary>
public interface IFrameCompositor
{
    /// <param name="canvas">(0,0)~(시퀀스 너비, 높이) 좌표로 그린다. 화면 크기에 맞추는 변환은 호출하는 쪽이 건다.</param>
    /// <param name="imageOf">클립이 이 시각에 보여줄 이미지. 없으면 null.</param>
    void Render(SKCanvas canvas, Sequence sequence, TimeSpan time, Func<Clip, SKImage?> imageOf);
}

/// <param name="lutOf">클립에 적힌 LUT 경로로 색 변환표를 찾아 준다. 없으면 LUT는 적용하지 않는다.</param>
public sealed class SkiaCompositor(Func<string, CubeLut?>? lutOf = null) : IFrameCompositor
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear);
    private readonly SKPaint _background = new() { Color = SKColors.Black };
    private readonly SKPaint _layer = new();

    public void Render(SKCanvas canvas, Sequence sequence, TimeSpan time, Func<Clip, SKImage?> imageOf)
    {
        var frame = SKRect.Create(sequence.Preset.Width, sequence.Preset.Height);

        canvas.Save();
        canvas.ClipRect(frame); // 프레임 밖으로 나간 부분은 잘린다
        canvas.DrawRect(frame, _background);

        // 목록의 첫 트랙이 맨 위에 보여야 하므로 마지막 트랙부터 그린다.
        for (var i = sequence.Tracks.Count - 1; i >= 0; i--)
        {
            var track = sequence.Tracks[i];
            if (track is AudioTrack || track.Hidden) continue;

            foreach (var clip in track.Clips)
            {
                if (!clip.Range.Contains(time)) continue;

                if (clip is TextualClip text) TextRenderer.Draw(canvas, text, frame, time);
                else if (clip is VisualClip visual) DrawWithTransition(canvas, track, visual, frame, time, imageOf);
            }
        }

        canvas.Restore();
    }

    /// <summary>클립을 그린다. 전환 중이면 앞 클립을 먼저 그리고 그 위에 전환 효과를 적용해 그린다.</summary>
    private void DrawWithTransition(SKCanvas canvas, Track track, VisualClip clip, SKRect frame, TimeSpan time, Func<Clip, SKImage?> imageOf)
    {
        var image = imageOf(clip);
        if (Transitions.Progress(clip, time) is not { } progress)
        {
            if (image is not null) Draw(canvas, image, clip, frame, time, 1);
            return;
        }

        // 검은 화면 페이드: 앞 절반 동안 앞 장면이 사라지고, 뒤 절반 동안 이 장면이 나타난다.
        var dip = clip.Transition == TransitionKind.DipToBlack;
        if (Transitions.Outgoing(track, clip, time) is { } outgoing && imageOf(outgoing) is { } previous)
            Draw(canvas, previous, outgoing, frame, time, dip ? Math.Max(0, 1 - 2 * progress) : 1);
        if (image is null) return;

        switch (clip.Transition)
        {
            case TransitionKind.Wipe:
                canvas.Save();
                canvas.ClipRect(SKRect.Create(frame.Left, frame.Top, (float)(frame.Width * progress), frame.Height));
                Draw(canvas, image, clip, frame, time, 1);
                canvas.Restore();
                break;
            case TransitionKind.DipToBlack:
                Draw(canvas, image, clip, frame, time, Math.Max(0, 2 * progress - 1));
                break;
            default:
                Draw(canvas, image, clip, frame, time, progress);
                break;
        }
    }

    /// <summary>
    /// 클립의 자르기·화면 맞춤·위치·회전·크기·뒤집기·불투명도·블렌드·효과·색보정을 적용해 그린다.
    /// fade는 전환 때문에 더 곱하는 불투명도다.
    /// </summary>
    private void Draw(SKCanvas canvas, SKImage image, VisualClip clip, SKRect frame, TimeSpan time, double fade)
    {
        // 키프레임이 있는 속성은 이 시각의 값을 쓴다.
        var x = clip.ValueAt(AnimatedProperty.X, clip.X, time);
        var y = clip.ValueAt(AnimatedProperty.Y, clip.Y, time);
        var scale = Math.Clamp(clip.ValueAt(AnimatedProperty.Scale, clip.Scale, time), 0.1, 4);
        var rotation = clip.ValueAt(AnimatedProperty.Rotation, clip.Rotation, time);
        var opacity = Math.Clamp(clip.ValueAt(AnimatedProperty.Opacity, clip.Opacity, time), 0, 1) * fade;
        if (opacity <= 0) return;

        var source = SKRect.Create(
            (float)(image.Width * clip.CropLeft),
            (float)(image.Height * clip.CropTop),
            (float)(image.Width * (1 - clip.CropLeft - clip.CropRight)),
            (float)(image.Height * (1 - clip.CropTop - clip.CropBottom)));
        var size = PlacedSize(source.Width, source.Height, frame, clip.Fit);
        var target = SKRect.Create(-size.Width / 2, -size.Height / 2, size.Width, size.Height);

        canvas.Save();
        canvas.Translate(frame.MidX + (float)x, frame.MidY + (float)y);
        canvas.RotateDegrees((float)rotation);
        canvas.Scale((float)scale * (clip.FlipHorizontal ? -1 : 1), (float)scale * (clip.FlipVertical ? -1 : 1));

        using var colorFilter = ColorFilterOf(clip.Effects);
        using var imageFilter = ImageFilterOf(clip.Effects);
        _layer.Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255));
        _layer.BlendMode = clip.Blend switch
        {
            BlendMode.Multiply => SKBlendMode.Multiply,
            BlendMode.Screen => SKBlendMode.Screen,
            BlendMode.Overlay => SKBlendMode.Overlay,
            BlendMode.Add => SKBlendMode.Plus,
            _ => SKBlendMode.SrcOver,
        };

        if (clip.Mask.IsActive)
        {
            // 마스크: 따로 한 겹에 그림을 그려 도형 모양으로 오려낸 뒤, 그 겹을 불투명도·블렌드로 아래 화면과 합친다.
            canvas.SaveLayer(_layer);
            _inner.ColorFilter = colorFilter;
            _inner.ImageFilter = imageFilter;
            canvas.DrawImage(image, source, target, Sampling, _inner);
            _inner.ColorFilter = null;
            _inner.ImageFilter = null;
            if (clip.Effects.Vignette > 0) DrawVignette(canvas, target, clip.Effects.Vignette);
            DrawMask(canvas, clip, target, time);
            canvas.Restore();
        }
        else
        {
            _layer.ColorFilter = colorFilter;
            _layer.ImageFilter = imageFilter;
            canvas.DrawImage(image, source, target, Sampling, _layer);
            _layer.ColorFilter = null;
            _layer.ImageFilter = null;
            if (clip.Effects.Vignette > 0) DrawVignette(canvas, target, clip.Effects.Vignette * opacity);
        }

        canvas.Restore();
    }

    private readonly SKPaint _inner = new();

    /// <summary>
    /// 지금 겹에 그려진 그림에서 마스크 도형의 안쪽(반전이면 바깥쪽)만 남긴다.
    /// 도형의 위치·크기는 클립 그림(target)에 대한 비율이고, 키프레임이 있으면 이 시각의 값을 쓴다.
    /// </summary>
    private static void DrawMask(SKCanvas canvas, VisualClip clip, SKRect target, TimeSpan time)
    {
        var mask = clip.Mask;
        var x = clip.ValueAt(AnimatedProperty.MaskX, mask.X, time);
        var y = clip.ValueAt(AnimatedProperty.MaskY, mask.Y, time);
        var width = Math.Max(0.001, clip.ValueAt(AnimatedProperty.MaskWidth, mask.Width, time)) * target.Width;
        var height = Math.Max(0.001, clip.ValueAt(AnimatedProperty.MaskHeight, mask.Height, time)) * target.Height;
        var center = new SKPoint(target.Left + (float)x * target.Width, target.Top + (float)y * target.Height);
        var bounds = SKRect.Create(center.X - (float)width / 2, center.Y - (float)height / 2, (float)width, (float)height);

        // 도형을 따로 한 겹에 그리고, 그 겹을 합칠 때 "도형이 있는 곳만 남기기"(반전이면 "지우기")로 합친다.
        // 이렇게 해야 도형 바깥도 함께 처리된다.
        using var combine = new SKPaint { BlendMode = mask.Inverted ? SKBlendMode.DstOut : SKBlendMode.DstIn };
        canvas.SaveLayer(combine);
        using var blur = mask.Feather > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)mask.Feather / 2) : null;
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, MaskFilter = blur };
        switch (mask.Shape)
        {
            case MaskShape.Rectangle:
                canvas.DrawRect(bounds, paint);
                break;
            case MaskShape.Ellipse:
                canvas.DrawOval(bounds, paint);
                break;
            default:
                using (var builder = new SKPathBuilder())
                {
                    // 자유 도형은 가운데가 0.5에서 벗어난 만큼 통째로 옮긴다.
                    var points = mask.Points.Select(point => new SKPoint(
                        target.Left + (float)(point.X + x - 0.5) * target.Width,
                        target.Top + (float)(point.Y + y - 0.5) * target.Height)).ToArray();
                    builder.AddPoly(points, true);
                    using var path = builder.Detach();
                    canvas.DrawPath(path, paint);
                }

                break;
        }

        canvas.Restore();
    }

    /// <summary>
    /// 시퀀스의 한 점이 클립 그림의 어디인지 돌려준다: 그림의 왼쪽 위가 (0, 0), 오른쪽 아래가 (1, 1).
    /// 미리보기에서 누른 곳을 마스크 좌표로 바꿀 때 쓴다. imageSize는 원본의 화면 크기.
    /// </summary>
    public static SKPoint ToClipSpace(VisualClip clip, SKSize imageSize, SKRect frame, TimeSpan time, SKPoint point)
    {
        var size = PlacedSize(
            (float)(imageSize.Width * (1 - clip.CropLeft - clip.CropRight)),
            (float)(imageSize.Height * (1 - clip.CropTop - clip.CropBottom)), frame, clip.Fit);
        if (!MatrixOf(clip, frame, time).TryInvert(out var inverse)) return new SKPoint(0.5f, 0.5f);

        var local = inverse.MapPoint(point);
        return new SKPoint(local.X / size.Width + 0.5f, local.Y / size.Height + 0.5f);
    }

    /// <summary>
    /// 클립 그림의 네 귀퉁이가 시퀀스의 어디에 놓이는지(왼쪽 위부터 시계 방향). 선택한 클립의 테두리를 그릴 때 쓴다.
    /// imageSize는 원본의 화면 크기.
    /// </summary>
    public static SKPoint[] Corners(VisualClip clip, SKSize imageSize, SKRect frame, TimeSpan time)
    {
        var size = PlacedSize(
            (float)(imageSize.Width * (1 - clip.CropLeft - clip.CropRight)),
            (float)(imageSize.Height * (1 - clip.CropTop - clip.CropBottom)), frame, clip.Fit);
        var (w, h) = (size.Width / 2, size.Height / 2);
        return MatrixOf(clip, frame, time).MapPoints([new(-w, -h), new(w, -h), new(w, h), new(-w, h)]);
    }

    /// <summary>Draw가 캔버스에 거는 변환(이동 → 회전 → 크기·뒤집기)과 같은 행렬.</summary>
    private static SKMatrix MatrixOf(VisualClip clip, SKRect frame, TimeSpan time)
    {
        var scale = (float)Math.Clamp(clip.ValueAt(AnimatedProperty.Scale, clip.Scale, time), 0.1, 4);
        var matrix = SKMatrix.CreateTranslation(
            frame.MidX + (float)clip.ValueAt(AnimatedProperty.X, clip.X, time),
            frame.MidY + (float)clip.ValueAt(AnimatedProperty.Y, clip.Y, time));
        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateRotationDegrees((float)clip.ValueAt(AnimatedProperty.Rotation, clip.Rotation, time)));
        return SKMatrix.Concat(matrix, SKMatrix.CreateScale(scale * (clip.FlipHorizontal ? -1 : 1), scale * (clip.FlipVertical ? -1 : 1)));
    }

    // ───── 효과와 색보정 ─────

    /// <summary>가운데는 그대로 두고 가장자리로 갈수록 검게 덮는다.</summary>
    private static void DrawVignette(SKCanvas canvas, SKRect target, double strength)
    {
        var radius = MathF.Sqrt(target.Width * target.Width + target.Height * target.Height) / 2;
        using var shader = SKShader.CreateRadialGradient(new SKPoint(0, 0), radius,
            [SKColors.Transparent, SKColors.Black.WithAlpha((byte)Math.Round(strength * 255))], [0.45f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(target, paint);
    }

    /// <summary>흐림과 선명하게. 둘 다 없으면 null.</summary>
    private static SKImageFilter? ImageFilterOf(ClipEffects effects)
    {
        var blur = effects.Blur > 0 ? SKImageFilter.CreateBlur((float)effects.Blur, (float)effects.Blur) : null;
        if (effects.Sharpen <= 0) return blur;

        // 가운데 점을 키우고 상하좌우를 빼면 경계가 또렷해진다. 합이 1이라 전체 밝기는 그대로다.
        var amount = (float)effects.Sharpen;
        float[] kernel = [0, -amount, 0, -amount, 1 + 4 * amount, -amount, 0, -amount, 0];
        var sharpen = SKImageFilter.CreateMatrixConvolution(new SKSizeI(3, 3), kernel, 1, 0, new SKPointI(1, 1),
            SKShaderTileMode.Clamp, false, blur);
        blur?.Dispose();
        return sharpen;
    }

    /// <summary>노출·색온도·밝기·대비·채도를 하나의 색 행렬로 만들고, LUT가 있으면 그 뒤에 적용한다. 아무것도 없으면 null.</summary>
    private SKColorFilter? ColorFilterOf(ClipEffects effects)
    {
        // 적용 순서: 크로마키(원래 색으로 판단) → 색보정 → LUT
        SKColorFilter? result = null;
        void Then(SKColorFilter? next)
        {
            if (next is null || result is null)
            {
                result ??= next;
                return;
            }

            var both = SKColorFilter.CreateCompose(next, result);
            next.Dispose();
            result.Dispose();
            result = both;
        }

        Then(ChromaKeyFilter(effects));
        if (effects.Exposure != 0 || effects.Temperature != 0 || effects.Brightness != 0 || effects.Contrast != 0 || effects.Saturation != 0)
            Then(SKColorFilter.CreateColorMatrix(ColorMatrix(effects)));
        if (effects.Lut is { } path && lutOf?.Invoke(path) is { } lut) Then(LutFilter(lut));
        return result;
    }

    // 색에서 밝기를 뺀 색상 성분(Cb, Cr)만 비교한다. 조명 때문에 배경의 밝기가 고르지 않아도 같은 색으로 본다.
    // 지정한 색과의 거리가 tolerance 안이면 투명, tolerance + softness부터는 그대로, 그 사이는 반투명이다.
    private const string ChromaKeyShader = """
        uniform half3 key;
        uniform half tolerance;
        uniform half softness;
        half2 chroma(half3 c) {
            return half2(dot(c, half3(-0.169, -0.331, 0.5)), dot(c, half3(0.5, -0.419, -0.081)));
        }
        half4 main(half4 color) {
            half3 c = color.a > 0 ? color.rgb / color.a : half3(0);
            half keep = smoothstep(tolerance, tolerance + softness + 0.0001, distance(chroma(c), chroma(key)));
            return half4(c * color.a * keep, color.a * keep);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect?> ChromaKeyEffect = new(() => SKRuntimeEffect.CreateColorFilter(ChromaKeyShader, out _));

    private static SKColorFilter? ChromaKeyFilter(ClipEffects effects)
    {
        if (TextRenderer.ColorOf(effects.KeyColor) is not { } key || ChromaKeyEffect.Value is not { } effect) return null;

        // 색상 성분의 거리는 가장 멀어도 1 안팎이라서 0~1의 설정값을 절반으로 줄여 쓴다.
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["key"] = new[] { key.Red / 255f, key.Green / 255f, key.Blue / 255f },
            ["tolerance"] = (float)effects.KeyTolerance / 2,
            ["softness"] = (float)effects.KeySoftness / 2,
        };
        return effect.ToColorFilter(uniforms);
    }

    /// <summary>
    /// 채널마다 (값 × 노출 × 색온도 + 밝기)를 중간 밝기 기준으로 대비만큼 벌린 뒤, 채도만큼 흑백과 섞는다.
    /// 4행 5열(R, G, B, A 행과 마지막 열의 더하는 값)이며 값의 범위는 0~1이다.
    /// </summary>
    public static float[] ColorMatrix(ClipEffects effects)
    {
        var contrast = 1 + effects.Contrast;
        var exposure = Math.Pow(2, effects.Exposure);
        double[] gain =
        [
            exposure * (1 + 0.25 * effects.Temperature) * contrast,
            exposure * contrast,
            exposure * (1 - 0.25 * effects.Temperature) * contrast,
        ];
        var offset = (effects.Brightness - 0.5) * contrast + 0.5;

        var saturation = 1 + effects.Saturation;
        double[] luma = [0.2126, 0.7152, 0.0722];
        var result = new float[20];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
                result[row * 5 + column] = (float)(((1 - saturation) * luma[column] + (row == column ? saturation : 0)) * gain[column]);
            result[row * 5 + 4] = (float)offset; // 세 채널에 같은 값을 더하므로 채도를 섞어도 그대로다
        }

        result[18] = 1; // 투명도는 그대로
        return result;
    }

    // ───── LUT ─────

    // 색의 빨강·초록으로 한 장 안의 위치를, 파랑으로 몇 번째 장인지를 정해 표에서 색을 읽는다.
    // 표는 가로로 size장을 이어 붙인 그림이다. 파랑은 앞뒤 두 장을 읽어 섞는다.
    private const string LutShader = """
        uniform shader lut;
        uniform float size;
        half4 main(half4 color) {
            half3 c = color.a > 0 ? saturate(color.rgb / color.a) : half3(0);
            float blue = c.b * (size - 1.0);
            float slice = floor(blue);
            float next = min(slice + 1.0, size - 1.0);
            float2 position = float2(c.r * (size - 1.0) + 0.5, c.g * (size - 1.0) + 0.5);
            half3 low = lut.eval(float2(position.x + slice * size, position.y)).rgb;
            half3 high = lut.eval(float2(position.x + next * size, position.y)).rgb;
            return half4(mix(low, high, half(blue - slice)) * color.a, color.a);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect?> LutEffect = new(() => SKRuntimeEffect.CreateColorFilter(LutShader, out _));
    private static readonly ConditionalWeakTable<CubeLut, SKImage> LutImages = [];

    private static SKColorFilter? LutFilter(CubeLut lut)
    {
        if (LutEffect.Value is not { } effect || lut.Size < 2 || lut.Rgb.Length < lut.Size * lut.Size * lut.Size * 3) return null;

        var image = LutImages.GetValue(lut, LutImage);
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling);
        var uniforms = new SKRuntimeEffectUniforms(effect) { ["size"] = (float)lut.Size };
        var children = new SKRuntimeEffectChildren(effect) { ["lut"] = shader };
        return effect.ToColorFilter(uniforms, children);
    }

    /// <summary>표를 그림으로 만든다: (빨강 + 파랑 × size, 초록) 위치에 그 색.</summary>
    // shortcut: 표의 색을 8비트로 담는다. 색이 계단지게 보이면 16비트 부동소수 그림으로 바꾼다.
    private static SKImage LutImage(CubeLut lut)
    {
        var size = lut.Size;
        var pixels = new byte[size * size * size * 4];
        for (var blue = 0; blue < size; blue++)
        for (var green = 0; green < size; green++)
        for (var red = 0; red < size; red++)
        {
            var from = (red + green * size + blue * size * size) * 3;
            var to = (green * size * size + blue * size + red) * 4;
            for (var channel = 0; channel < 3; channel++)
                pixels[to + channel] = (byte)Math.Round(Math.Clamp(lut.Rgb[from + channel], 0f, 1f) * 255);
            pixels[to + 3] = 255;
        }

        using var data = SKData.CreateCopy(pixels);
        return SKImage.FromPixels(new SKImageInfo(size * size, size, SKColorType.Rgba8888, SKAlphaType.Opaque), data, size * size * 4);
    }

    // ───── 크기 ─────

    /// <summary>크기 100%일 때 화면에 놓이는 크기: 맞추기는 전체가 보이게, 채우기는 화면이 꽉 차게.</summary>
    public static SKSize PlacedSize(float width, float height, SKRect bounds, FitMode fit)
    {
        var horizontal = bounds.Width / width;
        var vertical = bounds.Height / height;
        var scale = fit == FitMode.Fill ? Math.Max(horizontal, vertical) : Math.Min(horizontal, vertical);
        return new SKSize(width * scale, height * scale);
    }

    /// <summary>비율을 유지한 채 전체가 보이도록 가운데에 맞춘 영역(맞추기).</summary>
    public static SKRect Fit(float width, float height, SKRect bounds)
    {
        var size = PlacedSize(width, height, bounds, FitMode.Fit);
        return SKRect.Create(bounds.MidX - size.Width / 2, bounds.MidY - size.Height / 2, size.Width, size.Height);
    }
}
