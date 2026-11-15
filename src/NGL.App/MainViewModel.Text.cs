using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using NGL.Commands;
using NGL.Core;
using NGL.IO;

namespace NGL.App;

/// <summary>텍스트: 추가, 속성 패널의 글자 항목, 스타일 프리셋, 자막 파일 가져오기·내보내기.</summary>
public partial class MainViewModel
{
    private static readonly string[] TextProperties =
    [
        nameof(HasTextSelection), nameof(TextTitle), nameof(TextContent), nameof(FontName),
        nameof(TextSize), nameof(TextBold), nameof(TextColor), nameof(OutlineColor), nameof(OutlineWidth), nameof(ShadowColor),
        nameof(ShadowOffset), nameof(BoxColor), nameof(TextX), nameof(TextY), nameof(HighlightColor),
    ];

    private static readonly string StylePresetPath = PresetFile.PathOf("text-style-presets.json");
    private readonly List<TextStylePreset> _stylePresets = PresetFile.Load(StylePresetPath, TextStylePreset.Defaults);

    /// <summary>이 PC에 설치된 글꼴 이름들.</summary>
    public static IReadOnlyList<string> FontNames { get; } =
        Fonts.SystemFontFamilies.Select(family => family.Source).Order(StringComparer.CurrentCulture).ToList();

    public ObservableCollection<string> StylePresetNames { get; } = [];

    public int StylePresetIndex { get; set; }

    /// <summary>선택한 텍스트 클립들. 패널에는 첫 클립의 값이 보이고, 고치면 모두에 적용된다.</summary>
    private List<TextualClip> SelectedTextClips() => SelectedClips().OfType<TextualClip>().ToList();

    private TextualClip? FirstText => SelectedTextClips().FirstOrDefault();

    public bool HasTextSelection => FirstText is not null;

    public string TextTitle => SelectedTextClips() is { Count: > 0 } clips
        ? "텍스트" + (clips.Count > 1 ? $" {clips.Count}개" : "")
        : "";

    // ───── 추가 ─────

    /// <summary>재생 위치에 텍스트 클립을 놓고 선택한다.</summary>
    [RelayCommand]
    private void AddText() => AddTextAt(null, Position);

    /// <summary>time에 텍스트 클립을 놓고 선택한다. track이 영상 트랙이고 그 자리가 비어 있으면 거기에 놓는다.</summary>
    public void AddTextAt(Track? track, TimeSpan time)
    {
        if (Project is not { } project) return;
        TextClip? added = null;
        Edit("텍스트 추가", () => added = project.Sequence.AddText(time, "텍스트", track));
        if (added is not null) Select(added);
    }

    // ───── 속성 패널 ─────

    public string TextContent
    {
        get => FirstText?.Text ?? "";
        set => EditText("문구 변경", clip => clip.Text = value);
    }

