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

    /// <summary>속성별 키프레임(시간순). 키프레임이 없는 속성은 고정된 값을 쓴다.</summary>
    public Dictionary<AnimatedProperty, List<Keyframe>> Keyframes { get; } = [];

    /// <summary>타임라인의 time에 속성의 값. 키프레임이 없으면 fixedValue.</summary>
    public double ValueAt(AnimatedProperty property, double fixedValue, TimeSpan time) =>
        Keyframes.TryGetValue(property, out var keys) && keys.Count > 0
            ? Animation.Evaluate(keys, time - Range.Start)
            : fixedValue;

    /// <summary>키프레임을 찍는다. 같은 시각에 이미 있으면 바꾼다. time은 클립의 시작에서 잰 시간.</summary>
    public void SetKeyframe(AnimatedProperty property, Keyframe keyframe)
    {
        if (!Keyframes.TryGetValue(property, out var keys)) Keyframes[property] = keys = [];
        keys.RemoveAll(key => key.Time == keyframe.Time);
        keys.Add(keyframe);
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    /// <summary>그 시각의 키프레임을 지운다. 마지막 하나까지 지우면 속성은 다시 고정된 값을 쓴다.</summary>
    public bool RemoveKeyframe(AnimatedProperty property, TimeSpan time)
    {
        if (!Keyframes.TryGetValue(property, out var keys) || keys.RemoveAll(key => key.Time == time) == 0) return false;
        if (keys.Count == 0) Keyframes.Remove(property);
        return true;
    }

    /// <summary>모든 키프레임을 offset만큼 옮긴다. 클립의 시작이 바뀌어도 움직임이 화면 내용과 어긋나지 않게 할 때 쓴다.</summary>
    public void ShiftKeyframes(TimeSpan offset)
    {
        foreach (var keys in Keyframes.Values)
        {
            for (var i = 0; i < keys.Count; i++) keys[i] = keys[i] with { Time = keys[i].Time + offset };
        }
    }
}

/// <summary>보관함의 원본(MediaAsset)을 가리키는 클립. 표시 이름은 원본의 이름을 따른다.</summary>
public abstract class MediaClip : Clip
{
    public Guid AssetId { get; set; }

    /// <summary>원본에서 재생을 시작하는 위치(앞쪽 트리밍). 이미지는 쓰지 않는다.</summary>
    public TimeSpan SourceStart { get; set; }

    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4;

    private double _speed = 1;

    /// <summary>재생 속도. 1이 원래 속도이고 0.25~4배까지. 속도 키프레임이 있으면 그쪽을 따른다.</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinSpeed, MaxSpeed);
    }

    /// <summary>클립이 시작하고 elapsed가 지나는 동안 원본이 흐른 시간.</summary>
    public TimeSpan SourceOffsetAt(TimeSpan elapsed) =>
        Keyframes.TryGetValue(AnimatedProperty.Speed, out var keys) && keys.Count > 0
            ? TimeSpan.FromSeconds(Animation.Integrate(keys, elapsed))
            : elapsed * Speed;
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

    /// <summary>효과와 색보정.</summary>
    public ClipEffects Effects { get; } = new();

    /// <summary>일부만 보이게 하는 마스크.</summary>
    public ClipMask Mask { get; } = new();

    /// <summary>앞 클립에서 이 클립으로 넘어올 때의 전환. 이 클립의 처음 TransitionDuration 동안 진행된다.</summary>
    public TransitionKind Transition { get; set; }

    public TimeSpan TransitionDuration { get; set; } = Transitions.DefaultDuration;
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

/// <summary>글자의 모양과 위치. 텍스트 클립과 자막이 함께 쓴다.</summary>
public sealed class TextStyle
{
    public string Font { get; set; } = "Malgun Gothic";

    /// <summary>글자 크기(시퀀스 픽셀).</summary>
    public double Size { get; set; } = 72;

    public bool Bold { get; set; } = true;

    /// <summary>글자 색. #RRGGBB 또는 #AARRGGBB.</summary>
    public string Color { get; set; } = "#FFFFFF";

    public string OutlineColor { get; set; } = "#000000";

    /// <summary>외곽선 두께(픽셀). 0이면 없다.</summary>
    public double OutlineWidth { get; set; } = 6;

    public string ShadowColor { get; set; } = "#000000";

    /// <summary>그림자를 오른쪽 아래로 옮기는 거리(픽셀). 0이면 없다.</summary>
    public double ShadowOffset { get; set; }

    /// <summary>글자 뒤에 까는 배경 박스의 색. null이면 없다.</summary>
    public string? BoxColor { get; set; }

    /// <summary>화면 가운데에서 오른쪽으로 옮긴 거리(픽셀).</summary>
    public double X { get; set; }

    /// <summary>화면 가운데에서 아래로 옮긴 거리(픽셀).</summary>
    public double Y { get; set; }

    /// <summary>말하는 단어를 강조하는 색. null이면 강조하지 않는다. 단어별 시간은 글자 수 비율로 나눈다.</summary>
    public string? HighlightColor { get; set; }

    public TextStyle Clone() => (TextStyle)MemberwiseClone();
}

/// <summary>이름을 붙여 저장해 둔 글자 스타일.</summary>
public sealed record TextStylePreset(string Name, TextStyle Style)
{
    /// <summary>처음 쓸 때 들어 있는 프리셋.</summary>
    public static List<TextStylePreset> Defaults() =>
    [
        new("기본", new TextStyle()),
        new("팝업", new TextStyle { Size = 88, Color = "#FFE14D", OutlineWidth = 10 }),
        new("박스", new TextStyle { OutlineWidth = 0, BoxColor = "#CC000000" }),
    ];
}

/// <summary>글자를 그리는 클립.</summary>
public abstract class TextualClip : Clip
{
    private TextStyle _style = new();

    public string Text { get; set; } = "";

    public TextStyle Style
    {
        get => _style;
        set => _style = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>제목·설명 같은 자유 텍스트. 영상 트랙에 올린다.</summary>
public sealed class TextClip : TextualClip;

/// <summary>자막 트랙에 올리는 자막 한 줄.</summary>
public sealed class CaptionClip : TextualClip;
