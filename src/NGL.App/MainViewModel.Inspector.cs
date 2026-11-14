using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using NGL.Core;

namespace NGL.App;

/// <summary>속성 패널: 선택한 오디오 클립과 그 트랙의 볼륨·페이드·자동 감쇠.</summary>
public partial class MainViewModel
{
    private static readonly string[] InspectorProperties =
    [
        nameof(HasAudioSelection), nameof(InspectorTitle), nameof(ClipVolume), nameof(FadeIn), nameof(FadeOut),
        nameof(TrackVolume), nameof(DuckOptions), nameof(DuckIndex), nameof(DuckLevel),
    ];

    /// <summary>선택한 오디오 클립들. 패널에는 첫 클립의 값이 보이고, 고치면 모두에 적용된다.</summary>
    private List<AudioClip> SelectedAudioClips() => SelectedClips().OfType<AudioClip>().ToList();

    private AudioTrack? SelectedAudioTrack() =>
        SelectedAudioClips().FirstOrDefault() is { } clip ? Project?.Sequence.TrackOf(clip) as AudioTrack : null;

    private List<AudioTrack> OtherAudioTracks() => SelectedAudioTrack() is { } track
        ? Project!.Sequence.Tracks.OfType<AudioTrack>().Where(other => other != track).ToList()
        : [];

    public bool HasAudioSelection => SelectedAudioTrack() is not null;

    public string InspectorTitle => SelectedAudioClips() is { Count: > 0 } clips && SelectedAudioTrack() is { } track
        ? clips.Count == 1 ? $"오디오 클립 · {track.Name}" : $"오디오 클립 {clips.Count}개 · {track.Name}"
        : "";

    /// <summary>클립 볼륨(%).</summary>
    public string ClipVolume
    {
        get => Percent(Shown(SelectedAudioClips().FirstOrDefault(), AnimatedProperty.Volume, SelectedAudioClips().FirstOrDefault()?.Volume));
        set => SetNumber(value, 0, 200, number => SetAnimated("볼륨 변경", AnimatedProperty.Volume, number / 100, (clip, volume) => ((AudioClip)clip).Volume = volume));
    }

    /// <summary>페이드 인(초).</summary>
    public string FadeIn
    {
        get => Seconds(SelectedAudioClips().FirstOrDefault()?.FadeIn);
        set => SetNumber(value, 0, 600, number => EditAudio("페이드 인 변경", clip => clip.FadeIn = TimeSpan.FromSeconds(number)));
    }

    /// <summary>페이드 아웃(초).</summary>
    public string FadeOut
    {
        get => Seconds(SelectedAudioClips().FirstOrDefault()?.FadeOut);
        set => SetNumber(value, 0, 600, number => EditAudio("페이드 아웃 변경", clip => clip.FadeOut = TimeSpan.FromSeconds(number)));
    }

    /// <summary>트랙 볼륨(%).</summary>
    public string TrackVolume
    {
        get => Percent(SelectedAudioTrack()?.Volume);
        set => SetNumber(value, 0, 200, number =>
        {
            if (SelectedAudioTrack() is { } track) Edit("트랙 볼륨 변경", () => track.Volume = number / 100);
        });
    }

    /// <summary>자동 감쇠의 기준 트랙 후보: "없음"과 다른 오디오 트랙들.</summary>
    public List<string> DuckOptions => ["없음", .. OtherAudioTracks().Select(track => track.Name)];

    public int DuckIndex
    {
        get => SelectedAudioTrack() is { DuckUnder: { } id } ? OtherAudioTracks().FindIndex(track => track.Id == id) + 1 : 0;
        set
        {
            if (SelectedAudioTrack() is not { } track || value < 0) return;
            var others = OtherAudioTracks();
            Guid? trigger = value >= 1 && value <= others.Count ? others[value - 1].Id : null;
            if (trigger != track.DuckUnder) Edit("자동 감쇠 변경", () => track.DuckUnder = trigger);
        }
    }

    /// <summary>자동 감쇠 중의 볼륨(%).</summary>
    public string DuckLevel
    {
        get => Percent(SelectedAudioTrack()?.DuckLevel);
        set => SetNumber(value, 0, 100, number =>
        {
            if (SelectedAudioTrack() is { } track) Edit("감쇠량 변경", () => track.DuckLevel = number / 100);
        });
    }

    /// <summary>선택한 클립의 볼륨·페이드를 같은 트랙의 모든 클립에 적용한다.</summary>
    [RelayCommand]
    private void ApplyToTrack()
    {
        if (SelectedAudioClips().FirstOrDefault() is not { } source || SelectedAudioTrack() is not { } track) return;
        Edit("트랙 전체에 적용", () =>
        {
            foreach (var clip in track.Clips.OfType<AudioClip>())
            {
                clip.Volume = source.Volume;
                clip.FadeIn = source.FadeIn;
                clip.FadeOut = source.FadeOut;
            }
        });
    }

    private void EditAudio(string name, Action<AudioClip> change)
    {
        var clips = SelectedAudioClips();
        Edit(name, () => clips.ForEach(change));
    }

    /// <summary>선택이나 값이 바뀌었을 때 패널이 다시 읽게 한다.</summary>
    private void RefreshInspector()
    {
        foreach (var property in InspectorProperties) OnPropertyChanged(property);
        foreach (var property in VisualProperties) OnPropertyChanged(property);
        foreach (var property in KeyframeProperties) OnPropertyChanged(property);
        foreach (var property in TextProperties) OnPropertyChanged(property);
        foreach (var property in EffectProperties) OnPropertyChanged(property);
        foreach (var property in MaskProperties) OnPropertyChanged(property);
        foreach (var property in SpeedProperties) OnPropertyChanged(property);
    }

    /// <summary>재생 중인 소리의 좌·우 크기. 읽으면 다시 0부터 잰다.</summary>
    public (float Left, float Right) ReadLevels() => _engine?.Meter.Read() ?? (0, 0);

    private static string Percent(double? ratio) =>
        ratio is { } value ? Math.Round(value * 100).ToString(CultureInfo.CurrentCulture) : "";

    private static string Seconds(TimeSpan? time) =>
        time is { } value ? value.TotalSeconds.ToString("0.##", CultureInfo.CurrentCulture) : "";

    /// <summary>숫자로 읽을 수 있으면 범위 안으로 맞춰 적용하고, 아니면 원래 값을 다시 보여준다.</summary>
    private void SetNumber(string text, double minimum, double maximum, Action<double> apply)
    {
        if (double.TryParse(text.Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var number))
            apply(Math.Clamp(number, minimum, maximum));
        RefreshInspector();
    }
}
