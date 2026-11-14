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

public sealed class SkiaCompositor : IFrameCompositor
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
            if (sequence.Tracks[i] is not VideoTrack { Hidden: false } track) continue;

            foreach (var clip in track.Clips)
            {
                if (!clip.Range.Contains(time) || imageOf(clip) is not { } image) continue;

                if (clip is VisualClip visual) Draw(canvas, image, visual, frame, time);
                else canvas.DrawImage(image, Fit(image.Width, image.Height, frame), Sampling);
            }
        }

        canvas.Restore();
    }

    /// <summary>클립의 자르기·화면 맞춤·위치·회전·크기·뒤집기·불투명도·블렌드를 적용해 그린다.</summary>
    private void Draw(SKCanvas canvas, SKImage image, VisualClip clip, SKRect frame, TimeSpan time)
    {
        // 키프레임이 있는 속성은 이 시각의 값을 쓴다.
        var x = clip.ValueAt(AnimatedProperty.X, clip.X, time);
        var y = clip.ValueAt(AnimatedProperty.Y, clip.Y, time);
        var scale = Math.Clamp(clip.ValueAt(AnimatedProperty.Scale, clip.Scale, time), 0.1, 4);
        var rotation = clip.ValueAt(AnimatedProperty.Rotation, clip.Rotation, time);
        var opacity = Math.Clamp(clip.ValueAt(AnimatedProperty.Opacity, clip.Opacity, time), 0, 1);

        var source = SKRect.Create(
            (float)(image.Width * clip.CropLeft),
            (float)(image.Height * clip.CropTop),
            (float)(image.Width * (1 - clip.CropLeft - clip.CropRight)),
            (float)(image.Height * (1 - clip.CropTop - clip.CropBottom)));
        var size = PlacedSize(source.Width, source.Height, frame, clip.Fit);

        canvas.Save();
        canvas.Translate(frame.MidX + (float)x, frame.MidY + (float)y);
        canvas.RotateDegrees((float)rotation);
        canvas.Scale((float)scale * (clip.FlipHorizontal ? -1 : 1), (float)scale * (clip.FlipVertical ? -1 : 1));

        _layer.Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255));
        _layer.BlendMode = clip.Blend switch
        {
            BlendMode.Multiply => SKBlendMode.Multiply,
            BlendMode.Screen => SKBlendMode.Screen,
            BlendMode.Overlay => SKBlendMode.Overlay,
            BlendMode.Add => SKBlendMode.Plus,
            _ => SKBlendMode.SrcOver,
        };
        canvas.DrawImage(image, source, SKRect.Create(-size.Width / 2, -size.Height / 2, size.Width, size.Height), Sampling, _layer);
        canvas.Restore();
    }

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
