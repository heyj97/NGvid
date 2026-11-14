using NGL.Core;

namespace NGL.App;

/// <summary>오른쪽 클릭 메뉴의 한 줄. Header가 비어 있으면 구분선이다.</summary>
public sealed record MenuEntry(string Header, Action? Run = null, string Gesture = "", bool Checked = false, List<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new("");
}

/// <summary>타임라인의 오른쪽 클릭 메뉴에 무엇을 넣을지 정한다.</summary>
public partial class MainViewModel
{
    private static readonly int[] SpeedChoices = [25, 50, 100, 200, 400];

    /// <summary>선택한 클립들에 맞는 메뉴. 해당하지 않는 항목은 넣지 않는다.</summary>
    public List<MenuEntry> ClipMenu()
    {
        var clips = SelectedClips();
        var menu = new List<MenuEntry>
        {
            new("재생 위치에서 분할", () => SplitCommand.Execute(null), "Ctrl+K"),
            new("삭제", () => DeleteCommand.Execute(null), "Delete"),
            new("리플 삭제", () => RippleDeleteCommand.Execute(null), "Shift+Delete"),
            MenuEntry.Separator,
            new("복사", () => CopyCommand.Execute(null), "Ctrl+C"),
            new("붙여넣기", () => PasteCommand.Execute(null), "Ctrl+V"),
            new("속성 붙여넣기", () => PastePropertiesCommand.Execute(null), "Ctrl+Alt+V"),
        };

        // 연결·그룹: 지금 상태에서 할 수 있는 쪽만 보인다.
        var linking = new List<MenuEntry>();
        if (clips.Any(clip => clip.LinkId is not null)) linking.Add(new("영상·오디오 분리", () => UnlinkCommand.Execute(null)));
        else if (clips.Count >= 2) linking.Add(new("영상·오디오 연결", () => LinkCommand.Execute(null)));
        if (clips.Any(clip => clip.GroupId is not null)) linking.Add(new("그룹 해제", () => UngroupCommand.Execute(null), "Ctrl+Shift+G"));
        else if (clips.Count >= 2) linking.Add(new("그룹", () => GroupCommand.Execute(null), "Ctrl+G"));
        AddSection(menu, linking);

        var clipKind = new List<MenuEntry>();
        if (clips.Any(clip => clip is VideoClip)) clipKind.Add(new("정지 프레임 만들기", () => FreezeFrameCommand.Execute(null)));
        if (HasVisualSelection)
        {
            clipKind.Add(new("전환", Children: TransitionNames
                .Select((name, index) => new MenuEntry(name, () => TransitionIndex = index, Checked: TransitionIndex == index)).ToList()));
        }

        if (FirstSpeedClip is { } speedClip)
        {
            var ramp = speedClip.Keyframes.ContainsKey(AnimatedProperty.Speed);
            clipKind.Add(new("속도", Children: SpeedChoices
                .Select(percent => new MenuEntry($"{percent}%", () => SpeedPercent = percent.ToString(),
                    Checked: !ramp && Math.Abs(speedClip.Speed * 100 - percent) < 0.5)).ToList()));
        }

        AddSection(menu, clipKind);

        if (HasAnimation) AddSection(menu, [new("키프레임 모두 지우기", () => ClearKeyframesCommand.Execute(null))]);
        return menu;
    }

    /// <summary>클립이 없는 곳의 메뉴.</summary>
    public List<MenuEntry> EmptyMenu() =>
    [
        new("붙여넣기", () => PasteCommand.Execute(null), "Ctrl+V"),
        new("마커 추가", () => AddMarkerCommand.Execute(null), "M"),
    ];

    private static void AddSection(List<MenuEntry> menu, List<MenuEntry> items)
    {
        if (items.Count == 0) return;
        menu.Add(MenuEntry.Separator);
        menu.AddRange(items);
    }
}
