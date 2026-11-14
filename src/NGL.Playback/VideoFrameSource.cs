using NGL.Core;
using NGL.Media;
using SkiaSharp;

namespace NGL.Playback;

/// <summary>미리보기 화질. 값은 원본 크기를 나누는 수다.</summary>
public enum PreviewQuality
{
    Full = 1,
    Half = 2,
    Quarter = 4,
}

/// <summary>
/// 영상 클립이 특정 시각에 보여줄 프레임을 디코딩해서 Skia 이미지로 넘겨준다.
/// 원본 하나에 디코더 하나를 열어 둔다. 여러 스레드에서 동시에 쓸 수 없다.
/// </summary>
public sealed class VideoFrameSource(Func<Guid, string?> pathOfAsset) : IDisposable
{
    private readonly Dictionary<Guid, IVideoDecoder?> _decoders = []; // 열 수 없는 원본이면 null

    public PreviewQuality Quality { get; set; } = PreviewQuality.Full;

    /// <summary>
    /// 타임라인 시각에 클립이 보여줄 이미지. 원본을 열 수 없으면 null.
    /// 돌려준 이미지는 받은 쪽이 해제한다.
    /// </summary>
    public SKImage? GetImage(VideoClip clip, TimeSpan timelineTime)
    {
        if (!_decoders.TryGetValue(clip.AssetId, out var decoder))
        {
            decoder = Open(clip.AssetId);
            _decoders.Add(clip.AssetId, decoder);
        }

        if (decoder is null) return null;

        decoder.Downscale = (int)Quality;
        var sourceTime = clip.SourceStart + (timelineTime - clip.Range.Start);
        if (decoder.GetFrameAt(sourceTime) is not { } frame) return null;

        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        return decoder.Rotation == 0
            ? SKImage.FromPixelCopy(info, frame.Pixels, frame.Stride)
            : Rotated(info, frame, decoder.Rotation);
    }

    // shortcut: 회전은 CPU에서 한 번 더 그린다. 회전된 4K 원본 재생이 느리면 합성기에서 GPU로 돌리도록 바꾼다.
    private static SKImage Rotated(SKImageInfo info, VideoFrame frame, int degrees)
    {
        using var source = SKImage.FromPixels(info, frame.Pixels, frame.Stride);
        var sideways = degrees % 180 != 0;
        var (width, height) = sideways ? (info.Height, info.Width) : (info.Width, info.Height);
        using var surface = SKSurface.Create(info.WithSize(width, height));
        var canvas = surface.Canvas;
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.DrawImage(source, -info.Width / 2f, -info.Height / 2f, SKSamplingOptions.Default);
        return surface.Snapshot();
    }

    private IVideoDecoder? Open(Guid assetId)
    {
        try
        {
            return pathOfAsset(assetId) is { } path ? new FFmpegVideoDecoder(path) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var decoder in _decoders.Values) decoder?.Dispose();
        _decoders.Clear();
    }
}
