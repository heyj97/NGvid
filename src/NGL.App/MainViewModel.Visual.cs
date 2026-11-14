using System.Globalization;
using NGL.Core;

namespace NGL.App;

/// <summary>속성 패널: 선택한 영상·이미지 클립의 위치·크기·회전·자르기·뒤집기·불투명도·블렌드·화면 맞춤.</summary>
public partial class MainViewModel
{
    private static readonly string[] VisualProperties =
    [
        nameof(HasVisualSelection), nameof(HasNoSelection), nameof(VisualTitle), nameof(PositionX), nameof(PositionY),
        nameof(ScalePercent), nameof(RotationDegrees), nameof(OpacityPercent), nameof(BlendIndex), nameof(FitIndex),
        nameof(CropLeft), nameof(CropTop), nameof(CropRight), nameof(CropBottom), nameof(FlipHorizontal), nameof(FlipVertical),
    ];

    /// <summary>선택한 영상·이미지 클립들. 패널에는 첫 클립의 값이 보이고, 고치면 모두에 적용된다.</summary>
    private List<VisualClip> SelectedVisualClips() => SelectedClips().OfType<VisualClip>().ToList();

    private VisualClip? FirstVisual => SelectedVisualClips().FirstOrDefault();

    public bool HasVisualSelection => FirstVisual is not null;

    /// <summary>패널에 보여줄 것이 없는가.</summary>
    public bool HasNoSelection => !HasVisualSelection && !HasAudioSelection && !HasTextSelection;

    public string VisualTitle => SelectedVisualClips() is { Count: > 0 } clips
        ? clips.Count == 1 ? (clips[0] is ImageClip ? "이미지 클립" : "영상 클립") : $"화면 클립 {clips.Count}개"
        : "";

    /// <summary>화면 가운데에서 오른쪽으로 옮긴 거리(픽셀).</summary>
    public string PositionX
    {
        get => Number(Shown(FirstVisual, AnimatedProperty.X, FirstVisual?.X));
        set => SetNumber(value, -10000, 10000, number => SetAnimated("위치 변경", AnimatedProperty.X, number, (clip, x) => ((VisualClip)clip).X = x));
    }

    /// <summary>화면 가운데에서 아래로 옮긴 거리(픽셀).</summary>
    public string PositionY
    {
        get => Number(Shown(FirstVisual, AnimatedProperty.Y, FirstVisual?.Y));
        set => SetNumber(value, -10000, 10000, number => SetAnimated("위치 변경", AnimatedProperty.Y, number, (clip, y) => ((VisualClip)clip).Y = y));
    }

    /// <summary>크기(%). 10~400.</summary>
    public string ScalePercent
    {
        get => Percent(Shown(FirstVisual, AnimatedProperty.Scale, FirstVisual?.Scale));
        set => SetNumber(value, 10, 400, number => SetAnimated("크기 변경", AnimatedProperty.Scale, number / 100, (clip, scale) => ((VisualClip)clip).Scale = scale));
    }

    /// <summary>시계 방향 회전(도).</summary>
    public string RotationDegrees
    {
        get => Number(Shown(FirstVisual, AnimatedProperty.Rotation, FirstVisual?.Rotation));
        set => SetNumber(value, -3600, 3600, number => SetAnimated("회전 변경", AnimatedProperty.Rotation, number, (clip, rotation) => ((VisualClip)clip).Rotation = rotation));
    }

    /// <summary>불투명도(%).</summary>
    public string OpacityPercent
    {
        get => Percent(Shown(FirstVisual, AnimatedProperty.Opacity, FirstVisual?.Opacity));
        set => SetNumber(value, 0, 100, number => SetAnimated("불투명도 변경", AnimatedProperty.Opacity, number / 100, (clip, opacity) => ((VisualClip)clip).Opacity = opacity));
    }

    public static IReadOnlyList<string> BlendNames { get; } = ["일반", "곱하기", "스크린", "오버레이", "더하기"];
    public static IReadOnlyList<string> FitNames { get; } = ["맞추기 (전체 보이기)", "채우기 (꽉 채우기)"];

    public int BlendIndex
    {
        get => (int)(FirstVisual?.Blend ?? BlendMode.Normal);
        set
        {
            if (value >= 0 && FirstVisual is { } first && (int)first.Blend != value)
                EditVisual("블렌드 모드 변경", clip => clip.Blend = (BlendMode)value);
        }
    }

    public int FitIndex
    {
        get => (int)(FirstVisual?.Fit ?? FitMode.Fit);
        set
        {
            if (value >= 0 && FirstVisual is { } first && (int)first.Fit != value)
                EditVisual("화면 맞춤 변경", clip => clip.Fit = (FitMode)value);
        }
    }

