namespace NGL.Core;

public enum MediaKind
{
    Video,
    Audio,
    Image,
}

/// <summary>리소스 보관함에 등록된 원본 파일 하나.</summary>
public sealed class MediaAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MediaKind Kind { get; init; }

    /// <summary>보관함과 클립에 표시되는 이름.</summary>
    public string Name { get; set; } = "";

    /// <summary>프로젝트 폴더 기준 상대 경로. 예: media/interview.mp4</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>원본 길이. 이미지는 0.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>영상 원본에 소리가 들어 있는가.</summary>
    public bool HasAudio { get; set; }

    /// <summary>화면에 보이는 크기(픽셀). 소리이거나 예전에 가져온 원본이면 0.</summary>
    public int Width { get; set; }

    public int Height { get; set; }
}
