using System.Text.Json.Serialization;

namespace NGL.Core;

/// <summary>시작 시각과 길이로 나타낸 시간 구간. 끝 시각은 구간에 포함되지 않는다.</summary>
[method: JsonConstructor] // 구조체는 표시하지 않으면 JSON을 읽을 때 생성자를 건너뛴다.
public readonly record struct TimeRange(TimeSpan Start, TimeSpan Duration)
{
    public TimeSpan Duration { get; } = Duration >= TimeSpan.Zero
        ? Duration
        : throw new ArgumentOutOfRangeException(nameof(Duration), "길이는 0 이상이어야 합니다.");

    [JsonIgnore]
    public TimeSpan End => Start + Duration;

    public bool Contains(TimeSpan time) => time >= Start && time < End;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;
}
