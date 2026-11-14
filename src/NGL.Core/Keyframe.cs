namespace NGL.Core;

/// <summary>키프레임을 찍을 수 있는 속성.</summary>
public enum AnimatedProperty
{
    X,
    Y,
    Scale,
    Rotation,
    Opacity,
    Volume,

    // 마스크의 가운데와 크기
    MaskX,
    MaskY,
    MaskWidth,
    MaskHeight,
}

/// <summary>키프레임 사이에서 값이 변하는 속도 곡선.</summary>
public enum Easing
{
    /// <summary>일정한 속도.</summary>
    Linear,

    /// <summary>천천히 시작.</summary>
    EaseIn,

    /// <summary>천천히 끝.</summary>
    EaseOut,

    /// <summary>천천히 시작해서 천천히 끝.</summary>
    EaseInOut,
}

/// <summary>"이 시점에 이 값". Time은 클립의 시작에서 잰 시간이고, Easing은 다음 키프레임까지 가는 곡선이다.</summary>
public sealed record Keyframe(TimeSpan Time, double Value, Easing Easing = Easing.Linear);

public static class Animation
{
    /// <summary>
    /// 시각 time의 값. 첫 키프레임 전에는 첫 값, 마지막 키프레임 뒤에는 마지막 값이고
    /// 그 사이는 앞 키프레임의 이징대로 잇는다. keys는 시간순이어야 한다.
    /// </summary>
    public static double Evaluate(IReadOnlyList<Keyframe> keys, TimeSpan time)
    {
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;

        var next = 1;
        while (keys[next].Time <= time) next++;
        var (from, to) = (keys[next - 1], keys[next]);
        var progress = Ease(from.Easing, (time - from.Time) / (to.Time - from.Time));
        return from.Value + (to.Value - from.Value) * progress;
    }

    /// <summary>0~1의 진행 정도를 이징 곡선에 맞게 바꾼다.</summary>
    public static double Ease(Easing easing, double progress) => easing switch
    {
        Easing.EaseIn => progress * progress,
        Easing.EaseOut => 1 - (1 - progress) * (1 - progress),
        Easing.EaseInOut => progress < 0.5 ? 2 * progress * progress : 1 - 2 * (1 - progress) * (1 - progress),
        _ => progress,
    };
}

/// <summary>이름을 붙여 저장해 둔 움직임. 다른 클립에 그대로 입힐 수 있다.</summary>
public sealed record AnimationPreset(string Name, Dictionary<AnimatedProperty, List<Keyframe>> Keyframes);
