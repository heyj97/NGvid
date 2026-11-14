using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NGL.Core;
using NGL.Media;

namespace NGL.App;

/// <summary>타임라인: 트랙, 마커, 확대·스크롤, 파형.</summary>
public partial class MainViewModel
{
    private readonly Dictionary<Guid, Waveform> _waveforms = [];

    [ObservableProperty] private double _timelineZoom = 0.7; // 0~1, TimelineView.PixelsPerSecond 참고
    [ObservableProperty] private double _timelineOffset;     // 타임라인 왼쪽 끝의 시각(초)
    [ObservableProperty] private bool _snapEnabled = true;

    /// <summary>타임라인에 그려지는 내용이 바뀔 때마다 올라간다.</summary>
    public int TimelineVersion { get; private set; }

    /// <summary>트랙 머리에 보이는 트랙들.</summary>
    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public TimeSpan Position => _engine?.Position ?? TimeSpan.Zero;

    public Waveform? WaveformOf(Guid assetId) => _waveforms.GetValueOrDefault(assetId);

    public AssetItem? ItemOf(Guid assetId) => _allAssets.FirstOrDefault(item => item.Asset.Id == assetId);

    /// <summary>시퀀스를 바꾸고, 화면·길이를 갱신하고, 저장한다.</summary>
    internal void Edit(Action change)
    {
        if (_engine is null) return;
        _engine.Edit(change);
        TimelineVersion++;
        SequenceChanged();
        Save();
    }

    private void RefreshTracks()
    {
        Tracks.Clear();
        if (Project is null) return;
        foreach (var track in Project.Sequence.Tracks) Tracks.Add(new TrackItem(track, this));
        TimelineVersion++;
    }

    // ───── 트랙 ─────

    [RelayCommand]
    private void AddTrack(string kind)
    {
        if (Project is not { } project) return;
        Edit(() =>
        {
            switch (kind)
            {
                case "video": project.Sequence.AddTrack<VideoTrack>(); break;
                case "audio": project.Sequence.AddTrack<AudioTrack>(); break;
                default: project.Sequence.AddTrack<CaptionTrack>(); break;
            }
        });
        RefreshTracks();
    }

    public void DeleteTrack(Track track)
    {
        Edit(() => Project?.Sequence.Tracks.Remove(track));
        RefreshTracks();
    }

    public void MoveTrack(Track track, int offset)
    {
        Edit(() => Project?.Sequence.MoveTrack(track, offset));
        RefreshTracks();
    }

    // ───── 마커 ─────

    /// <summary>재생 위치에 마커를 꽂는다. 이미 있으면 그대로 둔다.</summary>
    [RelayCommand]
    private void AddMarker()
    {
        if (Project is not { } project) return;
        var time = Position;
        if (project.Sequence.Markers.Any(marker => marker.Time == time)) return;
        Edit(() => project.Sequence.Markers.Add(new Marker { Time = time, Name = $"마커 {project.Sequence.Markers.Count + 1}" }));
    }

    public void RenameMarker(Marker marker, string name) => Edit(() => marker.Name = name.Trim());

    public void DeleteMarker(Marker marker) => Edit(() => Project?.Sequence.Markers.Remove(marker));

    // ───── 탐색과 배치 ─────

    /// <summary>타임라인에서 고른 시각으로 간다. 스냅이 켜져 있으면 가까운 클립 경계·마커에 붙는다.</summary>
    public void SeekFromTimeline(TimeSpan time, TimeSpan snapTolerance)
    {
        if (_engine is null || Project is null) return;
        if (SnapEnabled) time = Sequence.Snap(time, Project.Sequence.SnapPoints(), snapTolerance);
        _engine.Seek(time);
        Tick();
    }

    /// <summary>보관함의 원본을 타임라인의 지정한 트랙·시각에 놓는다. 자리가 없으면 트랙 맨 뒤에 놓인다.</summary>
    public void AddToTimelineAt(AssetItem item, Track? track, TimeSpan time, TimeSpan snapTolerance)
    {
        if (Project is not { } project) return;
        if (SnapEnabled) time = Sequence.Snap(time, project.Sequence.SnapPoints().Append(Position), snapTolerance);
        Edit(() => project.Sequence.Add(item.Asset, track, time));
        RefreshTracks();
    }

    /// <summary>소리가 있는 원본의 파형을 뒤에서 만든다.</summary>
    private void LoadWaveform(MediaAsset asset, string path)
    {
        if (asset.Kind != MediaKind.Audio && !asset.HasAudio) return;

        Task.Run(() => WaveformService.Create(path)).ContinueWith(task =>
        {
            if (task.Result is null) return;
            _waveforms[asset.Id] = task.Result;
            TimelineVersion++;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
