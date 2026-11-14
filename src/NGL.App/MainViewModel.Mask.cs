using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NGL.Core;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.App;

/// <summary>속성 패널: 선택한 영상·이미지 클립의 크로마키와 마스크. 미리보기에서 마스크를 직접 옮기는 동작도 여기 있다.</summary>
public partial class MainViewModel
{
    private static readonly string[] MaskProperties =
    [
        nameof(KeyColor), nameof(KeyTolerancePercent), nameof(KeySoftnessPercent), nameof(MaskShapeIndex), nameof(HasMask),
        nameof(IsPolygonMask), nameof(MaskXPercent), nameof(MaskYPercent), nameof(MaskWidthPercent), nameof(MaskHeightPercent),
        nameof(MaskFeather), nameof(MaskInverted), nameof(MaskKey), nameof(MaskPointText),
    ];

    public static IReadOnlyList<string> MaskShapeNames { get; } = ["없음", "사각형", "원", "자유 도형"];

    /// <summary>켜면 미리보기에서 클립 대신 마스크를 옮긴다(자유 도형은 누른 곳에 점을 찍는다).</summary>
    [ObservableProperty] private bool _maskEditing;

    private ClipMask? FirstMask => FirstVisual?.Mask;

    // ───── 크로마키 ─────

    /// <summary>투명하게 만들 색(#RRGGBB). 비우면 크로마키를 쓰지 않는다.</summary>
    public string KeyColor
    {
        get => FirstEffects?.KeyColor ?? "";
        set => SetColor(value, required: false, color => EditEffects("크로마키 색 변경", effects => effects.KeyColor = color));
    }

    public string KeyTolerancePercent
    {
        get => Percent(FirstEffects?.KeyTolerance);
        set => SetNumber(value, 0, 100, number => EditEffects("크로마키 허용 범위 변경", effects => effects.KeyTolerance = number / 100));
    }

    public string KeySoftnessPercent
    {
        get => Percent(FirstEffects?.KeySoftness);
        set => SetNumber(value, 0, 100, number => EditEffects("크로마키 부드럽게 변경", effects => effects.KeySoftness = number / 100));
    }

    // ───── 마스크 ─────

    public int MaskShapeIndex
    {
        get => (int)(FirstMask?.Shape ?? MaskShape.None);
        set
        {
            if (value >= 0 && FirstMask is { } first && (int)first.Shape != value)
                EditVisual("마스크 도형 변경", clip => clip.Mask.Shape = (MaskShape)value);
        }
    }

    public bool HasMask => FirstMask is { Shape: not MaskShape.None };

    public bool IsPolygonMask => FirstMask?.Shape == MaskShape.Polygon;

    public string MaskPointText => FirstMask is { } mask ? $"점 {mask.Points.Count}개" : "";

    // 가운데와 크기(%): 클립 그림에 대한 비율. 키프레임이 있으면 재생 위치의 값을 보이고 고친다.
    public string MaskXPercent
    {
        get => Percent(Shown(FirstVisual, AnimatedProperty.MaskX, FirstMask?.X));
        set => SetNumber(value, -200, 300, number => SetAnimated("마스크 위치 변경", AnimatedProperty.MaskX, number / 100, (clip, x) => ((VisualClip)clip).Mask.X = x));
    }

    public string MaskYPercent
    {
        get => Percent(Shown(FirstVisual, AnimatedProperty.MaskY, FirstMask?.Y));
        set => SetNumber(value, -200, 300, number => SetAnimated("마스크 위치 변경", AnimatedProperty.MaskY, number / 100, (clip, y) => ((VisualClip)clip).Mask.Y = y));
    }

    public string MaskWidthPercent
    {
        get => Percent(Shown(FirstVisual, AnimatedProperty.MaskWidth, FirstMask?.Width));
        set => SetNumber(value, 1, 300, number => SetAnimated("마스크 크기 변경", AnimatedProperty.MaskWidth, number / 100, (clip, width) => ((VisualClip)clip).Mask.Width = width));
    }

