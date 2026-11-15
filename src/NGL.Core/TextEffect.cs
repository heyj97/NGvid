namespace NGL.Core;

public enum TextEffectKind
{
    FadeIn,
    FadeOut,
    Move,
    Scale,
    Popup,
}

/// <summary>
/// 텍스트 클립에 거는 효과 하나. 클립이 시작한 뒤 Start초부터 Duration초 동안 진행된다.
/// 종류에 따라 쓰는 값이 다르다: 이동은 FromX·FromY·ToX·ToY, 확대·축소는 From·To, 팝업은 From·Peak.
/// </summary>
public sealed record TextEffect
{
    public TextEffectKind Kind { get; set; }

    /// <summary>클립 시작에서 잰 시작 시각(초).</summary>
    public double Start { get; set; }

    /// <summary>지속 시간(초).</summary>
    public double Duration { get; set; } = 0.5;

    /// <summary>시작 크기(%). 확대·축소와 팝업에서 쓴다.</summary>
    public double From { get; set; }

    /// <summary>끝 크기(%). 확대·축소에서 쓴다.</summary>
    public double To { get; set; }

    /// <summary>가장 커졌을 때의 크기(%). 팝업에서 쓴다. 그 뒤 살짝 튕기며 100%로 자리 잡는다.</summary>
    public double Peak { get; set; }

    /// <summary>클립의 자리에서 얼마나 떨어져 시작하고 끝나는지(화면 너비·높이의 %). 이동에서 쓴다.</summary>
    public double FromX { get; set; }

    public double FromY { get; set; }

    public double ToX { get; set; }

    public double ToY { get; set; }

    /// <summary>그 종류를 처음 더할 때의 값.</summary>
    public static TextEffect Create(TextEffectKind kind) => kind switch
    {
        TextEffectKind.Move => new() { Kind = kind, FromY = 12 },
        TextEffectKind.Scale => new() { Kind = kind, From = 70, To = 100 },
        TextEffectKind.Popup => new() { Kind = kind, From = 65, Peak = 120 },
        _ => new() { Kind = kind },
    };
}

/// <summary>효과들이 어느 시각에 만드는 변화. X·Y는 클립의 자리에 더하는 거리(화면의 %), Scale·Opacity는 곱하는 비율.</summary>
public readonly record struct TextMotion(double X, double Y, double Scale, double Opacity);

public static class TextEffects
{
    /// <summary>
    /// 클립이 시작한 뒤 elapsed초일 때의 변화. 위치·크기·투명도마다 이미 시작한 효과 중 가장 늦게 시작한 것 하나가 적용된다.
    /// 페이드 인이 있으면 그 효과가 시작하기 전에는 보이지 않는다.
    /// </summary>
    public static TextMotion At(IReadOnlyList<TextEffect> effects, double elapsed)
    {
        TextEffect? move = null, zoom = null, fade = null;
        foreach (var effect in effects.Where(effect => elapsed >= effect.Start))
        {
            ref var active = ref fade;
            if (effect.Kind == TextEffectKind.Move) active = ref move;
            else if (effect.Kind is TextEffectKind.Scale or TextEffectKind.Popup) active = ref zoom;
            if (active is null || active.Start <= effect.Start) active = effect;
        }

        double Progress(TextEffect effect) => Math.Clamp((elapsed - effect.Start) / Math.Max(0.01, effect.Duration), 0, 1);

        var scale = zoom is null ? 1
            : zoom.Kind == TextEffectKind.Popup ? PopupScale(zoom.From / 100, zoom.Peak / 100, Progress(zoom))
            : Lerp(zoom.From, zoom.To, Progress(zoom)) / 100;
        var opacity = fade is null ? (effects.Any(effect => effect.Kind == TextEffectKind.FadeIn) ? 0 : 1)
            : fade.Kind == TextEffectKind.FadeIn ? Progress(fade)
            : 1 - Progress(fade);
        return move is null
            ? new TextMotion(0, 0, scale, opacity)
            : new TextMotion(Lerp(move.FromX, move.ToX, Progress(move)), Lerp(move.FromY, move.ToY, Progress(move)), scale, opacity);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>진행의 40%에서 peak까지 빠르게 커지고, 남은 동안 한 번 살짝 튕기며 1로 자리 잡는다.</summary>
    private static double PopupScale(double from, double peak, double progress)
    {
        const double peakTime = 0.4;
        if (progress < peakTime)
        {
            var t = progress / peakTime;
            return Lerp(from, peak, 1 - Math.Pow(1 - t, 3));
        }

        var settle = (progress - peakTime) / (1 - peakTime);
        return 1 + (peak - 1) * Math.Cos(Math.PI * 1.5 * settle) * Math.Pow(1 - settle, 2);
    }
}
