using NGL.Core;
using NGL.Media;
using SkiaSharp;

namespace NGL.Playback;

/// <summary>
/// 영상 클립이 특정 시각에 보여줄 프레임을 디코딩해서 Skia 이미지로 넘겨준다.
/// 원본 하나에 디코더 하나를 열어 둔다. 여러 스레드에서 동시에 쓸 수 없다.
/// </summary>
public sealed class VideoFrameSource(Func<Guid, string?> pathOfAsset) : IDisposable
{
    private readonly Dictionary<Guid, IVideoDecoder?> _decoders = []; // 열 수 없는 원본이면 null

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

        var sourceTime = clip.SourceStart + (timelineTime - clip.Range.Start);
        if (decoder?.GetFrameAt(sourceTime) is not { } frame) return null;

        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        return SKImage.FromPixelCopy(info, frame.Pixels, frame.Stride);
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
