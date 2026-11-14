using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using NGL.Core;
using NGL.Media;
using NGL.Playback;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Export;

public enum ExportQuality
{
    Low,
    Medium,
    High,
}

/// <summary>내보내기 설정. 해상도와 fps는 시퀀스의 것을 그대로 쓴다.</summary>
public sealed record ExportSettings(string OutputPath, int VideoBitrate, int AudioBitrate = 192_000)
{
    /// <summary>
    /// 품질 단계에 맞는 영상 비트레이트(bps). 1080p 30fps에서 낮음 8 · 보통 12 · 높음 20 Mbps이고,
    /// 60fps면 1.5배, 화소 수가 다르면 그 비율만큼 조정한다.
    /// </summary>
    public static int BitrateFor(ExportQuality quality, Sequence sequence)
    {
        var megabits = quality switch { ExportQuality.Low => 8.0, ExportQuality.High => 20.0, _ => 12.0 };
        var pixels = sequence.Preset.Width * sequence.Preset.Height / (1920.0 * 1080.0);
        return (int)(megabits * 1_000_000 * pixels * (sequence.FrameRate > 30 ? 1.5 : 1));
    }
}

public interface IExporter
{
    /// <summary>프로젝트의 시퀀스를 파일로 내보낸다. 화면 없이도 동작한다.</summary>
    /// <param name="progress">0~1의 진행률.</param>
    /// <exception cref="InvalidOperationException">내보낼 내용이 없을 때.</exception>
    /// <exception cref="OperationCanceledException">취소했을 때. 만들던 파일은 지운다.</exception>
    void Export(Project project, string projectFolder, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken cancellation = default);
}

/// <summary>
/// H.264 + AAC MP4로 내보낸다. 화면은 미리보기와 같은 합성기로 한 프레임씩 그리고,
/// 소리는 미리보기와 같은 믹서로 섞어서 FFmpeg로 인코딩한다.
/// </summary>
public sealed unsafe class Mp4Exporter : IExporter
{
    private const int SampleRate = IAudioDecoder.SampleRate;

