namespace NGL.Media;

/// <summary>소리의 크기 변화. Peaks[i]는 i번째 1/100초 구간에서 가장 큰 크기(0~1)다.</summary>
public sealed record Waveform(float[] Peaks)
{
    public const int PeaksPerSecond = 100;
}

/// <summary>타임라인의 오디오 클립에 그릴 파형을 만든다.</summary>
public static class WaveformService
{
    /// <summary>파일 전체를 디코딩하므로 화면 스레드가 아닌 곳에서 부른다. 소리가 없거나 열 수 없으면 null.</summary>
    public static Waveform? Create(string path)
    {
        const int framesPerPeak = IAudioDecoder.SampleRate / Waveform.PeaksPerSecond;
        try
        {
            using var decoder = new FFmpegAudioDecoder(path);
            var peaks = new List<float>();
            var buffer = new float[framesPerPeak * IAudioDecoder.Channels * 64];
            int frames;
            do
            {
                frames = decoder.Read(buffer);
                for (var start = 0; start < frames; start += framesPerPeak)
                {
                    var count = Math.Min(framesPerPeak, frames - start) * IAudioDecoder.Channels;
                    var peak = 0f;
                    foreach (var sample in buffer.AsSpan(start * IAudioDecoder.Channels, count))
                        peak = Math.Max(peak, Math.Abs(sample));
                    peaks.Add(Math.Min(peak, 1f));
                }
            } while (frames == buffer.Length / IAudioDecoder.Channels);

            return new Waveform(peaks.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
