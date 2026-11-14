using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using NGL.Commands;
using NGL.Core;
using NGL.IO;

namespace NGL.App;

/// <summary>속성 패널: 키프레임 찍기·지우기·이동, 이징, 애니메이션 프리셋.</summary>
public partial class MainViewModel
{
    private static readonly string[] KeyframeProperties =
    [
        nameof(PositionKey), nameof(ScaleKey), nameof(RotationKey), nameof(OpacityKey), nameof(VolumeKey),
        nameof(EasingIndex), nameof(HasAnimation),
    ];

    private readonly List<AnimationPreset> _presets = AnimationPresetStore.Load();
    private Easing _newEasing; // 새로 찍는 키프레임의 이징
    private long _inspectorFrame = -1;

    public static IReadOnlyList<string> EasingNames { get; } = ["일정하게", "천천히 시작", "천천히 끝", "천천히 시작·끝"];

    public ObservableCollection<string> PresetNames { get; } = [];

    public int PresetIndex { get; set; }

    /// <summary>재생 위치를 클립의 시작에서 잰 시간(클립 안으로 제한).</summary>
    private TimeSpan KeyTime(Clip clip)
    {
        var time = Position - clip.Range.Start;
        return time < TimeSpan.Zero ? TimeSpan.Zero : time > clip.Range.Duration ? clip.Range.Duration : time;
    }

    /// <summary>패널에 보여줄 값: 키프레임이 있으면 재생 위치의 값.</summary>
    private double? Shown(Clip? clip, AnimatedProperty property, double? fixedValue) =>
        clip is null || fixedValue is null ? null : clip.ValueAt(property, fixedValue.Value, Position);

    /// <summary>
    /// 패널에서 값을 고쳤을 때: 그 속성에 키프레임이 있는 클립은 재생 위치에 키프레임을 찍고,
    /// 없는 클립은 고정된 값을 바꾼다.
    /// </summary>
    private void SetAnimated(string name, AnimatedProperty property, double value, Action<Clip, double> setFixed)
    {
        var clips = SelectedClips().Where(clip => SequenceEdits.CanAnimate(clip, property)).ToList();
        Edit(name, () =>
        {
            foreach (var clip in clips)
            {
                if (clip.Keyframes.ContainsKey(property)) clip.SetKeyframe(property, new Keyframe(KeyTime(clip), value, _newEasing));
                else setFixed(clip, value);
            }
        });
    }

    // ───── 키프레임 버튼 ─────

    /// <summary>버튼 글자: ◆ 재생 위치에 키프레임 있음, ◈ 다른 곳에 있음, ◇ 없음.</summary>
    private string KeyMark(params AnimatedProperty[] properties)
    {
        var clips = SelectedClips().Where(clip => SequenceEdits.CanAnimate(clip, properties[0])).ToList();
        if (clips.Any(clip => properties.Any(property => HasKeyAtPlayhead(clip, property)))) return "◆";
        return clips.Any(clip => properties.Any(clip.Keyframes.ContainsKey)) ? "◈" : "◇";
    }

    private bool HasKeyAtPlayhead(Clip clip, AnimatedProperty property) =>
        clip.Keyframes.TryGetValue(property, out var keys) && keys.Any(key => key.Time == KeyTime(clip));

    public string PositionKey => KeyMark(AnimatedProperty.X, AnimatedProperty.Y);
    public string ScaleKey => KeyMark(AnimatedProperty.Scale);
    public string RotationKey => KeyMark(AnimatedProperty.Rotation);
    public string OpacityKey => KeyMark(AnimatedProperty.Opacity);
    public string VolumeKey => KeyMark(AnimatedProperty.Volume);

    /// <summary>선택한 클립에 키프레임이 하나라도 있는가.</summary>
    public bool HasAnimation => SelectedClips().Any(clip => clip.Keyframes.Count > 0);

    /// <summary>재생 위치에 키프레임을 찍는다. 이미 있으면 지운다. kind는 Position·Scale·Rotation·Opacity·Volume.</summary>
    [RelayCommand]
    private void ToggleKeyframe(string kind)
    {
        AnimatedProperty[] properties = kind == "Position"
            ? [AnimatedProperty.X, AnimatedProperty.Y]
            : [Enum.Parse<AnimatedProperty>(kind)];
        var clips = SelectedClips().Where(clip => SequenceEdits.CanAnimate(clip, properties[0])).ToList();
        if (clips.Count == 0) return;

        var remove = clips.All(clip => properties.All(property => HasKeyAtPlayhead(clip, property)));
        Edit(remove ? "키프레임 삭제" : "키프레임 추가", () =>
        {
            foreach (var clip in clips)
            {
                foreach (var property in properties)
                {
                    if (remove) clip.RemoveKeyframe(property, KeyTime(clip));
                    else clip.SetKeyframe(property, new Keyframe(KeyTime(clip), clip.ValueAt(property, FixedValue(clip, property), Position), _newEasing));
                }
            }
        });
    }

