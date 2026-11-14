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

    /// <summary>재생 속도(배). 키프레임을 찍으면 속도가 서서히 변한다(속도 램프).</summary>
    Speed,
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

    /// <summary>
    /// 0초부터 time까지 값을 쌓은 것(값×초). 속도 키프레임에서 원본이 얼마나 흘렀는지 구할 때 쓴다.
    /// 키프레임 밖에서는 Evaluate와 같이 첫 값·마지막 값이 이어진다.
    /// </summary>
    public static double Integrate(IReadOnlyList<Keyframe> keys, TimeSpan time) => Area(keys, time) - Area(keys, TimeSpan.Zero);

    /// <summary>첫 키프레임부터 time까지 쌓은 값. time이 더 앞이면 음수다.</summary>
    private static double Area(IReadOnlyList<Keyframe> keys, TimeSpan time)
    {
        if (time <= keys[0].Time) return keys[0].Value * (time - keys[0].Time).TotalSeconds;

        var area = 0.0;
        for (var i = 1; i < keys.Count; i++)
        {
            var (from, to) = (keys[i - 1], keys[i]);
            var progress = Math.Min(1, (time - from.Time) / (to.Time - from.Time));
            area += (to.Time - from.Time).TotalSeconds
                * (from.Value * progress + (to.Value - from.Value) * EaseArea(from.Easing, progress));
            if (time <= to.Time) return area;
        }

        return area + keys[^1].Value * (time - keys[^1].Time).TotalSeconds;
    }

    /// <summary>Ease를 0부터 progress까지 적분한 것.</summary>
    private static double EaseArea(Easing easing, double p) => easing switch
    {
        Easing.EaseIn => p * p * p / 3,
        Easing.EaseOut => p - (1 - Math.Pow(1 - p, 3)) / 3,
        Easing.EaseInOut => p < 0.5 ? 2 * p * p * p / 3 : 1.0 / 12 + (p - 0.5) - 2 * (0.125 - Math.Pow(1 - p, 3)) / 3,
        _ => p * p / 2,
    };

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