    // 자르기(%): 각 가장자리에서 잘라낼 비율
    public string CropLeft
    {
        get => Percent(FirstVisual?.CropLeft);
        set => SetNumber(value, 0, 95, number => EditVisual("자르기 변경", clip => clip.CropLeft = number / 100));
    }

    public string CropTop
    {
        get => Percent(FirstVisual?.CropTop);
        set => SetNumber(value, 0, 95, number => EditVisual("자르기 변경", clip => clip.CropTop = number / 100));
    }

    public string CropRight
    {
        get => Percent(FirstVisual?.CropRight);
        set => SetNumber(value, 0, 95, number => EditVisual("자르기 변경", clip => clip.CropRight = number / 100));
    }

    public string CropBottom
    {
        get => Percent(FirstVisual?.CropBottom);
        set => SetNumber(value, 0, 95, number => EditVisual("자르기 변경", clip => clip.CropBottom = number / 100));
    }

    public bool FlipHorizontal
    {
        get => FirstVisual?.FlipHorizontal ?? false;
        set
        {
            if (FirstVisual is { } first && first.FlipHorizontal != value)
                EditVisual("좌우 뒤집기", clip => clip.FlipHorizontal = value);
        }
    }

    public bool FlipVertical
    {
        get => FirstVisual?.FlipVertical ?? false;
        set
        {
            if (FirstVisual is { } first && first.FlipVertical != value)
                EditVisual("상하 뒤집기", clip => clip.FlipVertical = value);
        }
    }

    /// <summary>위치·크기·회전·자르기·뒤집기를 처음 상태로 되돌린다.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetTransform() => EditVisual("변형 초기화", clip =>
    {
        clip.X = clip.Y = clip.Rotation = 0;
        clip.Scale = 1;
        clip.CropLeft = clip.CropTop = clip.CropRight = clip.CropBottom = 0;
        clip.FlipHorizontal = clip.FlipVertical = false;
    });

    private void EditVisual(string name, Action<VisualClip> change)
    {
        var clips = SelectedVisualClips();
        Edit(name, () => clips.ForEach(change));
    }

    private static string Number(double? value) =>
        value is { } number ? Math.Round(number, 1).ToString(CultureInfo.CurrentCulture) : "";
}

/// <summary>미리보기에서 마우스로 직접 옮기기와 안전 영역.</summary>
public partial class MainViewModel
{
    private string? _beforeLiveEdit;

    /// <summary>미리보기에 안전 영역(가장자리 10%·20% 안쪽 선)을 그릴지.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _showSafeArea;

    partial void OnShowSafeAreaChanged(bool value) => RenderRequested?.Invoke();

    /// <summary>지금 화면에 보이는 영상·이미지 클립 중 맨 위의 것.</summary>
    public VisualClip? TopVisualAtPlayhead() => Project?.Sequence.Tracks
        .Where(track => track is VideoTrack { Hidden: false, Locked: false })
        .SelectMany(track => track.Clips).OfType<VisualClip>()
        .FirstOrDefault(clip => clip.Range.Contains(Position));

    /// <summary>선택한 화면 클립들의 현재 위치.</summary>
    public List<(VisualClip Clip, double X, double Y)> SelectedVisualPositions() =>
        SelectedVisualClips().Select(clip => (clip, clip.X, clip.Y)).ToList();

    /// <summary>마우스로 끄는 동안의 변경을 시작한다. 끝나면 CommitLiveEdit로 한 번의 편집으로 기록한다.</summary>
    public void BeginLiveEdit()
    {
        if (Project is { } project) _beforeLiveEdit = NGL.IO.SequenceSnapshot.Take(project.Sequence);
    }

    /// <summary>끄는 동안 화면에 바로 반영한다. 실행 취소 기록에는 남기지 않는다.</summary>
    public void LiveEdit(Action change)
    {
        if (_engine is null || _beforeLiveEdit is null) return;
        _engine.Edit(change);
        TimelineVersion++;
        RefreshInspector();
    }

    public void CommitLiveEdit(string name)
    {
        if (Project is not { } project || _beforeLiveEdit is not { } before) return;
        _beforeLiveEdit = null;

        var command = NGL.Commands.SequenceEditCommand.Completed(name, project.Sequence, before,
            NGL.IO.SequenceSnapshot.Take(project.Sequence));
        if (!command.Changed) return;
        _history.Record(command);
        AfterEdit();
    }

    /// <summary>선택한 화면 클립의 크기를 비율만큼 바꾼다.</summary>
    // shortcut: 미리보기에서 끌거나 휠을 굴리면 고정된 값만 바뀐다. 키프레임이 있는 속성은 속성 패널에서 고쳐야 한다.
    public void ScaleSelection(double factor) => EditVisual("크기 변경", clip => clip.Scale *= factor);
}
