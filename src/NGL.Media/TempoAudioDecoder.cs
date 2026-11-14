using System.Globalization;
using FFmpeg.AutoGen;

namespace NGL.Media;

/// <summary>
/// 다른 디코더의 소리를 음높이는 그대로 두고 빠르게·느리게 만든다(FFmpeg의 atempo 필터).
/// 위치와 길이는 속도를 바꾼 뒤의 샘플 프레임으로 센다. 안쪽 디코더는 함께 해제된다.
/// </summary>
public sealed unsafe class TempoAudioDecoder : IAudioDecoder
{
    private const int Rate = IAudioDecoder.SampleRate;
    private const int Channels = IAudioDecoder.Channels;
    private const int FeedFrames = 4096;
    private const int HalfWindow = 1024;

    private readonly IAudioDecoder _inner;
    private readonly double _tempo;
    private readonly float[] _feed = new float[FeedFrames * Channels];

    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;
    private AVFrame* _frame;

    private float[] _pending = new float[Rate * Channels];
    private int _pendingStart;
    private int _pendingCount;
    private long _fed;
    private bool _flushed; // 안쪽 디코더가 끝나서 필터에 끝을 알렸다

    /// <param name="tempo">속도(0.25~4). 2면 두 배 빠르다.</param>
    public TempoAudioDecoder(IAudioDecoder inner, double tempo)
    {
        _inner = inner;
        _tempo = tempo;
        _frame = ffmpeg.av_frame_alloc();
        try
        {
            BuildGraph();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public TimeSpan Duration => _inner.Duration / _tempo;

    private void BuildGraph()
    {
        FreeGraph();

        // atempo 하나는 0.5배까지만 느려지므로 그보다 느리면 둘을 잇는다.
        var step = (_tempo < 0.5 ? Math.Sqrt(_tempo) : _tempo).ToString("0.######", CultureInfo.InvariantCulture);
        var tempo = _tempo < 0.5 ? $"atempo={step},atempo={step}" : $"atempo={step}";
        var description = $"abuffer=sample_rate={Rate}:sample_fmt=flt:channel_layout=stereo:time_base=1/{Rate},{tempo},abuffersink";

        _graph = ffmpeg.avfilter_graph_alloc();
        AVFilterInOut* inputs = null, outputs = null;
        var parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, description, &inputs, &outputs, null);
        ffmpeg.avfilter_inout_free(&inputs);
        ffmpeg.avfilter_inout_free(&outputs);
        FFmpegError.ThrowIfFailed(parsed, "속도 필터를 만들 수 없습니다");
        FFmpegError.ThrowIfFailed(ffmpeg.avfilter_graph_config(_graph, null), "속도 필터를 시작할 수 없습니다");

        // 설명에 적은 순서대로 만들어진다: 처음이 입력, 마지막이 출력.
        _source = _graph->filters[0];
        _sink = _graph->filters[_graph->nb_filters - 1];
        _fed = 0;
        _flushed = false;

        // atempo는 처음 창의 절반(48kHz에서 1024프레임)만큼을 내보내지 않아 소리가 그만큼 일찍 나온다.
        // 그만큼의 무음을 먼저 넣어 제자리에 맞춘다. 둘을 이으면 둘째 것의 몫은 첫째의 속도만큼 줄어든다.
        var lead = _tempo < 0.5 ? (int)(HalfWindow * (1 + Math.Sqrt(_tempo))) : HalfWindow;
        Array.Clear(_feed, 0, lead * Channels);
        Push(lead);
    }

    /// <summary>_feed의 앞 frames 프레임을 필터에 넣는다.</summary>
    private void Push(int frames)
    {
        _frame->nb_samples = frames;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _frame->sample_rate = Rate;
        _frame->pts = _fed;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, Channels);
        FFmpegError.ThrowIfFailed(ffmpeg.av_frame_get_buffer(_frame, 0), "소리 버퍼를 만들 수 없습니다");
        _feed.AsSpan(0, frames * Channels).CopyTo(new Span<float>(_frame->data[0], frames * Channels));
        FFmpegError.ThrowIfFailed(ffmpeg.av_buffersrc_add_frame(_source, _frame), "속도 필터에 소리를 넣을 수 없습니다");
        _fed += frames;
    }

    public void Seek(long sampleFrame)
    {
        ObjectDisposedException.ThrowIf(_frame == null, this);
        _inner.Seek((long)Math.Round(Math.Max(0, sampleFrame) * _tempo));
        BuildGraph(); // 필터 안에 남은 이전 위치의 소리를 버린다
        _pendingStart = 0;
        _pendingCount = 0;
    }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_frame == null, this);

        var wanted = buffer.Length / Channels * Channels;
        var written = 0;
        while (written < wanted)
        {
            if (_pendingCount == 0 && !FilterMore()) break;

            var count = Math.Min(_pendingCount, wanted - written);
            _pending.AsSpan(_pendingStart, count).CopyTo(buffer[written..]);
            _pendingStart += count;
            _pendingCount -= count;
            written += count;
        }

        return written / Channels;
    }

    /// <summary>필터에서 나온 소리를 _pending에 담는다. 더 나올 것이 없으면 false.</summary>
    private bool FilterMore()
    {
        while (true)
        {
            var received = ffmpeg.av_buffersink_get_frame(_sink, _frame);
            if (received >= 0)
            {
                var count = _frame->nb_samples * Channels;
                if (_pending.Length < count) _pending = new float[count];
                new Span<float>(_frame->data[0], count).CopyTo(_pending);
                _pendingStart = 0;
                _pendingCount = count;
                ffmpeg.av_frame_unref(_frame);
                if (count > 0) return true;
                continue;
            }

            if (received == ffmpeg.AVERROR_EOF || _flushed) return false;
            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN)) FFmpegError.ThrowIfFailed(received, "소리의 속도를 바꿀 수 없습니다");

            Feed();
        }
    }

    private void Feed()
    {
        var frames = _inner.Read(_feed);
        if (frames > 0) Push(frames);

        if (frames < FeedFrames)
        {
            // 원본이 끝났다. 필터에 남은 소리를 마저 내보내게 한다.
            ffmpeg.av_buffersrc_add_frame(_source, null);
            _flushed = true;
        }
    }

    private void FreeGraph()
    {
        if (_graph == null) return;
        var graph = _graph;
        ffmpeg.avfilter_graph_free(&graph);
        _graph = null;
    }

    public void Dispose()
    {
        FreeGraph();
        if (_frame != null) { var frame = _frame; ffmpeg.av_frame_free(&frame); _frame = null; }
        _inner.Dispose();
        GC.SuppressFinalize(this);
    }

    ~TempoAudioDecoder()
    {
        FreeGraph();
        if (_frame != null) { var frame = _frame; ffmpeg.av_frame_free(&frame); _frame = null; }
    }
}
