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

    public void Render(SKCanvas canvas, Sequence sequence, TimeSpan time, Func<Clip, SKImage?> imageOf)
    {
        var frame = SKRect.Create(sequence.Preset.Width, sequence.Preset.Height);

        canvas.Save();
        canvas.ClipRect(frame);
        canvas.DrawRect(frame, _background);

        // 목록의 첫 트랙이 맨 위에 보여야 하므로 마지막 트랙부터 그린다.
        for (var i = sequence.Tracks.Count - 1; i >= 0; i--)
        {
            if (sequence.Tracks[i] is not VideoTrack { Hidden: false } track) continue;

            foreach (var clip in track.Clips)
            {
                if (!clip.Range.Contains(time)) continue;

                var image = imageOf(clip);
                if (image is not null) canvas.DrawImage(image, Fit(image.Width, image.Height, frame), Sampling);
            }
        }

        canvas.Restore();
    }

    /// <summary>비율을 유지한 채 전체가 보이도록 가운데에 맞춘 영역(맞추기).</summary>
    public static SKRect Fit(float width, float height, SKRect bounds)
    {
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var size = new SKSize(width * scale, height * scale);
        return SKRect.Create(bounds.MidX - size.Width / 2, bounds.MidY - size.Height / 2, size.Width, size.Height);
    }
}
