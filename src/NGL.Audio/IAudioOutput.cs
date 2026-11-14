using System.Diagnostics;

namespace NGL.Audio;

/// <summary>버퍼를 48kHz 스테레오 float 샘플(좌·우 번갈아)로 빠짐없이 채운다. 오디오 스레드에서 불린다.</summary>
public delegate void AudioCallback(Span<float> buffer);

/// <summary>소리를 내보내는 장치. 재생 시계의 기준이 된다.</summary>
public interface IAudioOutput : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    /// <summary>재생을 시작한다. 장치가 필요할 때마다 read를 불러 샘플을 가져간다.</summary>
    void Start(AudioCallback read);

    /// <summary>재생을 멈춘다. 돌아온 뒤에는 read가 불리지 않는다.</summary>
    void Stop();

    /// <summary>Start한 뒤 실제로 스피커로 나간 소리의 길이. 멈춰 있으면 0.</summary>
    TimeSpan Elapsed { get; }
}

/// <summary>소리를 내지 않고 시간만 재는 출력. 오디오 장치가 없을 때와 테스트에 쓴다.</summary>
public sealed class StopwatchAudioOutput : IAudioOutput
{
    private readonly Stopwatch _stopwatch = new();

    public void Start(AudioCallback read) => _stopwatch.Restart();

    public void Stop() => _stopwatch.Reset();

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Dispose() => _stopwatch.Reset();
}
