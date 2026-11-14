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
        public double Speed = 1;          // 디코더가 내놓는 소리의 배속

        // 속도 램프용: 원본의 WindowStart부터 WindowCount 프레임을 읽어 둔 것
        public float[] Window = [];
        public long WindowStart;
        public int WindowCount;
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
                    Mix((AudioTrack)track, audio, clipStart, from, target);
                }
            }

            _position = end;
        }

        Meter.Process(buffer);
        // 1을 넘는 소리는 장치에서 심하게 찢어지므로 잘라 둔다. (미터에는 자르기 전의 크기가 보인다.)
        foreach (ref var sample in buffer) sample = Math.Clamp(sample, -1f, 1f);
    }

    /// <summary>
    /// 클립의 소리에 속도·볼륨·페이드·자동 감쇠를 적용해 target에 더한다.
    /// timelineFrame은 target의 첫 샘플이 놓이는 타임라인 위치, clipStart는 클립이 시작하는 위치다.
    /// </summary>
    private void Mix(AudioTrack track, AudioClip clip, long clipStart, long timelineFrame, Span<float> target)
    {
        // 일정한 배속은 음높이를 지키는 디코더로, 속도 램프는 원래 디코더의 소리를 직접 늘이고 줄여서 만든다.
        var ramp = clip.Keyframes.ContainsKey(AnimatedProperty.Speed);
        var speed = ramp ? 1 : clip.Speed;
        if (!_entries.TryGetValue(clip.Id, out var entry) || entry.Speed != speed)
        {
            entry?.Decoder?.Dispose();
            _entries[clip.Id] = entry = new Entry { Decoder = Open(clip.AssetId, speed), Speed = speed };
        }

        if (entry.Decoder is null) return;

        if (_scratch.Length < target.Length) _scratch = new float[target.Length];
        var samples = _scratch.AsSpan(0, target.Length);
        var read = ramp
            ? ReadRamp(entry, clip, clipStart, timelineFrame, samples)
            : ReadAt(entry, ToFrames(clip.SourceStart / speed) + (timelineFrame - clipStart), samples);

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

    /// <summary>디코더의 sourceFrame 위치부터 읽는다. 이어서 읽는 경우가 아니면(처음, 탐색, 클립이 옮겨짐) 위치를 다시 맞춘다.</summary>
    private static int ReadAt(Entry entry, long sourceFrame, Span<float> samples)
    {
        if (entry.NextSourceFrame != sourceFrame) entry.Decoder!.Seek(sourceFrame);
        var read = entry.Decoder!.Read(samples);
        entry.NextSourceFrame = sourceFrame + read;
        return read;
    }

    /// <summary>속도가 변하는 클립: 이 구간에 해당하는 원본 소리를 읽어 구간 길이에 맞게 늘이거나 줄인다.</summary>
    // shortcut: 음높이가 속도를 따라 변하고, 한 번에 읽는 구간 안에서는 속도가 일정하다고 본다. 거슬리면 atempo의 속도를 구간마다 바꾸는 방식으로 간다.
    private static int ReadRamp(Entry entry, AudioClip clip, long clipStart, long timelineFrame, Span<float> samples)
    {
        var frames = samples.Length / Channels;
        double SourceAt(long frame) => (clip.SourceStart + clip.SourceOffsetAt(ToTime(frame - clipStart))).TotalSeconds * Rate;
        var (from, to) = (SourceAt(timelineFrame), SourceAt(timelineFrame + frames));
        var first = (long)Math.Floor(from);
        var needed = (int)((long)Math.Ceiling(to) - first) + 2;

        // 앞 구간에서 읽어 둔 것 중 이번 구간과 겹치는 뒷부분은 다시 쓴다.
        var keep = first >= entry.WindowStart && first <= entry.WindowStart + entry.WindowCount
            ? (int)(entry.WindowStart + entry.WindowCount - first)
            : 0;
        var count = Math.Max(keep, needed);
        if (entry.Window.Length < count * Channels) Array.Resize(ref entry.Window, count * Channels);
        var window = entry.Window;
        if (keep > 0) window.AsSpan((int)(first - entry.WindowStart) * Channels, keep * Channels).CopyTo(window);
        if (keep < needed)
        {
            var fresh = window.AsSpan(keep * Channels, (needed - keep) * Channels);
            fresh[(ReadAt(entry, first + keep, fresh) * Channels)..].Clear(); // 원본이 끝난 뒤는 무음
        }

        (entry.WindowStart, entry.WindowCount) = (first, count);

        for (var i = 0; i < frames; i++)
        {
            var position = from + (to - from) * i / frames - first;
            var index = (int)position;
            var weight = (float)(position - index);
            for (var channel = 0; channel < Channels; channel++)
            {
                samples[i * Channels + channel] = window[index * Channels + channel] * (1 - weight)
                    + window[(index + 1) * Channels + channel] * weight;
            }
        }

        return frames;
    }

    private IAudioDecoder? Open(Guid assetId, double speed)
    {
        try
        {
            if (pathOfAsset(assetId) is not { } path) return null;
            var decoder = new FFmpegAudioDecoder(path);
            return speed == 1 ? decoder : new TempoAudioDecoder(decoder, speed);
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
