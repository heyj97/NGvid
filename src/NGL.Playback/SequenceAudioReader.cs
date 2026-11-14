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

    public static long ToFrames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * Rate);

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

            foreach (var track in sequence.Tracks)
            {
                if (track is not AudioTrack) continue;

                foreach (var clip in track.Clips)
                {
                    if (clip is not AudioClip audio) continue;

                    var clipStart = ToFrames(audio.Range.Start);
                    var clipEnd = ToFrames(audio.Range.End);
                    var from = Math.Max(start, clipStart);
                    var to = Math.Min(end, clipEnd);
                    if (from >= to) continue;

                    var target = buffer.Slice((int)(from - start) * Channels, (int)(to - from) * Channels);
                    Mix(audio, ToFrames(audio.SourceStart) + (from - clipStart), target);
                }
            }

            _position = end;
        }
    }

    /// <summary>클립의 원본 sourceFrame 위치부터의 소리를 target에 더한다.</summary>
    private void Mix(AudioClip clip, long sourceFrame, Span<float> target)
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
