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
            owner.Edit(() => track.Name = name);
            OnPropertyChanged();
        }
    }

    public bool Locked
    {
        get => track.Locked;
        set => Set(() => track.Locked = value, nameof(Locked));
    }

    public bool Hidden
    {
        get => track.Hidden;
        set => Set(() => track.Hidden = value, nameof(Hidden));
    }

    public bool Muted
    {
        get => track.Muted;
        set => Set(() => track.Muted = value, nameof(Muted));
    }

    public bool Solo
    {
        get => track.Solo;
        set => Set(() => track.Solo = value, nameof(Solo));
    }

    public void SetColor(string color) => Set(() => track.Color = color, nameof(Color));

    private void Set(Action change, string property)
    {
        owner.Edit(change);
        OnPropertyChanged(property);
    }
}
