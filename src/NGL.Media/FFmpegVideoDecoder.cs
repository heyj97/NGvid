using FFmpeg.AutoGen;

namespace NGL.Media;

public sealed unsafe class FFmpegVideoDecoder : IVideoDecoder
{
    /// <summary>요청 시각이 현재 위치보다 이만큼 이내로 앞서 있으면 탐색하지 않고 이어서 디코딩한다.</summary>
    private static readonly TimeSpan ForwardWindow = TimeSpan.FromSeconds(1);

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;     // 현재 프레임 원본 (보통 YUV)
    private AVFrame* _next;      // 디코더에서 방금 받은 프레임
    private AVFrame* _bgra;      // 현재 프레임을 화면에 그릴 수 있게 바꾼 것
    private SwsContext* _scaler;

    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly long _startPts;
    private readonly TimeSpan _frameDuration;

    private bool _hasFrame;      // _frame에 유효한 프레임이 있는가
    private bool _converted;     // _bgra가 _frame과 같은 프레임인가
    private bool _ended;         // 파일 끝까지 디코딩했는가
    private TimeSpan _frameStart;
    private TimeSpan _frameEnd;

    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public int Rotation { get; }

    private int _downscale = 1;
    private uint _defaultScalerFlags;

    public int Downscale
    {
        get => _downscale;
        set
        {
            if (value is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == _downscale) return;
            _downscale = value;
            _converted = false;

            // 크기를 줄일 때 기본 설정은 색이 몇 단계 틀어진다. 그때만 정확한 반올림과 전체 색차 보간을 켠다.
            // (원본 크기일 때는 기본 설정이 정확하고 더 빠르다.)
            if (_scaler != null)
            {
                _scaler->flags = value == 1
                    ? _defaultScalerFlags
                    : (uint)(SwsFlags.SWS_BILINEAR | SwsFlags.SWS_ACCURATE_RND | SwsFlags.SWS_FULL_CHR_H_INT);
            }
        }
    }
    public TimeSpan Duration { get; }