    public void Export(Project project, string projectFolder, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var sequence = project.Sequence;
        if (sequence.Duration <= TimeSpan.Zero) throw new InvalidOperationException("내보낼 내용이 없습니다.");

        FFmpegLibrary.EnsureLoaded();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath))!);

        var session = new Session();
        try
        {
            Run(session, project, projectFolder, settings, progress, cancellation);
            session.Free();
        }
        catch
        {
            // 실패하거나 취소하면 만들다 만 파일을 남기지 않는다.
            session.Free();
            File.Delete(settings.OutputPath);
            throw;
        }
    }

    /// <summary>FFmpeg 자원들. 어디서 실패하든 한꺼번에 해제할 수 있게 모아 둔다.</summary>
    private sealed class Session
    {
        public AVFormatContext* Format;
        public AVCodecContext* Video;
        public AVCodecContext* Audio;
        public AVFrame* Bgra;   // 합성기가 그린 그림
        public AVFrame* Yuv;    // 인코더에 넣을 형식으로 바꾼 그림
        public AVFrame* Sound;
        public AVPacket* Packet;
        public SwsContext* Scaler;

        public void Free()
        {
            if (Scaler != null) { var scaler = Scaler; ffmpeg.sws_free_context(&scaler); Scaler = null; }
            if (Packet != null) { var packet = Packet; ffmpeg.av_packet_free(&packet); Packet = null; }
            if (Sound != null) { var frame = Sound; ffmpeg.av_frame_free(&frame); Sound = null; }
            if (Yuv != null) { var frame = Yuv; ffmpeg.av_frame_free(&frame); Yuv = null; }
            if (Bgra != null) { var frame = Bgra; ffmpeg.av_frame_free(&frame); Bgra = null; }
            if (Audio != null) { var codec = Audio; ffmpeg.avcodec_free_context(&codec); Audio = null; }
            if (Video != null) { var codec = Video; ffmpeg.avcodec_free_context(&codec); Video = null; }
            if (Format != null)
            {
                if (Format->pb != null) ffmpeg.avio_closep(&Format->pb);
                ffmpeg.avformat_free_context(Format);
                Format = null;
            }
        }
    }

    private static void Run(Session s, Project project, string projectFolder, ExportSettings settings,
        IProgress<double>? progress, CancellationToken cancellation)
    {
        var sequence = project.Sequence;
        var width = sequence.Preset.Width;
        var height = sequence.Preset.Height;
        var fps = sequence.FrameRate;
        var frameCount = sequence.FrameAt(sequence.Duration - new TimeSpan(1)) + 1;
        var sampleCount = SequenceAudioReader.ToFrames(sequence.TimeAt(frameCount)); // 영상과 같은 길이의 소리

        string? PathOf(Guid id) => project.FindAsset(id) is { } asset ? ProjectPaths.AssetFile(projectFolder, asset) : null;
        using var frames = new VideoFrameSource(PathOf);
        using var audio = new SequenceAudioReader(sequence, PathOf);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var compositor = new SkiaCompositor();

        // ── 출력 파일과 인코더 ──
        AVFormatContext* format = null;
        Check(ffmpeg.avformat_alloc_output_context2(&format, null, "mp4", settings.OutputPath), "출력 형식을 준비할 수 없습니다");
        s.Format = format;
        var globalHeader = (format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

        var videoCodec = ffmpeg.avcodec_find_encoder_by_name("libx264");
        if (videoCodec == null) videoCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        if (videoCodec == null) throw new InvalidOperationException("H.264 인코더를 찾을 수 없습니다.");

        s.Video = ffmpeg.avcodec_alloc_context3(videoCodec);
        s.Video->width = width;
        s.Video->height = height;
        s.Video->time_base = new AVRational { num = 1, den = fps };
        s.Video->framerate = new AVRational { num = fps, den = 1 };
        s.Video->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        s.Video->bit_rate = settings.VideoBitrate;
        s.Video->gop_size = fps * 2; // 2초마다 키프레임
        // HD 영상의 표준 색 규격(BT.709, 제한 범위)으로 표시해 둔다.
        s.Video->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        s.Video->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        s.Video->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        s.Video->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        if (globalHeader) s.Video->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        ffmpeg.av_opt_set(s.Video->priv_data, "preset", "medium", 0);
        Check(ffmpeg.avcodec_open2(s.Video, videoCodec, null), "영상 인코더를 열 수 없습니다");
        var videoStream = NewStream(format, s.Video);

        var audioCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (audioCodec == null) throw new InvalidOperationException("AAC 인코더를 찾을 수 없습니다.");

        s.Audio = ffmpeg.avcodec_alloc_context3(audioCodec);
        s.Audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        s.Audio->sample_rate = SampleRate;
        s.Audio->bit_rate = settings.AudioBitrate;
        s.Audio->time_base = new AVRational { num = 1, den = SampleRate };
        ffmpeg.av_channel_layout_default(&s.Audio->ch_layout, IAudioDecoder.Channels);
        if (globalHeader) s.Audio->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        Check(ffmpeg.avcodec_open2(s.Audio, audioCodec, null), "오디오 인코더를 열 수 없습니다");
        var audioStream = NewStream(format, s.Audio);

        Check(ffmpeg.avio_open(&format->pb, settings.OutputPath, ffmpeg.AVIO_FLAG_WRITE), "출력 파일을 열 수 없습니다");
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0); // 웹에서 바로 재생되도록 정보를 파일 앞에 둔다
        var header = ffmpeg.avformat_write_header(format, &options);
        ffmpeg.av_dict_free(&options);
        Check(header, "파일 머리말을 쓸 수 없습니다");

        // ── 프레임 버퍼 ──
        s.Packet = ffmpeg.av_packet_alloc();
        s.Scaler = ffmpeg.sws_alloc_context();
        // 색을 정확히 옮기도록 정밀한 변환을 쓴다. (기본 설정은 밝은 색이 몇 단계 어두워진다.)
        s.Scaler->flags = (uint)(SwsFlags.SWS_BILINEAR | SwsFlags.SWS_ACCURATE_RND | SwsFlags.SWS_FULL_CHR_H_INP);

        s.Bgra = ffmpeg.av_frame_alloc();
        s.Bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        s.Bgra->width = width;
        s.Bgra->height = height;
        s.Bgra->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        // 원본과 결과의 색 기준을 같게 적어 둔다. 다르면 변환기가 감마까지 바꿔서 밝기가 달라진다.
        s.Bgra->colorspace = AVColorSpace.AVCOL_SPC_RGB;
        s.Bgra->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        s.Bgra->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        Check(ffmpeg.av_frame_get_buffer(s.Bgra, 0), "프레임 버퍼를 만들 수 없습니다");

        s.Yuv = ffmpeg.av_frame_alloc();
        s.Yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        s.Yuv->width = width;
        s.Yuv->height = height;
        s.Yuv->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        s.Yuv->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        s.Yuv->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        s.Yuv->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        Check(ffmpeg.av_frame_get_buffer(s.Yuv, 0), "프레임 버퍼를 만들 수 없습니다");

        var soundFrames = s.Audio->frame_size > 0 ? s.Audio->frame_size : 1024;
        s.Sound = ffmpeg.av_frame_alloc();
        s.Sound->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        s.Sound->sample_rate = SampleRate;
        s.Sound->nb_samples = soundFrames;
        ffmpeg.av_channel_layout_copy(&s.Sound->ch_layout, &s.Audio->ch_layout);
        Check(ffmpeg.av_frame_get_buffer(s.Sound, 0), "오디오 버퍼를 만들 수 없습니다");
        var mixed = new float[soundFrames * IAudioDecoder.Channels];
        long samplesWritten = 0;

        // 소리를 through까지(샘플 수) 인코딩한다.
        void EncodeAudioUntil(long through)
        {
            while (samplesWritten < through)
            {
                var count = (int)Math.Min(soundFrames, sampleCount - samplesWritten);
                if (count <= 0) return;

                audio.Read(mixed); // 끝을 넘는 부분은 무음으로 채워진다
                Check(ffmpeg.av_frame_make_writable(s.Sound), "오디오 버퍼에 쓸 수 없습니다");
                var left = (float*)s.Sound->data[0];
                var right = (float*)s.Sound->data[1];
                for (var i = 0; i < count; i++)
                {
                    left[i] = mixed[i * 2];
                    right[i] = mixed[i * 2 + 1];
                }

                s.Sound->nb_samples = count; // 마지막 프레임은 짧을 수 있다
                s.Sound->pts = samplesWritten;
                Encode(s, s.Audio, s.Sound, audioStream);
                samplesWritten += count;
            }
        }

        // ── 한 프레임씩 그려서 인코딩 ──
        for (long n = 0; n < frameCount; n++)
        {
            cancellation.ThrowIfCancellationRequested();
            var time = sequence.TimeAt(n);

            DrawFrame(surface.Canvas, compositor, sequence, frames, time);
            CopyPixels(surface, s.Bgra);

            Check(ffmpeg.av_frame_make_writable(s.Yuv), "프레임 버퍼에 쓸 수 없습니다");
            Check(ffmpeg.sws_scale_frame(s.Scaler, s.Yuv, s.Bgra), "프레임을 변환할 수 없습니다");
            s.Yuv->pts = n;
            Encode(s, s.Video, s.Yuv, videoStream);

            // 소리는 영상과 같은 시각까지만 앞서가게 해서 파일 안에서 나란히 놓이게 한다.
            EncodeAudioUntil(SequenceAudioReader.ToFrames(sequence.TimeAt(n + 1)));
            progress?.Report((n + 1) / (double)frameCount);
        }

        EncodeAudioUntil(sampleCount);
        Encode(s, s.Video, null, videoStream); // 인코더 안에 남은 프레임을 내보낸다
        Encode(s, s.Audio, null, audioStream);
        Check(ffmpeg.av_write_trailer(format), "파일을 마무리할 수 없습니다");
    }

    private static AVStream* NewStream(AVFormatContext* format, AVCodecContext* codec)
    {
        var stream = ffmpeg.avformat_new_stream(format, null);
        if (stream == null) throw new InvalidOperationException("스트림을 만들 수 없습니다.");
        stream->time_base = codec->time_base;
        Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, codec), "스트림 정보를 쓸 수 없습니다");
        return stream;
    }

    /// <summary>그 시각의 영상·이미지 클립을 디코딩해서 합성기로 그린다. (재생 엔진의 화면 준비와 같은 과정)</summary>
    private static void DrawFrame(SKCanvas canvas, SkiaCompositor compositor, Sequence sequence, VideoFrameSource frames, TimeSpan time)
    {
        var images = new Dictionary<Guid, SKImage>();
        try
        {
            foreach (var clip in sequence.Tracks.OfType<VideoTrack>().Where(track => !track.Hidden)
                         .SelectMany(track => track.Clips).Where(clip => clip.Range.Contains(time)))
            {
                var image = clip switch
                {
                    VideoClip video => frames.GetImage(video, time),
                    ImageClip still => frames.GetImage(still),
                    _ => null,
                };
                if (image is not null) images[clip.Id] = image;
            }

            compositor.Render(canvas, sequence, time, clip => images.GetValueOrDefault(clip.Id));
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private static void CopyPixels(SKSurface surface, AVFrame* target)
    {
        Check(ffmpeg.av_frame_make_writable(target), "프레임 버퍼에 쓸 수 없습니다");
        using var pixels = surface.PeekPixels();
        var source = (byte*)pixels.GetPixels();
        var rowBytes = pixels.Width * 4;
        for (var y = 0; y < pixels.Height; y++)
        {
            Buffer.MemoryCopy(source + (long)y * pixels.RowBytes, target->data[0] + (long)y * target->linesize[0], rowBytes, rowBytes);
        }
    }

    /// <summary>프레임을 인코더에 넣고, 나온 패킷을 파일에 쓴다. frame이 null이면 인코더에 남은 것을 모두 내보낸다.</summary>
    private static void Encode(Session s, AVCodecContext* codec, AVFrame* frame, AVStream* stream)
    {
        Check(ffmpeg.avcodec_send_frame(codec, frame), "프레임을 인코딩할 수 없습니다");
        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(codec, s.Packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF) return;
            Check(received, "인코딩 결과를 받을 수 없습니다");

            // 영상 패킷에 길이가 없으면 파일의 마지막 프레임이 잘려 나간다. 한 프레임 길이를 적어 준다.
            if (s.Packet->duration == 0 && codec->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) s.Packet->duration = 1;
            ffmpeg.av_packet_rescale_ts(s.Packet, codec->time_base, stream->time_base);
            s.Packet->stream_index = stream->index;
            Check(ffmpeg.av_interleaved_write_frame(s.Format, s.Packet), "파일에 쓸 수 없습니다");
        }
    }

    private static void Check(int result, string action)
    {
        if (result >= 0) return;

        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(result, buffer, size);
        throw new IOException($"{action}: {Marshal.PtrToStringUTF8((nint)buffer)} ({result})");
    }
}
