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

/// <summary>겹치는 아래 화면과 색을 섞는 방식.</summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Add,
}

/// <summary>시퀀스와 비율이 다른 원본을 화면에 놓는 방식.</summary>
public enum FitMode
{
    /// <summary>전체가 보이게 맞춘다. 남는 곳은 비어 있다.</summary>
    Fit,

    /// <summary>화면을 꽉 채운다. 넘치는 부분은 잘린다.</summary>
    Fill,
}

/// <summary>화면에 그려지는 클립(영상·이미지)의 위치·크기·모양.</summary>
public abstract class VisualClip : MediaClip
{
    private double _scale = 1;
    private double _opacity = 1;
    private double _cropLeft, _cropTop, _cropRight, _cropBottom;

    /// <summary>화면 가운데에서 오른쪽으로 옮긴 거리(시퀀스 픽셀).</summary>
    public double X { get; set; }

    /// <summary>화면 가운데에서 아래로 옮긴 거리(시퀀스 픽셀).</summary>
    public double Y { get; set; }

    /// <summary>크기 배율. 1이 화면 맞춤 그대로이고 0.1~4(10~400%)까지.</summary>
    public double Scale
    {
        get => _scale;
        set => _scale = Math.Clamp(value, 0.1, 4);
    }

    /// <summary>시계 방향 회전(도).</summary>
    public double Rotation { get; set; }

    /// <summary>불투명도. 1이면 완전히 보이고 0이면 투명하다.</summary>
    public double Opacity
    {
        get => _opacity;
        set => _opacity = Math.Clamp(value, 0, 1);
    }

    public BlendMode Blend { get; set; }
    public FitMode Fit { get; set; }

    // 가장자리에서 잘라낼 비율(0~0.95). 마주 보는 두 쪽을 합쳐 전부 잘라낼 수는 없다.
    public double CropLeft { get => _cropLeft; set => _cropLeft = Math.Clamp(value, 0, 0.95 - _cropRight); }
    public double CropTop { get => _cropTop; set => _cropTop = Math.Clamp(value, 0, 0.95 - _cropBottom); }
    public double CropRight { get => _cropRight; set => _cropRight = Math.Clamp(value, 0, 0.95 - _cropLeft); }
    public double CropBottom { get => _cropBottom; set => _cropBottom = Math.Clamp(value, 0, 0.95 - _cropTop); }

    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
}

public sealed class VideoClip : VisualClip;

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

public sealed class ImageClip : VisualClip;

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
