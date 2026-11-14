using System.Runtime.InteropServices;
using NGL.Core;

namespace NGL.Media;

/// <summary>작은 미리보기 그림. 픽셀은 BGRA 8비트이고 한 줄은 Width × 4바이트다.</summary>
public sealed record Thumbnail(int Width, int Height, byte[] Pixels);

/// <summary>보관함에 보여줄 썸네일을 만든다.</summary>
public static class ThumbnailService
{
    public const int MaxWidth = 240;

    /// <summary>영상은 길이의 10% 지점, 이미지는 그 자체를 줄인 그림. 소리이거나 열 수 없으면 null.</summary>
    public static Thumbnail? Create(string path, MediaKind kind, TimeSpan duration)
    {
        if (kind == MediaKind.Audio) return null;

        try
        {
            using var decoder = new FFmpegVideoDecoder(path);
            decoder.Downscale = decoder.Width >= MaxWidth * 4 ? 4 : decoder.Width >= MaxWidth * 2 ? 2 : 1;
            var time = kind == MediaKind.Video ? duration * 0.1 : TimeSpan.Zero;
            return decoder.GetFrameAt(time) is { } frame ? Shrink(frame, decoder.Rotation) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>프레임을 MaxWidth 이하로 줄이면서 회전 정보대로 돌린다. 점을 건너뛰며 고르는 단순한 축소다.</summary>
    private static Thumbnail Shrink(VideoFrame frame, int rotation)
    {
        var sideways = rotation % 180 != 0;
        var step = Math.Max(1, (int)Math.Ceiling((sideways ? frame.Height : frame.Width) / (double)MaxWidth));
        var sourceWidth = frame.Width / step;   // 줄인 뒤의 원본 방향 크기
        var sourceHeight = frame.Height / step;
        var width = sideways ? sourceHeight : sourceWidth;
        var height = sideways ? sourceWidth : sourceHeight;

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // 결과의 (x, y)에 올 원본 위치
                var (sx, sy) = rotation switch
                {
                    90 => (y, sourceHeight - 1 - x),
                    180 => (sourceWidth - 1 - x, sourceHeight - 1 - y),
                    270 => (sourceWidth - 1 - y, x),
                    _ => (x, y),
                };
                var color = Marshal.ReadInt32(frame.Pixels, sy * step * frame.Stride + sx * step * 4);
                MemoryMarshal.Write(pixels.AsSpan((y * width + x) * 4), in color);
            }
        }

        return new Thumbnail(width, height, pixels);
    }
}
