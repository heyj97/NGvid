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
        nameof(TextSize), nameof(TextPreset), nameof(TextX), nameof(TextY),
    ];

    private static readonly string StylePresetPath = PresetFile.PathOf("text-style-presets.json");
    private readonly List<TextStylePreset> _stylePresets = PresetFile.Load(StylePresetPath, TextStylePreset.Defaults);

    /// <summary>이 PC에 설치된 글꼴 이름들.</summary>
    public static IReadOnlyList<string> FontNames { get; } =
        Fonts.SystemFontFamilies.Select(family => family.Source).Order(StringComparer.CurrentCulture).ToList();

    public ObservableCollection<string> StylePresetNames { get; } = [];

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

    /// <summary>클립에 입힌 프리셋의 이름. 고르면 그 프리셋의 모양과 움직임을 입힌다.</summary>
    public string TextPreset
    {
        get => FirstText?.Style.Preset ?? "";
        set
        {
            // 목록을 다시 채우는 동안 콤보박스가 빈 값을 보내므로, 실제로 다른 프리셋을 골랐을 때만 입힌다.
            if (value == TextPreset || _stylePresets.Find(preset => preset.Name == value) is not { } chosen) return;
            var clips = SelectedTextClips();
            Edit($"프리셋 적용: {chosen.Name}", () => SequenceEdits.ApplyTextPreset(Project!.Sequence, clips, chosen));
        }
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
        OnPropertyChanged(nameof(TextPreset)); // 다시 채운 목록에서 지금 프리셋을 다시 고르게 한다
    }

    /// <summary>텍스트 스튜디오에서 고칠 프리셋들.</summary>
    public IReadOnlyList<TextStylePreset> TextPresets => _stylePresets;



    /// <summary>
    /// 프리셋을 저장한다. oldName이 있으면 그 프리셋을 바꾸고(이름이 달라져도 된다), 없으면 새로 더한다.
    /// 열려 있는 프로젝트에서 그 프리셋을 쓰는 텍스트는 새 모양으로 함께 바뀐다.
    /// </summary>
    public void SaveTextPreset(string? oldName, TextStylePreset preset)
    {
        _stylePresets.RemoveAll(each => each.Name == preset.Name && each.Name != oldName); // 같은 이름은 하나만 둔다
        var index = _stylePresets.FindIndex(each => each.Name == oldName);
        if (index >= 0) _stylePresets[index] = preset;
        else _stylePresets.Add(preset);
        StoreStylePresets();

        if (oldName is null || Project is not { } project) return;
        var users = AllClips.OfType<TextualClip>().Where(clip => clip.Style.Preset == oldName).ToList();
        if (users.Count > 0) Edit($"프리셋 변경: {preset.Name}", () => SequenceEdits.ApplyTextPreset(project.Sequence, users, preset));
    }

    /// <summary>프리셋을 지운다. 그 프리셋을 입혀 둔 텍스트의 모양은 그대로 남는다.</summary>
    public void DeleteTextPreset(string name)
    {
        _stylePresets.RemoveAll(preset => preset.Name == name);
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
