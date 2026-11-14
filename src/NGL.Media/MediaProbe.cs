using FFmpeg.AutoGen;

namespace NGL.Media;

public sealed record MediaInfo(TimeSpan Duration, VideoStreamInfo? Video, AudioStreamInfo? Audio);

/// <summary>Width·Height는 회전 정보를 반영한, 화면에 보이는 크기다.</summary>
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
            // MP3 등에 들어 있는 표지 그림은 영상으로 치지 않는다.
            if (videoIndex >= 0 && (format->streams[videoIndex]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
            {
                var stream = format->streams[videoIndex];
                var parameters = stream->codecpar;
                var sideways = Rotation(stream) % 180 != 0; // 돌려서 보는 영상은 가로·세로가 바뀐다
                video = new VideoStreamInfo(
                    sideways ? parameters->height : parameters->width,
                    sideways ? parameters->width : parameters->height,
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

    /// <summary>
    /// 화면에 바로 보이려면 시계 방향으로 돌려야 하는 각도(0·90·180·270).
    /// 휴대폰 세로 촬영본은 가로로 저장하고 이 값을 함께 적어 둔다.
    /// </summary>
    internal static int Rotation(AVStream* stream)
    {
        var parameters = stream->codecpar;
        var matrix = ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (matrix == null) return 0;

        var counterClockwise = ffmpeg.av_display_rotation_get(in *(int_array9*)matrix->data);
        if (double.IsNaN(counterClockwise)) return 0;
        return ((int)Math.Round(-counterClockwise / 90) * 90 % 360 + 360) % 360;
    }

    internal static double ToDouble(AVRational value) => value.den == 0 ? 0 : value.num / (double)value.den;
}
