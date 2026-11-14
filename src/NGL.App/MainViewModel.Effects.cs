using System.IO;
using CommunityToolkit.Mvvm.Input;
using NGL.Core;
using NGL.IO;

namespace NGL.App;

/// <summary>속성 패널: 선택한 영상·이미지 클립의 효과, 색보정, LUT, 전환.</summary>
public partial class MainViewModel
{
    private static readonly string[] EffectProperties =
    [
        nameof(BlurAmount), nameof(SharpenPercent), nameof(VignettePercent), nameof(BrightnessPercent), nameof(ContrastPercent),
        nameof(SaturationPercent), nameof(TemperaturePercent), nameof(ExposureStops), nameof(LutName), nameof(HasLut),
        nameof(TransitionIndex), nameof(TransitionSeconds),
    ];

    public static IReadOnlyList<string> TransitionNames { get; } = ["없음", "디졸브", "검은 화면 페이드", "와이프"];

    private ClipEffects? FirstEffects => FirstVisual?.Effects;

    /// <summary>흐림(픽셀, 0~100).</summary>
    public string BlurAmount
    {
        get => Number(FirstEffects?.Blur);
        set => SetNumber(value, 0, 100, number => EditEffects("흐림 변경", effects => effects.Blur = number));
    }

    /// <summary>선명하게(%, 0~100).</summary>
    public string SharpenPercent
    {
        get => Percent(FirstEffects?.Sharpen);
        set => SetNumber(value, 0, 100, number => EditEffects("선명하게 변경", effects => effects.Sharpen = number / 100));
    }

    /// <summary>비네팅(%, 0~100).</summary>
    public string VignettePercent
    {
        get => Percent(FirstEffects?.Vignette);
        set => SetNumber(value, 0, 100, number => EditEffects("비네팅 변경", effects => effects.Vignette = number / 100));
    }

    // 색보정(%): -100~100, 0이 원래 그대로
    public string BrightnessPercent
    {
        get => Percent(FirstEffects?.Brightness);
        set => SetNumber(value, -100, 100, number => EditEffects("밝기 변경", effects => effects.Brightness = number / 100));
    }

    public string ContrastPercent
    {
        get => Percent(FirstEffects?.Contrast);
        set => SetNumber(value, -100, 100, number => EditEffects("대비 변경", effects => effects.Contrast = number / 100));
    }

    public string SaturationPercent
    {
        get => Percent(FirstEffects?.Saturation);
        set => SetNumber(value, -100, 100, number => EditEffects("채도 변경", effects => effects.Saturation = number / 100));
    }

    public string TemperaturePercent
    {
        get => Percent(FirstEffects?.Temperature);
        set => SetNumber(value, -100, 100, number => EditEffects("색온도 변경", effects => effects.Temperature = number / 100));
    }

    /// <summary>노출(스톱, -2~2).</summary>
    public string ExposureStops
    {
        get => FirstEffects is { } effects ? Math.Round(effects.Exposure, 2).ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        set => SetNumber(value, -2, 2, number => EditEffects("노출 변경", effects => effects.Exposure = number));
    }

    /// <summary>걸려 있는 LUT 파일의 이름. 없으면 "없음".</summary>
    public string LutName => FirstEffects?.Lut is { } path ? Path.GetFileName(path) : "없음";

    public bool HasLut => FirstEffects?.Lut is not null;

    /// <summary>.cube 파일을 프로젝트의 luts 폴더로 복사하고 선택한 클립에 건다.</summary>
    public void ImportLut(string path)
    {
        if (_folder is null) return;
        try
        {
            var relative = CubeLutParser.Import(_folder, path);
            EditEffects("LUT 적용", effects => effects.Lut = relative);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = "LUT를 불러오지 못했습니다: " + e.Message;
        }
    }

    [RelayCommand]
    private void ClearLut() => EditEffects("LUT 해제", effects => effects.Lut = null);

    /// <summary>효과와 색보정을 모두 처음 상태로 되돌린다. (LUT 포함)</summary>
    [RelayCommand]
    private void ResetEffects() => EditEffects("효과 초기화", effects =>
    {
        effects.Blur = effects.Sharpen = effects.Vignette = 0;
        effects.Brightness = effects.Contrast = effects.Saturation = effects.Temperature = effects.Exposure = 0;
        effects.Lut = null;
    });

    /// <summary>앞 클립에서 이 클립으로 넘어올 때의 전환.</summary>
    public int TransitionIndex
    {
        get => (int)(FirstVisual?.Transition ?? TransitionKind.None);
        set
        {
            if (value >= 0 && FirstVisual is { } first && (int)first.Transition != value)
                EditVisual("전환 변경", clip => clip.Transition = (TransitionKind)value);
        }
    }

    /// <summary>전환 길이(초).</summary>
    public string TransitionSeconds
    {
        get => Seconds(FirstVisual?.TransitionDuration);
        set => SetNumber(value, 0.1, 10, number => EditVisual("전환 길이 변경", clip => clip.TransitionDuration = TimeSpan.FromSeconds(number)));
    }

    private void EditEffects(string name, Action<ClipEffects> change) => EditVisual(name, clip => change(clip.Effects));
}
