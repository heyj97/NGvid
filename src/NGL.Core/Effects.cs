namespace NGL.Core;

/// <summary>영상·이미지 클립에 거는 효과와 색보정. 모든 값은 0이 "효과 없음"이다.</summary>
public sealed class ClipEffects
{
    private double _blur, _sharpen, _vignette, _brightness, _contrast, _saturation, _temperature, _exposure;

    /// <summary>흐림의 세기(시퀀스 픽셀, 0~100).</summary>
    public double Blur { get => _blur; set => _blur = Math.Clamp(value, 0, 100); }

    /// <summary>선명하게(0~1).</summary>
    public double Sharpen { get => _sharpen; set => _sharpen = Math.Clamp(value, 0, 1); }

    /// <summary>비네팅: 가장자리를 어둡게 하는 정도(0~1).</summary>
    public double Vignette { get => _vignette; set => _vignette = Math.Clamp(value, 0, 1); }

    /// <summary>밝기(-1~1). 모든 색에 같은 값을 더한다.</summary>
    public double Brightness { get => _brightness; set => _brightness = Math.Clamp(value, -1, 1); }

    /// <summary>대비(-1~1). 중간 밝기를 기준으로 밝고 어두운 차이를 키우거나 줄인다.</summary>
    public double Contrast { get => _contrast; set => _contrast = Math.Clamp(value, -1, 1); }

    /// <summary>채도(-1~1). -1이면 흑백.</summary>
    public double Saturation { get => _saturation; set => _saturation = Math.Clamp(value, -1, 1); }

    /// <summary>색온도(-1~1). 양수는 따뜻하게(노랑), 음수는 차갑게(파랑).</summary>
    public double Temperature { get => _temperature; set => _temperature = Math.Clamp(value, -1, 1); }

    /// <summary>노출(-2~2 스톱). 1 스톱마다 밝기가 두 배가 된다.</summary>
    public double Exposure { get => _exposure; set => _exposure = Math.Clamp(value, -2, 2); }

    /// <summary>색감을 바꾸는 .cube LUT 파일. 프로젝트 폴더 기준 상대 경로(예: luts/film.cube). null이면 없다.</summary>
    public string? Lut { get; set; }
}

/// <summary>3D LUT(색 변환표). Rgb에는 빨강이 가장 빨리 바뀌는 순서로 Size³개의 색(R, G, B 0~1)이 들어 있다.</summary>
public sealed record CubeLut(int Size, float[] Rgb);

/// <summary>앞 클립에서 이 클립으로 넘어갈 때의 효과.</summary>
public enum TransitionKind
{
    None,

    /// <summary>앞 장면 위로 서서히 나타난다.</summary>
    Dissolve,

    /// <summary>앞 장면이 검게 사라졌다가 검은 화면에서 나타난다.</summary>
    DipToBlack,

    /// <summary>왼쪽에서 오른쪽으로 밀고 들어온다.</summary>
    Wipe,
}

public static class Transitions
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>전환이 진행된 정도(0~1). 전환이 없거나 이미 끝났으면 null.</summary>
    public static double? Progress(VisualClip clip, TimeSpan time)
    {
        var elapsed = time - clip.Range.Start;
        return clip.Transition == TransitionKind.None || clip.TransitionDuration <= TimeSpan.Zero
            || elapsed < TimeSpan.Zero || elapsed >= clip.TransitionDuration
            ? null
            : elapsed / clip.TransitionDuration;
    }

    /// <summary>
    /// 전환하는 동안 함께 보여야 하는 앞 클립: 같은 트랙에서 이 클립의 시작에 딱 붙어 끝나는 영상·이미지 클립.
    /// 전환 중이 아니거나 그런 클립이 없으면 null.
    /// </summary>
    public static VisualClip? Outgoing(Track track, VisualClip clip, TimeSpan time) => Progress(clip, time) is null
        ? null
        : track.Clips.OfType<VisualClip>().FirstOrDefault(other => other != clip && other.Range.End == clip.Range.Start);
}
