using CommunityToolkit.Mvvm.ComponentModel;
using NGL.Core;

namespace NGL.App;

/// <summary>타임라인 왼쪽의 트랙 머리 하나.</summary>
public class TrackItem(Track track, MainViewModel owner) : ObservableObject
{
    public static IReadOnlyList<string> Palette { get; } =
        ["#4C8DFF", "#8B6CC7", "#3FA87A", "#C79A2B", "#D9623B", "#D94F7A", "#2FA6B8", "#8E949C"];

    public Track Track => track;

    /// <summary>오디오 트랙은 음소거·솔로, 그 밖의 트랙은 숨김 버튼을 보인다.</summary>
    public bool IsAudio => track is AudioTrack;
    public bool IsVisual => track is not AudioTrack;

    public string Color => track.Color;

    public string Name
    {
        get => track.Name;
        set
        {
            var name = value.Trim();
            if (name.Length == 0 || name == track.Name) return;
            owner.Edit("트랙 이름 변경", () => track.Name = name);
        }
    }

    public bool Locked
    {
        get => track.Locked;
        set => owner.Edit(value ? "트랙 잠금" : "트랙 잠금 해제", () => track.Locked = value);
    }

    public bool Hidden
    {
        get => track.Hidden;
        set => owner.Edit(value ? "트랙 숨김" : "트랙 표시", () => track.Hidden = value);
    }

    public bool Muted
    {
        get => track.Muted;
        set => owner.Edit(value ? "음소거" : "음소거 해제", () => track.Muted = value);
    }

    public bool Solo
    {
        get => track.Solo;
        set => owner.Edit(value ? "솔로" : "솔로 해제", () => track.Solo = value);
    }

    public void SetColor(string color) => owner.Edit("트랙 색상 변경", () => track.Color = color);

    /// <summary>편집이나 실행 취소 뒤에 화면이 값을 다시 읽게 한다.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