    public string FontName
    {
        get => FirstText?.Style.Font ?? "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) EditStyle("글꼴 변경", style => style.Font = value.Trim());
        }
    }

    /// <summary>글자 크기(픽셀).</summary>
    public string TextSize
    {
        get => Number(FirstText?.Style.Size);
        set => SetNumber(value, 8, 1000, number => EditStyle("글자 크기 변경", style => style.Size = number));
    }

    public bool TextBold
    {
        get => FirstText?.Style.Bold ?? false;
        set => EditStyle("굵기 변경", style => style.Bold = value);
    }

    public string TextColor
    {
        get => FirstText?.Style.Color ?? "";
        set => SetColor(value, required: true, color => EditStyle("글자 색 변경", style => style.Color = color!));
    }

    public string OutlineColor
    {
        get => FirstText?.Style.OutlineColor ?? "";
        set => SetColor(value, required: true, color => EditStyle("외곽선 색 변경", style => style.OutlineColor = color!));
    }

    /// <summary>외곽선 두께(픽셀). 0이면 없음.</summary>
    public string OutlineWidth
    {
        get => Number(FirstText?.Style.OutlineWidth);
        set => SetNumber(value, 0, 100, number => EditStyle("외곽선 두께 변경", style => style.OutlineWidth = number));
    }

    public string ShadowColor
    {
        get => FirstText?.Style.ShadowColor ?? "";
        set => SetColor(value, required: true, color => EditStyle("그림자 색 변경", style => style.ShadowColor = color!));
    }

    /// <summary>그림자 거리(픽셀). 0이면 없음.</summary>
    public string ShadowOffset
    {
        get => Number(FirstText?.Style.ShadowOffset);
        set => SetNumber(value, 0, 200, number => EditStyle("그림자 변경", style => style.ShadowOffset = number));
    }

    /// <summary>배경 박스 색. 비우면 없음.</summary>
    public string BoxColor
    {
        get => FirstText?.Style.BoxColor ?? "";
        set => SetColor(value, required: false, color => EditStyle("배경 박스 변경", style => style.BoxColor = color));
    }

    public string TextX
    {
        get => Number(Shown(FirstText, AnimatedProperty.X, FirstText?.Style.X));
        set => SetNumber(value, -10000, 10000, number => SetAnimated("위치 변경", AnimatedProperty.X, number, (TextualClip clip, double x) => clip.Style.X = x));
    }

    public string TextY
    {
        get => Number(Shown(FirstText, AnimatedProperty.Y, FirstText?.Style.Y));
        set => SetNumber(value, -10000, 10000, number => SetAnimated("위치 변경", AnimatedProperty.Y, number, (TextualClip clip, double y) => clip.Style.Y = y));
    }

    /// <summary>말하는 단어를 강조하는 색. 비우면 강조하지 않는다.</summary>
    public string HighlightColor
    {
        get => FirstText?.Style.HighlightColor ?? "";
        set => SetColor(value, required: false, color => EditStyle("단어 강조 변경", style => style.HighlightColor = color));
    }

    private void EditText(string name, Action<TextualClip> change)
    {
        var clips = SelectedTextClips();
        Edit(name, () => clips.ForEach(change));
    }

    private void EditStyle(string name, Action<TextStyle> change) => EditText(name, clip => change(clip.Style));

    /// <summary>
    /// #RRGGBB(또는 #AARRGGBB)로 읽을 수 있으면 적용한다. 비운 값은 required가 아닐 때만 "없음"으로 적용한다.
    /// 읽을 수 없으면 원래 값을 다시 보여준다.
    /// </summary>
    private void SetColor(string text, bool required, Action<string?> apply)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            if (!required) apply(null);
        }
        else
        {
            if (!text.StartsWith('#')) text = "#" + text;
            if (NGL.Rendering.TextRenderer.ColorOf(text) is not null) apply(text.ToUpperInvariant());
        }

        RefreshInspector();
    }

    // ───── 스타일 프리셋 ─────

    private void RefreshStylePresets()
    {
        StylePresetNames.Clear();
        foreach (var preset in _stylePresets) StylePresetNames.Add(preset.Name);
    }

    /// <summary>고른 프리셋의 모양을 입힌다. 위치는 지금 것을 그대로 둔다.</summary>
    [RelayCommand]
    private void ApplyStylePreset()
    {
        if (StylePresetIndex < 0 || StylePresetIndex >= _stylePresets.Count) return;
        var preset = _stylePresets[StylePresetIndex];
        EditText($"스타일 적용: {preset.Name}", clip =>
        {
            var style = preset.Style.Clone();
            (style.X, style.Y) = (clip.Style.X, clip.Style.Y);
            clip.Style = style;
        });
    }

    /// <summary>선택한 클립(첫 번째)의 스타일을 프리셋으로 저장한다. 같은 이름이 있으면 바꾼다.</summary>
    public void SaveStylePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || FirstText is not { } source) return;

        _stylePresets.RemoveAll(preset => preset.Name == name);
        _stylePresets.Add(new TextStylePreset(name, source.Style.Clone()));
        StoreStylePresets();
    }

    [RelayCommand]
    private void DeleteStylePreset()
    {
        if (StylePresetIndex < 0 || StylePresetIndex >= _stylePresets.Count) return;
        _stylePresets.RemoveAt(StylePresetIndex);
        StoreStylePresets();
    }

    private void StoreStylePresets()
    {
        try
        {
            PresetFile.Save(StylePresetPath, _stylePresets);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "프리셋을 저장하지 못했습니다: " + e.Message;
        }

        RefreshStylePresets();
    }

    /// <summary>선택한 텍스트의 스타일을 같은 트랙의 모든 텍스트에 복사한다. 문구와 시간은 그대로다.</summary>
    [RelayCommand]
    private void ApplyStyleToTrack()
    {
        if (Project is not { } project || FirstText is not { } source) return;
        Edit("모든 텍스트에 스타일 적용", () => SequenceEdits.ApplyStyleToTrack(project.Sequence, source));
    }

    // ───── 자막 파일 ─────

    /// <summary>SRT·VTT 파일의 자막을 맨 위의 새 영상 트랙에 텍스트 클립으로 넣는다.</summary>
    public void ImportSubtitles(string path)
    {
        if (Project is not { } project) return;

        List<Cue> cues;
        try
        {
            cues = Subtitles.Load(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "자막 파일을 읽지 못했습니다: " + e.Message;
            return;
        }

        if (cues.Count == 0)
        {
            StatusText = $"자막을 찾지 못했습니다 ({Path.GetFileName(path)})";
            return;
        }

        var skipped = 0;
        Edit("자막 가져오기", () =>
        {
            var track = project.Sequence.AddTrack<VideoTrack>();
            foreach (var cue in cues)
            {
                // 파일 안에서 시간이 겹치는 자막은 한 트랙에 놓을 수 없어 건너뛴다.
                if (project.Sequence.AddSubtitle(track, cue.Range, cue.Text) is null) skipped++;
            }
        });
        StatusText = $"자막 {cues.Count - skipped}개를 가져왔습니다." + (skipped > 0 ? $" (시간이 겹쳐 건너뛴 자막 {skipped}개)" : "");
    }

    /// <summary>트랙의 텍스트 클립들을 SRT 파일로 저장한다.</summary>
    public void ExportSubtitles(VideoTrack track, string path)
    {
        try
        {
            var cues = track.Clips.OfType<TextClip>().Select(clip => new Cue(clip.Range, clip.Text)).ToList();
            Subtitles.SaveSrt(path, cues);
            StatusText = $"자막 {cues.Count}개를 저장했습니다: {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "자막 파일을 저장하지 못했습니다: " + e.Message;
        }
    }

    /// <summary>텍스트 클립이 하나라도 있는 영상 트랙들.</summary>
    public List<VideoTrack> TextTracks() =>
        Project?.Sequence.Tracks.OfType<VideoTrack>().Where(track => track.Clips.OfType<TextClip>().Any()).ToList() ?? [];
}
