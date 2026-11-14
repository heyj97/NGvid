using NGL.Audio;

namespace NGL.Playback;

/// <summary>
/// 재생 위치의 기준 시계. 실제로 스피커로 나간 소리의 양으로 시간을 재기 때문에
/// 화면이 이 시계를 따라가면 영상과 소리가 어긋나지 않는다.
/// </summary>
public sealed class MasterClock(IAudioOutput output)
{
    private TimeSpan _origin;

    public bool IsRunning { get; private set; }

    /// <summary>현재 위치. 멈춰 있으면 마지막 위치.</summary>
    public TimeSpan Position => IsRunning ? _origin + output.Elapsed : _origin;

    /// <summary>position에서부터 흐르기 시작한다. 소리는 read가 공급한다.</summary>
    public void Start(TimeSpan position, AudioCallback read)
    {
        if (IsRunning) output.Stop();
        _origin = position;
        output.Start(read);
        IsRunning = true;
    }

    /// <summary>멈추고 position에 선다.</summary>
    public void Stop(TimeSpan position)
    {
        if (IsRunning) output.Stop();
        IsRunning = false;
        _origin = position;
    }
}
