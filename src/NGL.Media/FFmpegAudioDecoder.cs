using FFmpeg.AutoGen;

namespace NGL.Media;

public sealed unsafe class FFmpegAudioDecoder : IAudioDecoder
{
    private const int Rate = IAudioDecoder.SampleRate;
    private const int Channels = IAudioDecoder.Channels;

    /// <summary>탐색할 때 목표보다 이만큼 앞에서부터 디코딩한다. 압축 오디오는 앞 패킷이 있어야 첫 소리가 온전하다.</summary>
    private const long PrerollFrames = Rate / 10;

    /// <summary>탐색한 곳에 소리가 없을 때 채우는 무음의 최대 길이.</summary>
    private const long MaxGapFrames = Rate * 60;

    private static readonly AVRational SampleBase = new() { num = 1, den = Rate };

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _resampler;

    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly long _startPts;
    private readonly bool _mono;

    // 디코딩해 둔 샘플. _pending[_pendingStart .. _pendingStart + _pendingCount)가 아직 읽지 않은 부분이다.
    private float[] _pending = new float[Rate * Channels];
    private int _pendingStart;
    private int _pendingCount;
    private float[] _converted = new float[8192 * Channels];

    private long _target;        // 탐색 목표 위치
    private bool _aligning;      // 탐색 뒤 첫 프레임을 목표 위치에 맞춰야 하는가
    private long _skipFrames;    // 목표 위치 전이라서 버릴 샘플 프레임 수
    private bool _ended;

    public TimeSpan Duration { get; }

    /// <exception cref="InvalidDataException">열 수 없거나 소리가 없는 파일일 때.</exception>
    public FFmpegAudioDecoder(string path)
    {
        FFmpegLibrary.EnsureLoaded();
        try
        {
            AVFormatContext* format = null;
            FFmpegError.ThrowIfFailed(ffmpeg.avformat_open_input(&format, path, null, null), $"파일을 열 수 없습니다 ({path})");
            _format = format;
            FFmpegError.ThrowIfFailed(ffmpeg.avformat_find_stream_info(_format, null), "미디어 정보를 읽을 수 없습니다");

            AVCodec* decoder = null;
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
            if (_streamIndex < 0 || decoder == null)
                throw new InvalidDataException($"디코딩할 수 있는 소리가 없는 파일입니다 ({path})");

            for (var i = 0; i < _format->nb_streams; i++)
            {
                if (i != _streamIndex) _format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }

            var stream = _format->streams[_streamIndex];
            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            FFmpegError.ThrowIfFailed(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "코덱 설정을 읽을 수 없습니다");
            FFmpegError.ThrowIfFailed(ffmpeg.avcodec_open2(_codec, decoder, null), "디코더를 열 수 없습니다");

            // 채널 배치 정보가 없는 파일은 채널 수에 맞는 기본 배치로 본다.
            if (_codec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                ffmpeg.av_channel_layout_default(&_codec->ch_layout, _codec->ch_layout.nb_channels);

            // 모노는 변환기에 스테레오로 펼치게 하면 0.707배로 줄어든다.
            // 모노 그대로 받아서 양쪽 채널에 원래 크기로 복사한다.
            _mono = _codec->ch_layout.nb_channels == 1;
            AVChannelLayout output;
            ffmpeg.av_channel_layout_default(&output, _mono ? 1 : Channels);
            SwrContext* resampler = null;
            FFmpegError.ThrowIfFailed(
                ffmpeg.swr_alloc_set_opts2(&resampler, &output, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate,
                    &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate, 0, null),
                "오디오 변환기를 만들 수 없습니다");
            _resampler = resampler;
            FFmpegError.ThrowIfFailed(ffmpeg.swr_init(_resampler), "오디오 변환기를 시작할 수 없습니다");

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();

            _timeBase = stream->time_base;
            _startPts = FFmpegError.FileStart(_format, _timeBase);
            Duration = _format->duration != ffmpeg.AV_NOPTS_VALUE
                ? FFmpegError.ToTimeSpan(_format->duration, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE })
                : TimeSpan.Zero;

            _aligning = true; // 파일의 소리가 0초보다 늦게 시작하면 그만큼 무음으로 채운다.
        }
        catch
        {
            Free();
            throw;
        }
    }