    /// <exception cref="InvalidDataException">열 수 없거나 영상이 없는 파일일 때.</exception>
    public FFmpegVideoDecoder(string path)
    {
        FFmpegLibrary.EnsureLoaded();
        try
        {
            AVFormatContext* format = null;
            FFmpegError.ThrowIfFailed(ffmpeg.avformat_open_input(&format, path, null, null), $"파일을 열 수 없습니다 ({path})");
            _format = format;
            FFmpegError.ThrowIfFailed(ffmpeg.avformat_find_stream_info(_format, null), "미디어 정보를 읽을 수 없습니다");

            AVCodec* decoder = null;
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (_streamIndex < 0 || decoder == null)
                throw new InvalidDataException($"디코딩할 수 있는 영상이 없는 파일입니다 ({path})");

            // 영상 외의 스트림은 읽지 않는다.
            for (var i = 0; i < _format->nb_streams; i++)
            {
                if (i != _streamIndex) _format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }

            var stream = _format->streams[_streamIndex];
            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            FFmpegError.ThrowIfFailed(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "코덱 설정을 읽을 수 없습니다");
            _codec->thread_count = 0; // CPU 코어 수에 맞춰 자동
            FFmpegError.ThrowIfFailed(ffmpeg.avcodec_open2(_codec, decoder, null), "디코더를 열 수 없습니다");

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _next = ffmpeg.av_frame_alloc();
            _bgra = ffmpeg.av_frame_alloc();
            _scaler = ffmpeg.sws_alloc_context();
            _defaultScalerFlags = _scaler->flags;

            _timeBase = stream->time_base;
            _startPts = FFmpegError.FileStart(_format, _timeBase);

            Width = _codec->width;
            Height = _codec->height;
            Rotation = MediaProbe.Rotation(stream);
            FrameRate = MediaProbe.ToDouble(ffmpeg.av_guess_frame_rate(_format, stream, null));
            if (FrameRate <= 0) FrameRate = 30;
            _frameDuration = TimeSpan.FromSeconds(1 / FrameRate);

            Duration = stream->duration != ffmpeg.AV_NOPTS_VALUE
                ? FFmpegError.ToTimeSpan(stream->duration, _timeBase)
                : _format->duration != ffmpeg.AV_NOPTS_VALUE
                    ? FFmpegError.ToTimeSpan(_format->duration, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE })
                    : TimeSpan.Zero;
        }
        catch
        {
            Free();
            throw;
        }
    }

    public VideoFrame? GetFrameAt(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;

        var isCurrent = _hasFrame && time >= _frameStart && time < _frameEnd;
        if (!isCurrent)
        {
            var isJustAhead = _hasFrame && time >= _frameEnd && time - _frameEnd < ForwardWindow;
            if (!isJustAhead) Seek(time);

            // 요청 시각을 덮는 프레임이 나올 때까지 디코딩한다. 파일 끝이면 마지막 프레임에서 멈춘다.
            while (DecodeNext() && _frameEnd <= time) { }
        }

        return _hasFrame ? Convert() : null;
    }

    /// <summary>요청 시각 바로 앞의 키프레임으로 이동한다. 이후 DecodeNext로 정확한 프레임까지 전진한다.</summary>
    private void Seek(TimeSpan time)
    {
        var target = _startPts + FFmpegError.FromTimeSpan(time, _timeBase);
        FFmpegError.ThrowIfFailed(
            ffmpeg.av_seek_frame(_format, _streamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD), "탐색할 수 없습니다");
        ffmpeg.avcodec_flush_buffers(_codec);
        _ended = false;
        _hasFrame = false;
        _converted = false;
    }

    /// <summary>다음 프레임을 _frame에 받는다. 더 이상 프레임이 없으면 false이고 _frame은 마지막 프레임 그대로다.</summary>
    private bool DecodeNext()
    {
        if (_ended) return false;

        while (true)
        {
            // avcodec_receive_frame은 실패해도 받는 프레임을 비운다.
            // 파일 끝에서 마지막 프레임을 잃지 않도록 _next로 받아서 성공했을 때만 _frame으로 옮긴다.
            var received = ffmpeg.avcodec_receive_frame(_codec, _next);
            if (received >= 0)
            {
                ffmpeg.av_frame_unref(_frame);
                ffmpeg.av_frame_move_ref(_frame, _next);

                var pts = _frame->best_effort_timestamp;
                _frameStart = pts == ffmpeg.AV_NOPTS_VALUE
                    ? (_hasFrame ? _frameEnd : TimeSpan.Zero)
                    : FFmpegError.ToTimeSpan(pts - _startPts, _timeBase);
                var duration = _frame->duration > 0 ? FFmpegError.ToTimeSpan(_frame->duration, _timeBase) : _frameDuration;
                _frameEnd = _frameStart + duration;
                _hasFrame = true;
                _converted = false;
                return true;
            }

            if (received == ffmpeg.AVERROR_EOF)
            {
                _ended = true;
                return false;
            }

            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FFmpegError.ThrowIfFailed(received, "프레임을 디코딩할 수 없습니다");

            SendNextPacket();
        }
    }

    /// <summary>영상 패킷 하나를 디코더에 넣는다. 파일 끝이면 디코더 안에 남은 프레임을 내놓게 한다.</summary>
    private void SendNextPacket()
    {
        while (true)
        {
            if (ffmpeg.av_read_frame(_format, _packet) < 0)
            {
                ffmpeg.avcodec_send_packet(_codec, null);
                return;
            }

            try
            {
                if (_packet->stream_index != _streamIndex) continue;

                var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
                // 깨진 패킷은 건너뛴다.
                if (sent < 0 && sent != ffmpeg.AVERROR_INVALIDDATA)
                    FFmpegError.ThrowIfFailed(sent, "패킷을 디코더에 넣을 수 없습니다");
                return;
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    /// <summary>현재 프레임을 BGRA로 바꾼다. 색 공간·범위는 원본 프레임의 정보를 따른다.</summary>
    private VideoFrame Convert()
    {
        if (!_converted)
        {
            ffmpeg.av_frame_unref(_bgra);
            _bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            _bgra->width = Math.Max(1, _frame->width / _downscale);
            _bgra->height = Math.Max(1, _frame->height / _downscale);
            FFmpegError.ThrowIfFailed(ffmpeg.sws_scale_frame(_scaler, _bgra, _frame), "프레임을 변환할 수 없습니다");
            _converted = true;
        }

        return new VideoFrame((nint)_bgra->data[0], _bgra->width, _bgra->height, _bgra->linesize[0], _frameStart);
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~FFmpegVideoDecoder() => Free();

    // FFmpeg 자원은 가비지 수집 대상이 아니라서 직접 해제해야 한다.
    private void Free()
    {
        if (_scaler != null) { var scaler = _scaler; ffmpeg.sws_free_context(&scaler); _scaler = null; }
        if (_next != null) { var next = _next; ffmpeg.av_frame_free(&next); _next = null; }
        if (_bgra != null) { var bgra = _bgra; ffmpeg.av_frame_free(&bgra); _bgra = null; }
        if (_frame != null) { var frame = _frame; ffmpeg.av_frame_free(&frame); _frame = null; }
        if (_packet != null) { var packet = _packet; ffmpeg.av_packet_free(&packet); _packet = null; }
        if (_codec != null) { var codec = _codec; ffmpeg.avcodec_free_context(&codec); _codec = null; }
        if (_format != null) { var format = _format; ffmpeg.avformat_close_input(&format); _format = null; }
    }
}
