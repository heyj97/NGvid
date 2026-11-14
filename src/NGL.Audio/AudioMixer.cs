using NGL.Core;

namespace NGL.Audio;

/// <summary>오디오 클립의 소리 크기 계산: 클립·트랙 볼륨, 페이드, 자동 감쇠. 미리보기와 내보내기가 같은 계산을 쓴다.</summary>
public static class AudioMixer
{
    /// <summary>자동 감쇠가 걸리고 풀리는 데 걸리는 시간. 갑자기 바뀌면 소리가 튄다.</summary>
    public static readonly TimeSpan DuckRamp = TimeSpan.FromSeconds(0.3);

    /// <summary>타임라인의 time에 이 클립에 곱할 크기: 트랙 볼륨 × 클립 볼륨 × 페이드 × 자동 감쇠.</summary>
    public static double Gain(Sequence sequence, AudioTrack track, AudioClip clip, TimeSpan time) =>
        track.Volume * ClipGain(clip, time) * DuckGain(sequence, track, time);

    /// <summary>클립 볼륨에 페이드 인·아웃을 곱한 값.</summary>
    public static double ClipGain(AudioClip clip, TimeSpan time)
    {
        var gain = Math.Clamp(clip.ValueAt(AnimatedProperty.Volume, clip.Volume, time), 0, 2); // 키프레임이 있으면 이 시각의 볼륨
        var fromStart = time - clip.Range.Start;
        var toEnd = clip.Range.End - time;
        if (clip.FadeIn > TimeSpan.Zero && fromStart < clip.FadeIn) gain *= Math.Clamp(fromStart / clip.FadeIn, 0, 1);
        if (clip.FadeOut > TimeSpan.Zero && toEnd < clip.FadeOut) gain *= Math.Clamp(toEnd / clip.FadeOut, 0, 1);
        return gain;
    }

    /// <summary>
    /// 자동 감쇠로 줄어든 비율. 기준 트랙에 클립이 있는 동안 DuckLevel, 없으면 1이고,
    /// 클립의 앞뒤 DuckRamp 동안 그 사이를 서서히 오간다.
    /// </summary>
    public static double DuckGain(Sequence sequence, AudioTrack track, TimeSpan time)
    {
        if (track.DuckUnder is not { } id || sequence.Tracks.Find(other => other.Id == id) is not { Muted: false } trigger)
            return 1;

        // 기준 트랙의 소리가 "얼마나 있는가"(0~1): 클립 안이면 1, 클립에서 DuckRamp만큼 떨어지면 0.
        var presence = 0.0;
        foreach (var clip in trigger.Clips)
        {
            var distance = time < clip.Range.Start ? clip.Range.Start - time
                : time >= clip.Range.End ? time - clip.Range.End
                : TimeSpan.Zero;
            presence = Math.Max(presence, 1 - Math.Clamp(distance / DuckRamp, 0, 1));
        }

        return 1 - presence * (1 - track.DuckLevel);
    }

    /// <summary>좌·우가 번갈아 놓인 샘플에 from에서 to로 고르게 변하는 크기를 곱한다.</summary>
    public static void ApplyGain(Span<float> stereo, double from, double to)
    {
        if (from == 1 && to == 1) return;

        var frames = stereo.Length / 2;
        for (var i = 0; i < frames; i++)
        {
            var gain = (float)(from + (to - from) * i / frames);
            stereo[i * 2] *= gain;
            stereo[i * 2 + 1] *= gain;
        }
    }
}

/// <summary>재생 중인 소리의 좌·우 크기를 잰다. 오디오 스레드가 Process로 넣고 화면이 Read로 읽는다.</summary>
public sealed class AudioMeter
{
    private float _left;
    private float _right;

    /// <summary>방금 섞은 소리(좌·우 번갈아)에서 가장 큰 크기를 기억한다.</summary>
    public void Process(ReadOnlySpan<float> stereo)
    {
        float left = 0, right = 0;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            left = Math.Max(left, Math.Abs(stereo[i]));
            right = Math.Max(right, Math.Abs(stereo[i + 1]));
        }

        // 화면이 읽어 가기 전에 여러 번 불려도 가장 큰 값이 남게 한다.
        _left = Math.Max(Volatile.Read(ref _left), left);
        _right = Math.Max(Volatile.Read(ref _right), right);
    }

    /// <summary>마지막으로 읽은 뒤의 가장 큰 크기(1이 최대, 넘으면 소리가 찢어진다)를 돌려주고 다시 잰다.</summary>
    public (float Left, float Right) Read() =>
        (Interlocked.Exchange(ref _left, 0), Interlocked.Exchange(ref _right, 0));
}
