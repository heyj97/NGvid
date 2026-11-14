using FFmpeg.AutoGen;

namespace NGL.Media;

public sealed record MediaInfo(TimeSpan Duration, VideoStreamInfo? Video, AudioStreamInfo? Audio);

public sealed record VideoStreamInfo(int Width, int Height, double FrameRate, string Codec);

public sealed record AudioStreamInfo(int SampleRate, int Channels, string Codec);

/// <summary>미디어 파일의 길이·해상도·fps·코덱·오디오 정보를 읽는다.</summary>
public static unsafe class MediaProbe
{
    /// <exception cref="InvalidDataException">열 수 없거나 미디어 파일이 아닐 때.</exception>
    public static MediaInfo Probe(string path)
    {
        FFmpegLibrary.EnsureLoaded();

        AVFormatContext* format = null;
        FFmpegError.ThrowIfFailed(ffmpeg.avformat_open_input(&format, path, null, null), $"파일을 열 수 없습니다 ({path})");
        try
        {
            FFmpegError.ThrowIfFailed(ffmpeg.avformat_find_stream_info(format, null), "미디어 정보를 읽을 수 없습니다");

            var duration = format->duration == ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.Zero
                : FFmpegError.ToTimeSpan(format->duration, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE });

            VideoStreamInfo? video = null;
            var videoIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIndex >= 0)
            {
                var stream = format->streams[videoIndex];
                var parameters = stream->codecpar;
                video = new VideoStreamInfo(
                    parameters->width,
                    parameters->height,
                    ToDouble(ffmpeg.av_guess_frame_rate(format, stream, null)),
                    ffmpeg.avcodec_get_name(parameters->codec_id));
            }

            AudioStreamInfo? audio = null;
            var audioIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioIndex >= 0)
            {
                var parameters = format->streams[audioIndex]->codecpar;
                audio = new AudioStreamInfo(
                    parameters->sample_rate,
                    parameters->ch_layout.nb_channels,
                    ffmpeg.avcodec_get_name(parameters->codec_id));
            }

            return new MediaInfo(duration, video, audio);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    internal static double ToDouble(AVRational value) => value.den == 0 ? 0 : value.num / (double)value.den;
}