    private static double FixedValue(Clip clip, AnimatedProperty property) => (clip, property) switch
    {
        (VisualClip visual, AnimatedProperty.X) => visual.X,
        (VisualClip visual, AnimatedProperty.Y) => visual.Y,
        (VisualClip visual, AnimatedProperty.Scale) => visual.Scale,
        (VisualClip visual, AnimatedProperty.Rotation) => visual.Rotation,
        (VisualClip visual, AnimatedProperty.Opacity) => visual.Opacity,
        (AudioClip audio, AnimatedProperty.Volume) => audio.Volume,
        (TextualClip text, AnimatedProperty.X) => text.Style.X,
        (TextualClip text, AnimatedProperty.Y) => text.Style.Y,
        (TextualClip, AnimatedProperty.Scale or AnimatedProperty.Opacity) => 1, // 글자의 크기·불투명도는 100%가 기본
        _ => 0,
    };

    /// <summary>선택한 클립의 이전(-1)·다음(1) 키프레임으로 재생 위치를 옮긴다.</summary>
    [RelayCommand]
    private void JumpKeyframe(string direction)
    {
        var times = SelectedClips()
            .SelectMany(clip => clip.Keyframes.Values.SelectMany(keys => keys.Select(key => clip.Range.Start + key.Time)))
            .Distinct().Order().ToList();
        var position = Position;
        var target = direction == "1"
            ? times.Where(time => time > position).Cast<TimeSpan?>().FirstOrDefault()
            : times.Where(time => time < position).Cast<TimeSpan?>().LastOrDefault();
        if (target is null || _engine is null) return;

        _engine.Seek(target.Value);
        Tick();
    }

    /// <summary>재생 위치에 있는 키프레임의 이징. 고르면 그 키프레임들과 앞으로 찍을 키프레임에 적용된다.</summary>
    public int EasingIndex
    {
        get => (int)(SelectedClips()
            .SelectMany(clip => clip.Keyframes.Values.SelectMany(keys => keys.Where(key => key.Time == KeyTime(clip))))
            .FirstOrDefault()?.Easing ?? _newEasing);
        set
        {
            if (value < 0) return;
            _newEasing = (Easing)value;
            var clips = SelectedClips();
            Edit("이징 변경", () =>
            {
                foreach (var clip in clips)
                {
                    foreach (var keys in clip.Keyframes.Values)
                    {
                        var index = keys.FindIndex(key => key.Time == KeyTime(clip));
                        if (index >= 0) keys[index] = keys[index] with { Easing = _newEasing };
                    }
                }
            });
        }
    }

    [RelayCommand]
    private void ClearKeyframes() => EditSelection("키프레임 모두 삭제", (_, clips) => clips.ForEach(clip => clip.Keyframes.Clear()));

    // ───── 프리셋 ─────

    private void RefreshPresets()
    {
        PresetNames.Clear();
        foreach (var preset in _presets) PresetNames.Add(preset.Name);
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (PresetIndex < 0 || PresetIndex >= _presets.Count) return;
        var preset = _presets[PresetIndex];
        EditSelection($"프리셋 적용: {preset.Name}", (sequence, clips) => SequenceEdits.ApplyPreset(sequence, clips, preset));
    }

    /// <summary>선택한 클립(첫 번째)의 움직임을 프리셋으로 저장한다. 같은 이름이 있으면 바꾼다.</summary>
    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || SelectedClips().FirstOrDefault(clip => clip.Keyframes.Count > 0) is not { } source) return;

        _presets.RemoveAll(preset => preset.Name == name);
        _presets.Add(SequenceEdits.ToPreset(source, name));
        StorePresets();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (PresetIndex < 0 || PresetIndex >= _presets.Count) return;
        _presets.RemoveAt(PresetIndex);
        StorePresets();
    }

    private void StorePresets()
    {
        try
        {
            AnimationPresetStore.Save(_presets);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "프리셋을 저장하지 못했습니다: " + e.Message;
        }

        RefreshPresets();
    }

    /// <summary>재생 위치가 다른 프레임으로 옮겨졌고 선택한 클립에 움직임이 있으면 패널의 값을 다시 보여준다.</summary>
    private void FollowAnimation()
    {
        var frame = Project?.Sequence.FrameAt(Position) ?? -1;
        if (frame == _inspectorFrame) return;
        _inspectorFrame = frame;
        if (HasAnimation) RefreshInspector();
    }
}