    public string MaskHeightPercent
    {
        get => Percent(Shown(FirstVisual, AnimatedProperty.MaskHeight, FirstMask?.Height));
        set => SetNumber(value, 1, 300, number => SetAnimated("마스크 크기 변경", AnimatedProperty.MaskHeight, number / 100, (clip, height) => ((VisualClip)clip).Mask.Height = height));
    }

    /// <summary>가장자리 흐림(픽셀).</summary>
    public string MaskFeather
    {
        get => Number(FirstMask?.Feather);
        set => SetNumber(value, 0, 300, number => EditVisual("마스크 흐림 변경", clip => clip.Mask.Feather = number));
    }

    public bool MaskInverted
    {
        get => FirstMask?.Inverted ?? false;
        set
        {
            if (FirstMask is { } first && first.Inverted != value) EditVisual("마스크 반전", clip => clip.Mask.Inverted = value);
        }
    }

    public string MaskKey => KeyMark(AnimatedProperty.MaskX, AnimatedProperty.MaskY, AnimatedProperty.MaskWidth, AnimatedProperty.MaskHeight);

    /// <summary>자유 도형의 점을 모두 지운다. 미리보기에서 다시 찍을 수 있다.</summary>
    [RelayCommand]
    private void ClearMaskPoints() => EditVisual("마스크 점 지우기", clip => clip.Mask.Points.Clear());

    // ───── 미리보기에서 직접 편집 ─────

    /// <summary>미리보기에서 마스크를 편집하는 중인가: 편집을 켰고 선택한 클립에 마스크가 있다.</summary>
    public bool IsEditingMask => MaskEditing && HasMask;

    /// <summary>시퀀스의 한 점이 선택한 클립 그림의 어디인지(0~1). 선택한 화면 클립이 없으면 null.</summary>
    public (double X, double Y)? ToMaskSpace(double sequenceX, double sequenceY)
    {
        if (Project is not { } project || FirstVisual is not { } clip) return null;

        // 예전에 가져와 크기가 기록되지 않은 원본은 시퀀스와 같은 크기로 본다.
        var preset = project.Sequence.Preset;
        var asset = project.FindAsset(clip.AssetId);
        var size = asset is { Width: > 0, Height: > 0 } ? new SKSize(asset.Width, asset.Height) : new SKSize(preset.Width, preset.Height);
        var point = SkiaCompositor.ToClipSpace(clip, size, SKRect.Create(preset.Width, preset.Height), Position,
            new SKPoint((float)sequenceX, (float)sequenceY));
        return (point.X, point.Y);
    }

    /// <summary>끄는 동안 마스크의 가운데를 옮긴다. BeginLiveEdit와 CommitLiveEdit 사이에서 부른다.</summary>
    // shortcut: 끌어서 옮기면 고정된 값만 바뀐다. 마스크 위치에 키프레임이 있으면 속성 패널에서 고쳐야 한다.
    public void MoveMask(double x, double y)
    {
        var clips = SelectedVisualClips();
        LiveEdit(() => clips.ForEach(clip => (clip.Mask.X, clip.Mask.Y) = (Math.Round(x, 3), Math.Round(y, 3))));
    }

    /// <summary>자유 도형에 점을 하나 더한다.</summary>
    public void AddMaskPoint(double x, double y) =>
        EditVisual("마스크 점 추가", clip => clip.Mask.Points.Add(new MaskPoint(Math.Round(x, 3), Math.Round(y, 3))));

    /// <summary>사각형·원 마스크의 크기를 비율만큼 바꾼다.</summary>
    public void ScaleMask(double factor) => EditVisual("마스크 크기 변경", clip =>
    {
        clip.Mask.Width *= factor;
        clip.Mask.Height *= factor;
    });
}
