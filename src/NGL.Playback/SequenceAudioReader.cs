using NGL.Audio;
using NGL.Core;
using NGL.Media;

namespace NGL.Playback;

/// <summary>
/// 시퀀스의 오디오 트랙에 놓인 클립들의 소리를 타임라인 위치에 맞춰 섞어 준다.
/// Seek과 Read는 서로 다른 스레드에서 불러도 된다.
/// </summary>
public sealed class SequenceAudioReader(Sequence sequence, Func<Guid, string?> pathOfAsset, object? sequenceGate = null) : IDisposable
{
    private const int Rate = IAudioDecoder.SampleRate;
    private const int Channels = IAudioDecoder.Channels;

    private sealed class Entry
    {
        public IAudioDecoder? Decoder;   // 열 수 없는 원본이면 null
        public long NextSourceFrame = -1; // 디코더가 다음에 내놓을 원본 위치
    }

    private readonly object _gate = new();
    private readonly object _sequenceGate = sequenceGate ?? new object(); // 편집과 동시에 트랙 목록을 읽지 않기 위한 잠금
    private readonly Dictionary<Guid, Entry> _entries = []; // 클립 ID별
    private float[] _scratch = [];
    private long _position; // 다음에 읽을 타임라인 위치 (샘플 프레임)

    /// <summary>방금 섞은 소리의 크기.</summary>
    public AudioMeter Meter { get; } = new();

    public static long ToFrames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * Rate);

    private static TimeSpan ToTime(long frames) => TimeSpan.FromSeconds(frames / (double)Rate);

    public void Seek(TimeSpan time)
    {
        lock (_gate) _position = Math.Max(0, ToFrames(time));
    }

    /// <summary>현재 위치부터 버퍼를 가득 채우고 그만큼 전진한다. 소리가 없는 곳은 무음이다.</summary>
    public void Read(Span<float> buffer)
    {
        buffer.Clear();
        var frames = buffer.Length / Channels;

        lock (_gate)
        lock (_sequenceGate)
        {
            var start = _position;
            var end = start + frames;

            // 솔로인 트랙이 있으면 그 트랙들만, 없으면 음소거하지 않은 트랙들이 소리를 낸다.
            var anySolo = sequence.Tracks.Any(track => track is AudioTrack { Solo: true });
            foreach (var track in sequence.Tracks)
            {
                if (track is not AudioTrack || track.Muted || (anySolo && !track.Solo)) continue;

                foreach (var clip in track.Clips)
                {
                    if (clip is not AudioClip audio) continue;

                    var clipStart = ToFrames(audio.Range.Start);
                    var clipEnd = ToFrames(audio.Range.End);
                    var from = Math.Max(start, clipStart);
                    var to = Math.Min(end, clipEnd);
                    if (from >= to) continue;

                    var target = buffer.Slice((int)(from - start) * Channels, (int)(to - from) * Channels);
                    Mix((AudioTrack)track, audio, ToFrames(audio.SourceStart) + (from - clipStart), from, target);
                }
            }

            _position = end;
        }

        Meter.Process(buffer);
        // 1을 넘는 소리는 장치에서 심하게 찢어지므로 잘라 둔다. (미터에는 자르기 전의 크기가 보인다.)
        foreach (ref var sample in buffer) sample = Math.Clamp(sample, -1f, 1f);
    }

    /// <summary>
    /// 클립의 원본 sourceFrame 위치부터의 소리에 볼륨·페이드·자동 감쇠를 곱해 target에 더한다.
    /// timelineFrame은 target의 첫 샘플이 놓이는 타임라인 위치다.
    /// </summary>
    private void Mix(AudioTrack track, AudioClip clip, long sourceFrame, long timelineFrame, Span<float> target)
    {
        if (!_entries.TryGetValue(clip.Id, out var entry))
        {
            entry = new Entry { Decoder = Open(clip.AssetId) };
            _entries.Add(clip.Id, entry);
        }

        if (entry.Decoder is null) return;

        // 이어서 읽는 경우가 아니면(처음, 탐색, 클립이 옮겨짐) 원본 위치를 다시 맞춘다.
        if (entry.NextSourceFrame != sourceFrame) entry.Decoder.Seek(sourceFrame);

        if (_scratch.Length < target.Length) _scratch = new float[target.Length];
        var samples = _scratch.AsSpan(0, target.Length);
        var read = entry.Decoder.Read(samples);
        entry.NextSourceFrame = sourceFrame + read;

        // 크기는 1/100초마다 다시 계산하고 그 사이는 고르게 잇는다.
        const int step = Rate / 100;
        for (var offset = 0; offset < read; offset += step)
        {
            var count = Math.Min(step, read - offset);
            AudioMixer.ApplyGain(samples.Slice(offset * Channels, count * Channels),
                AudioMixer.Gain(sequence, track, clip, ToTime(timelineFrame + offset)),
                AudioMixer.Gain(sequence, track, clip, ToTime(timelineFrame + offset + count)));
        }

        for (var i = 0; i < read * Channels; i++) target[i] += samples[i];
    }

    private IAudioDecoder? Open(Guid assetId)
    {
        try
        {
            return pathOfAsset(assetId) is { } path ? new FFmpegAudioDecoder(path) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values) entry.Decoder?.Dispose();
            _entries.Clear();
        }
    }
}
