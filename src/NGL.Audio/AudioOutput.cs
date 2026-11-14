using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;

namespace NGL.Audio;

/// <summary>Windows 기본 출력 장치(WASAPI)로 소리를 낸다. 장치를 열 수 없으면 소리 없이 시간만 흐른다.</summary>
[SupportedOSPlatform("windows")]
public sealed class AudioOutput : IAudioOutput, IWaveProvider
{
    private const int LatencyMilliseconds = 60;

    private readonly StopwatchAudioOutput _fallback = new();
    private readonly object _readGate = new();
    private WasapiPlayer? _device;
    private AudioCallback? _read;
    private long _suppliedBytes; // Start한 뒤 장치가 가져간 양

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(IAudioOutput.SampleRate, IAudioOutput.Channels);

    /// <summary>장치를 열지 못해 소리 없이 동작 중인가.</summary>
    public bool IsSilent => _device is null;

    public void Start(AudioCallback read)
    {
        Stop();
        lock (_readGate) _read = read;
        Interlocked.Exchange(ref _suppliedBytes, 0);
        try
        {
            _device = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(LatencyMilliseconds).Build();
            _device.Init(this);
            _device.Play();
        }
        catch (Exception)
        {
            // 출력 장치가 없거나 이 형식으로 열 수 없다. 어떤 예외가 나든 재생 자체는 계속되어야 한다.
            _device?.Dispose();
            _device = null;
            _fallback.Start(read);
        }
    }

    public void Stop()
    {
        // 진행 중인 읽기가 끝나기를 기다린 뒤 끊는다. 이후 장치가 더 요청해도 무음만 나간다.
        lock (_readGate) _read = null;

        _device?.Stop();
        _device?.Dispose();
        _device = null;
        _fallback.Stop();
    }

    public TimeSpan Elapsed
    {
        get
        {
            if (_device is not { } device) return _fallback.Elapsed;

            // 장치가 알려주는 재생량을 쓰되, 장치가 가져간 양보다 많이 재생됐을 수는 없으므로 그 값을 넘지 않게 한다.
            var played = device.GetPosition() / (double)device.OutputWaveFormat.AverageBytesPerSecond;
            var supplied = Interlocked.Read(ref _suppliedBytes) / (double)WaveFormat.AverageBytesPerSecond;
            return TimeSpan.FromSeconds(Math.Min(played, supplied));
        }
    }

    int IWaveProvider.Read(Span<byte> buffer)
    {
        lock (_readGate)
        {
            if (_read is null) buffer.Clear();
            else _read(MemoryMarshal.Cast<byte, float>(buffer));
        }

        Interlocked.Add(ref _suppliedBytes, buffer.Length);
        return buffer.Length;
    }

    public void Dispose() => Stop();
}
