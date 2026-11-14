using System.Runtime.InteropServices;
using NGL.Audio;
using NGL.Core;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Playback;

/// <summary>
/// 시퀀스를 재생·일시정지·탐색한다.
/// 소리는 오디오 스레드가, 영상 디코딩은 이 엔진의 작업 스레드가 맡고, 화면은 Render로 마지막에 준비된 프레임을 그린다.
/// 재생 위치는 오디오 장치의 시계(MasterClock)를 따른다.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    /// <summary>화면에 그릴 준비가 끝난 한 프레임 분량의 이미지.</summary>
    private sealed class PreparedFrame(TimeSpan time, Dictionary<Guid, SKImage> images) : IDisposable
    {
        public TimeSpan Time { get; } = time;
        public Dictionary<Guid, SKImage> Images { get; } = images; // 클립 ID별 (실행 취소로 클립 객체가 바뀌어도 찾을 수 있게)

        public void Dispose()
        {
            foreach (var image in Images.Values) image.Dispose();
        }
    }

    private readonly Sequence _sequence;
    private readonly IFrameCompositor _compositor = new SkiaCompositor();
    private readonly VideoFrameSource _video; // 작업 스레드만 쓴다
    private readonly SequenceAudioReader _audio;
    private readonly IAudioOutput _output;
    private readonly MasterClock _clock;

    private readonly object _stateGate = new(); // 시계와 반복 구간
    private readonly object _frameGate = new(); // _frame
    private readonly object _sequenceGate = new(); // 시퀀스의 트랙·클립 목록
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;

    private PreparedFrame? _frame;
    private TimeRange? _loopRange;
    private volatile bool _invalidated;
    private volatile PreviewQuality _quality = PreviewQuality.Full;
    private volatile bool _disposed;

    /// <summary>새 프레임이 준비됐다. 작업 스레드에서 불린다.</summary>
    public event Action? FrameReady;

    /// <summary>재생이 끝에 닿아 스스로 멈췄다. 작업 스레드에서 불린다.</summary>
    public event Action? Ended;

    public PlaybackEngine(Sequence sequence, Func<Guid, string?> pathOfAsset, IAudioOutput output)
    {
        _sequence = sequence;
        _video = new VideoFrameSource(pathOfAsset);
        _audio = new SequenceAudioReader(sequence, pathOfAsset, _sequenceGate);
        _output = output;
        _clock = new MasterClock(output);

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "NGvid 영상 준비" };
        _worker.Start();
    }

    public bool IsPlaying
    {
        get { lock (_stateGate) return _clock.IsRunning; }
    }

    /// <summary>현재 위치. 0과 마지막 프레임 사이다.</summary>
    public TimeSpan Position
    {
        get { lock (_stateGate) return Clamp(_clock.Position); }
    }

    /// <summary>재생이 이 구간의 끝에 닿으면 시작으로 돌아간다. null이면 반복하지 않는다.</summary>
    public TimeRange? LoopRange
    {
        get { lock (_stateGate) return _loopRange; }
        set { lock (_stateGate) _loopRange = value; }
    }

    /// <summary>재생 중인 소리의 좌·우 크기.</summary>
    public AudioMeter Meter => _audio.Meter;

    /// <summary>미리보기 화질. 낮추면 디코딩한 프레임을 작게 만들어 재생이 가벼워진다.</summary>
    public PreviewQuality Quality
    {
        get => _quality;
        set
        {
            _quality = value;
            Invalidate();
        }
    }

    /// <summary>마지막 프레임이 시작하는 시각.</summary>
    public TimeSpan LastFrameTime =>
        _sequence.TimeAt(Math.Max(0, _sequence.FrameAt(Duration - new TimeSpan(1))));

    private TimeSpan Duration
    {
        get { lock (_sequenceGate) return _sequence.Duration; }
    }

    /// <summary>
    /// 시퀀스의 트랙·클립을 바꾼다. 디코딩·오디오 스레드가 목록을 읽는 중이 아닐 때 실행되고, 끝나면 화면을 다시 준비한다.
    /// change 안에서는 모델만 바꾸고 이 엔진의 다른 기능은 부르지 않는다.
    /// </summary>
    public void Edit(Action change)
    {
        lock (_sequenceGate) change();
        Invalidate();
    }

    public void Play()
    {
        lock (_stateGate)
        {
            if (_clock.IsRunning) return;

            var from = _clock.Position;
            if (from >= LastFrameTime) from = _loopRange?.Start ?? TimeSpan.Zero; // 끝에서 누르면 처음부터
            StartAt(from);
        }

        _wake.Set();
    }

    public void Pause()
    {
        lock (_stateGate)
        {
            if (!_clock.IsRunning) return;
            _clock.Stop(FrameStart(Clamp(_clock.Position)));
        }

        _wake.Set();
    }

    /// <summary>가장 가까운 프레임으로 이동한다. 재생 중이면 그 위치에서 계속 재생한다.</summary>
    public void Seek(TimeSpan time)
    {
        lock (_stateGate)
        {
            var target = Clamp(_sequence.SnapToFrame(time < TimeSpan.Zero ? TimeSpan.Zero : time));
            if (_clock.IsRunning) StartAt(target);
            else _clock.Stop(target);
        }

        _wake.Set();
    }

    /// <summary>멈추고 프레임 단위로 이동한다. 음수면 뒤로 간다.</summary>
    public void Step(int frames)
    {
        lock (_stateGate)
        {
            var frame = _sequence.FrameAt(Clamp(_clock.Position)) + frames;
            _clock.Stop(Clamp(_sequence.TimeAt(Math.Max(0, frame))));
        }

        _wake.Set();
    }

    /// <summary>시퀀스 내용이 바뀌었을 때 현재 프레임을 다시 준비하게 한다.</summary>
    public void Invalidate()
    {
        _invalidated = true;
        _wake.Set();
    }

    /// <summary>마지막에 준비된 프레임을 시퀀스 좌표의 캔버스에 그린다. 화면 스레드에서 부른다.</summary>
    public void Render(SKCanvas canvas)
    {
        lock (_frameGate)
        {
            var frame = _frame;
            _compositor.Render(canvas, _sequence, frame?.Time ?? TimeSpan.Zero,
                clip => frame?.Images.GetValueOrDefault(clip.Id));
        }
    }

    private void StartAt(TimeSpan position)
    {
        _audio.Seek(position);
        _clock.Start(position, _audio.Read);
    }

    private TimeSpan Clamp(TimeSpan time)
    {
        var last = LastFrameTime;
        return time < TimeSpan.Zero ? TimeSpan.Zero : time > last ? last : time;
    }

    /// <summary>주어진 시각에 보이는 프레임이 시작하는 시각.</summary>
    private TimeSpan FrameStart(TimeSpan time) => _sequence.TimeAt(_sequence.FrameAt(time));

    private void WorkerLoop()
    {
        var preciseTimer = false;
        try
        {
            while (!_disposed)
            {
                var ended = false;
                bool playing;
                TimeSpan position;

                lock (_stateGate)
                {
                    playing = _clock.IsRunning;
                    position = _clock.Position;

                    if (playing)
                    {
                        if (_loopRange is { } loop && loop.Duration > TimeSpan.Zero && position >= loop.End)
                        {
                            StartAt(Clamp(loop.Start));
                            position = _clock.Position;
                        }
                        else if (position >= Duration)
                        {
                            _clock.Stop(LastFrameTime);
                            position = LastFrameTime;
                            playing = false;
                            ended = true;
                        }
                    }

                    position = Clamp(position);
                }

                if (ended) Ended?.Invoke();

                // Windows의 기본 타이머는 약 15ms 단위라서 60fps 프레임 간격(16.7ms)을 맞출 수 없다.
                // 재생하는 동안만 1ms 단위로 올린다.
                if (playing != preciseTimer)
                {
                    preciseTimer = playing;
                    SetPreciseTimer(playing);
                }

                var time = FrameStart(position);
                if (_frame is null || _frame.Time != time || _invalidated)
                {
                    _invalidated = false;
                    Prepare(time);
                    FrameReady?.Invoke();
                }

                if (!playing)
                {
                    _wake.WaitOne();
                    continue;
                }

                // 다음 프레임 경계까지 기다린다. 그 사이 탐색·일시정지가 오면 바로 깬다.
                var untilNext = _sequence.TimeAt(_sequence.FrameAt(time) + 1) - Position;
                _wake.WaitOne(Math.Clamp((int)Math.Ceiling(untilNext.TotalMilliseconds), 1, 10));
            }
        }
        finally
        {
            if (preciseTimer) SetPreciseTimer(false);
        }
    }

    /// <summary>주어진 시각의 영상 프레임들을 디코딩해 _frame으로 바꿔 끼운다.</summary>
    private void Prepare(TimeSpan time)
    {
        _video.Quality = _quality;
        _video.BeginFrame();
        var images = new Dictionary<Guid, SKImage>();

        // 목록은 잠근 채로 훑어 두고, 오래 걸리는 디코딩은 잠금 밖에서 한다.
        List<Clip> visible;
        lock (_sequenceGate)
        {
            visible = _sequence.Tracks.OfType<VideoTrack>()
                .Where(track => !track.Hidden)
                .SelectMany(track => track.Clips)
                .Where(clip => clip.Range.Contains(time))
                .ToList();
        }

        {
            foreach (var clip in visible)
            {
                try
                {
                    var image = clip switch
                    {
                        VideoClip video => _video.GetImage(video, time),
                        ImageClip still => _video.GetImage(still),
                        _ => null,
                    };
                    if (image is not null) images[clip.Id] = image;
                }
                catch (InvalidDataException)
                {
                    // 깨진 구간은 비워 둔다.
                }
            }
        }

        PreparedFrame? old;
        lock (_frameGate)
        {
            old = _frame;
            _frame = new PreparedFrame(time, images);
        }

        old?.Dispose();
    }

    private static void SetPreciseTimer(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (enabled) timeBeginPeriod(1);
        else timeEndPeriod(1);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _worker.Join();

        lock (_stateGate) _clock.Stop(TimeSpan.Zero);
        _audio.Dispose();
        _video.Dispose();
        lock (_frameGate)
        {
            _frame?.Dispose();
            _frame = null;
        }

        _wake.Dispose();
    }
}
