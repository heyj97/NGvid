namespace NGL.Core;

/// <summary>시퀀스 화면 규격. 새 해상도는 프리셋을 추가해서 지원한다.</summary>
public sealed record SequencePreset(string Name, int Width, int Height)
{
    public static SequencePreset Landscape { get; } = new("16:9", 1920, 1080);
    public static SequencePreset Portrait { get; } = new("9:16", 1080, 1920);
    public static SequencePreset Square { get; } = new("1:1", 1080, 1080);

    public static IReadOnlyList<SequencePreset> All { get; } = [Landscape, Portrait, Square];
}
