using System.Text.Json.Serialization;

namespace NGL.Core;

/// <summary>편집하는 결과물 한 편과 그 규격(화면비·fps). 프로젝트당 하나.</summary>
public sealed class Sequence
{
    public static IReadOnlyList<int> SupportedFrameRates { get; } = [30, 60];

    private SequencePreset _preset = SequencePreset.Landscape;
    private int _frameRate = 30;

    public SequencePreset Preset
    {
        get => _preset;
        set => _preset = value ?? throw new ArgumentNullException(nameof(value));
    }

    public int FrameRate
    {
        get => _frameRate;
        set => _frameRate = SupportedFrameRates.Contains(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "지원하지 않는 프레임레이트입니다.");
    }

    /// <summary>목록의 순서가 트랙 순서다. 첫 트랙이 화면 맨 위에 그려진다.</summary>
    public List<Track> Tracks { get; } = [];

    /// <summary>가장 늦게 끝나는 클립의 끝 시각.</summary>
    [JsonIgnore]
    public TimeSpan Duration => Tracks
        .SelectMany(track => track.Clips)
        .Select(clip => clip.Range.End)
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();

    // 시간은 fps와 무관한 TimeSpan으로 저장한다. 작업 중 fps를 바꿔도 클립 위치가 변하지 않는다.
    // TimeAt은 올림, FrameAt은 내림이라서 FrameAt(TimeAt(n)) == n이 항상 성립한다.

    /// <summary>주어진 시각에 보이는 프레임 번호.</summary>
    public long FrameAt(TimeSpan time) => time.Ticks * FrameRate / TimeSpan.TicksPerSecond;

    /// <summary>프레임이 시작하는 시각.</summary>
    public TimeSpan TimeAt(long frame) =>
        new((frame * TimeSpan.TicksPerSecond + FrameRate - 1) / FrameRate);

    /// <summary>가장 가까운 프레임 경계로 맞춘 시각.</summary>
    public TimeSpan SnapToFrame(TimeSpan time) =>
        TimeAt((long)Math.Round(time.Ticks * (double)FrameRate / TimeSpan.TicksPerSecond));
}