    public void Seek(long sampleFrame)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);
        if (sampleFrame < 0) sampleFrame = 0;

        var from = Math.Max(0, sampleFrame - PrerollFrames);
        var pts = _startPts + ffmpeg.av_rescale_q(from, SampleBase, _timeBase);
        // 끝을 넘는 위치로는 탐색이 실패할 수 있다. 그때는 읽을 것이 없는 상태로 둔다.
        _ended = ffmpeg.av_seek_frame(_format, _streamIndex, pts, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0;
        ffmpeg.avcodec_flush_buffers(_codec);

        // 변환기 안에 남은 이전 위치의 샘플을 비운다.
        ffmpeg.swr_close(_resampler);
        FFmpegError.ThrowIfFailed(ffmpeg.swr_init(_resampler), "오디오 변환기를 시작할 수 없습니다");

        _pendingStart = 0;
        _pendingCount = 0;
        _target = sampleFrame;
        _aligning = true;
        _skipFrames = 0;
    }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_format == null, this);

        var wanted = buffer.Length / Channels * Channels;
        var written = 0;
        while (written < wanted)
        {
            if (_pendingCount == 0 && !DecodeMore()) break;

            var count = Math.Min(_pendingCount, wanted - written);
            _pending.AsSpan(_pendingStart, count).CopyTo(buffer[written..]);
            _pendingStart += count;
            _pendingCount -= count;
            written += count;
        }

        return written / Channels;
    }

    /// <summary>프레임 하나를 디코딩해 _pending에 더한다. 파일이 끝났으면 false.</summary>
    private bool DecodeMore()
    {
        while (!_ended)
        {
            var received = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (received >= 0)
            {
                AppendFrame();
                ffmpeg.av_frame_unref(_frame);
                if (_pendingCount > 0) return true;
                continue; // 전부 목표 위치 전이라 버려졌다.
            }

            if (received == ffmpeg.AVERROR_EOF) break;
            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FFmpegError.ThrowIfFailed(received, "소리를 디코딩할 수 없습니다");

            SendNextPacket();
        }

        _ended = true;
        return false;
    }

    private void AppendFrame()
    {
        if (_aligning)
        {
            // 탐색은 목표보다 앞의 패킷에 도착한다. 이 프레임이 목표에서 얼마나 떨어져 있는지 계산해서
            // 앞쪽이면 그만큼 버리고, 뒤쪽이면(소리가 없는 구간) 그만큼 무음을 넣는다.
            _aligning = false;
            var pts = _frame->best_effort_timestamp;
            var framePosition = pts == ffmpeg.AV_NOPTS_VALUE
                ? _target
                // 0초 앞의 준비용 샘플(Opus 등)은 디코더가 이미 잘라냈는데 시각은 음수로 남는 경우가 있다.
                : Math.Max(0, ffmpeg.av_rescale_q(pts - _startPts, _timeBase, SampleBase));

            _skipFrames = _target - framePosition;
            if (_skipFrames < 0)
            {
                var silence = (int)Math.Min(-_skipFrames, MaxGapFrames) * Channels;
                Reserve(silence).Clear();
                _pendingCount += silence;
                _skipFrames = 0;
            }
        }

        var capacity = ffmpeg.swr_get_out_samples(_resampler, _frame->nb_samples);
        if (_converted.Length < capacity * Channels) _converted = new float[capacity * Channels];

        int frames;
        fixed (float* output = _converted)
        {
            var outputPlane = (byte*)output;
            frames = FFmpegError.ThrowIfFailed(
                ffmpeg.swr_convert(_resampler, &outputPlane, capacity, _frame->extended_data, _frame->nb_samples),
                "소리를 변환할 수 없습니다");
        }

        if (_mono)
        {
            // 뒤에서부터 채워야 아직 읽지 않은 모노 샘플을 덮어쓰지 않는다.
            for (var i = frames - 1; i >= 0; i--)
                _converted[i * 2] = _converted[i * 2 + 1] = _converted[i];
        }

        var skipped = (int)Math.Min(_skipFrames, frames);
        _skipFrames -= skipped;

        var samples = _converted.AsSpan(skipped * Channels, (frames - skipped) * Channels);
        samples.CopyTo(Reserve(samples.Length));
        _pendingCount += samples.Length;
    }

    /// <summary>_pending 끝에 count개를 쓸 자리를 마련해 돌려준다.</summary>
    private Span<float> Reserve(int count)
    {
        if (_pendingStart + _pendingCount + count > _pending.Length)
        {
            var needed = _pendingCount + count;
            var target = needed > _pending.Length ? new float[Math.Max(needed, _pending.Length * 2)] : _pending;
            _pending.AsSpan(_pendingStart, _pendingCount).CopyTo(target);
            _pending = target;
            _pendingStart = 0;
        }

        return _pending.AsSpan(_pendingStart + _pendingCount, count);
    }

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

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~FFmpegAudioDecoder() => Free();

    private void Free()
    {
        if (_resampler != null) { var resampler = _resampler; ffmpeg.swr_free(&resampler); _resampler = null; }
        if (_frame != null) { var frame = _frame; ffmpeg.av_frame_free(&frame); _frame = null; }
        if (_packet != null) { var packet = _packet; ffmpeg.av_packet_free(&packet); _packet = null; }
        if (_codec != null) { var codec = _codec; ffmpeg.avcodec_free_context(&codec); _codec = null; }
        if (_format != null) { var format = _format; ffmpeg.avformat_close_input(&format); _format = null; }
    }
}
