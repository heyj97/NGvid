namespace NGL.Core;

/// <summary>.ngvid 파일 하나에 해당하는 편집 데이터 전체.</summary>
public sealed class Project
{
    public const int CurrentVersion = 1;

    /// <summary>파일 형식 버전. 형식이 바뀌면 올리고 변환에 쓴다.</summary>
    public int Version { get; init; } = CurrentVersion;

    public string Name { get; set; } = "";
    public Sequence Sequence { get; } = new();
    public List<MediaAsset> Assets { get; } = [];

    public MediaAsset? FindAsset(Guid id) => Assets.Find(asset => asset.Id == id);
}
