namespace NGL.Core;

/// <summary>트랙 위에 올린 재료 한 조각.</summary>
public abstract class Clip
{
    private TimeRange _range;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>타임라인에서 차지하는 구간.</summary>
    public TimeRange Range
    {
        get => _range;
        set => _range = value.Start >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "클립은 0초보다 앞에 놓을 수 없습니다.");
    }

    /// <summary>같은 값을 가진 클립끼리 연결되어 있다(영상과 그 소리). 함께 선택·이동·트리밍된다.</summary>
    public Guid? LinkId { get; set; }

    /// <summary>같은 값을 가진 클립끼리 그룹이다. 함께 선택·이동·삭제된다.</summary>
    public Guid? GroupId { get; set; }
}

/// <summary>보관함의 원본(MediaAsset)을 가리키는 클립. 표시 이름은 원본의 이름을 따른다.</summary>
public abstract class MediaClip : Clip
{
    public Guid AssetId { get; set; }

    /// <summary>원본에서 재생을 시작하는 위치(앞쪽 트리밍). 이미지는 쓰지 않는다.</summary>
    public TimeSpan SourceStart { get; set; }
}

public sealed class VideoClip : MediaClip;

public sealed class AudioClip : MediaClip
{
    private double _volume = 1;

    /// <summary>볼륨. 1이 원래 크기이고 0~2(200%)까지.</summary>
    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 2);
    }

    /// <summary>클립의 시작에서 이 시간 동안 소리가 서서히 커진다.</summary>
    public TimeSpan FadeIn { get; set; }

    /// <summary>클립의 끝에서 이 시간 동안 소리가 서서히 작아진다.</summary>
    public TimeSpan FadeOut { get; set; }
}

public sealed class ImageClip : MediaClip;

/// <summary>제목·설명 같은 자유 텍스트. 영상 트랙에 올린다.</summary>
public sealed class TextClip : Clip
{
    public string Text { get; set; } = "";
}

/// <summary>자막 트랙에 올리는 자막 한 줄.</summary>
public sealed class CaptionClip : Clip
{
    public string Text { get; set; } = "";
}
