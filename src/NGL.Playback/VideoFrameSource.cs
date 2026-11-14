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
    private sealed class Entry
    {
        public IVideoDecoder? Decoder; // 열 수 없는 원본이면 null
        public SKImage? Image;
        public TimeSpan ImageTime;
    }

    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>
    /// 타임라인 시각에 클립이 보여줄 이미지. 원본을 열 수 없으면 null.
    /// 돌려준 이미지는 이 객체가 소유하며 같은 원본의 다음 프레임을 요청하면 해제된다.
    /// </summary>
    public SKImage? GetImage(VideoClip clip, TimeSpan timelineTime)
    {
        if (!_entries.TryGetValue(clip.AssetId, out var entry))
        {
            entry = new Entry { Decoder = Open(clip.AssetId) };
            _entries.Add(clip.AssetId, entry);
        }

        if (entry.Decoder is null) return null;

        var sourceTime = clip.SourceStart + (timelineTime - clip.Range.Start);
        if (entry.Decoder.GetFrameAt(sourceTime) is not { } frame) return null;

        if (entry.Image is null || entry.ImageTime != frame.Time)
        {
            entry.Image?.Dispose();
            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            entry.Image = SKImage.FromPixelCopy(info, frame.Pixels, frame.Stride);
            entry.ImageTime = frame.Time;
        }

        return entry.Image;
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
        foreach (var entry in _entries.Values)
        {
            entry.Image?.Dispose();
            entry.Decoder?.Dispose();
        }

        _entries.Clear();
    }
}
