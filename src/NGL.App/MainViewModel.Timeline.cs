using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NGL.Commands;
using NGL.Core;
using NGL.Media;

namespace NGL.App;

/// <summary>타임라인: 트랙, 마커, 확대·스크롤, 파형, 실행 취소.</summary>
public partial class MainViewModel
{
    private readonly Dictionary<Guid, Waveform> _waveforms = [];
    private readonly CommandHistory _history = new();

    [ObservableProperty] private double _timelineZoom = 0.7; // 0~1, TimelineView.PixelsPerSecond 참고
    [ObservableProperty] private double _timelineOffset;     // 타임라인 왼쪽 끝의 시각(초)
    [ObservableProperty] private bool _snapEnabled = true;

    /// <summary>타임라인에 그려지는 내용이 바뀔 때마다 올라간다.</summary>
    public int TimelineVersion { get; private set; }

    /// <summary>트랙 머리에 보이는 트랙들.</summary>
    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public TimeSpan Position => _engine?.Position ?? TimeSpan.Zero;

    public string UndoText => _history.UndoName is { } name ? $"실행 취소: {name}" : "실행 취소";
    public string RedoText => _history.RedoName is { } name ? $"다시 실행: {name}" : "다시 실행";

    public Waveform? WaveformOf(Guid assetId) => _waveforms.GetValueOrDefault(assetId);

    public AssetItem? ItemOf(Guid assetId) => _allAssets.FirstOrDefault(item => item.Asset.Id == assetId);

    // ───── 편집과 실행 취소 ─────

    /// <summary>
    /// 시퀀스를 바꾼다. 모든 편집이 여기를 거치므로 실행 취소·화면 갱신·저장이 한 곳에서 처리된다.
    /// 실제로 바뀐 것이 없으면 기록하지 않는다.
    /// </summary>
    internal void Edit(string name, Action change)
    {
        if (_engine is null || Project is null) return;

        var command = new SequenceEditCommand(name, Project.Sequence, change);
        _engine.Edit(command.Execute);
        if (!command.Changed) return;

        _history.Record(command);
        AfterEdit();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_engine is null || !_history.CanUndo) return;
        _engine.Edit(() => _history.Undo());
        AfterEdit();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_engine is null || !_history.CanRedo) return;
        _engine.Edit(() => _history.Redo());
        AfterEdit();
    }

    private void AfterEdit()
    {
        TimelineVersion++;
        SyncTracks();
        PruneSelection();
        SequenceChanged();
        Save();
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        RefreshInspector();
    }

    /// <summary>트랙 머리를 시퀀스와 맞춘다. 트랙이 그대로면 값만 다시 읽게 하고, 달라졌으면 새로 만든다.</summary>
    private void SyncTracks()
    {
        var tracks = Project?.Sequence.Tracks ?? [];
        if (Tracks.Select(item => item.Track).SequenceEqual(tracks))
        {
            foreach (var item in Tracks) item.Refresh();
            return;
        }

        Tracks.Clear();
        foreach (var track in tracks) Tracks.Add(new TrackItem(track, this));
    }

    // ───── 트랙 ─────

    [RelayCommand]
    private void AddTrack(string kind)
    {
        if (Project is not { } project) return;
        Edit("트랙 추가", () =>
        {
            switch (kind)
            {
                case "video": project.Sequence.AddTrack<VideoTrack>(); break;
                case "audio": project.Sequence.AddTrack<AudioTrack>(); break;
                default: project.Sequence.AddTrack<CaptionTrack>(); break;
            }
        });
    }

    public void DeleteTrack(Track track) => Edit("트랙 삭제", () => Project?.Sequence.Tracks.Remove(track));

    public void MoveTrack(Track track, int offset) => Edit("트랙 순서 변경", () => Project?.Sequence.MoveTrack(track, offset));

    // ───── 마커 ─────

    /// <summary>재생 위치에 마커를 꽂는다. 이미 있으면 그대로 둔다.</summary>
    [RelayCommand]
    private void AddMarker()
    {
        if (Project is not { } project) return;
        var time = Position;
        if (project.Sequence.Markers.Any(marker => marker.Time == time)) return;
        Edit("마커 추가", () => project.Sequence.Markers.Add(
            new Marker { Time = time, Name = $"마커 {project.Sequence.Markers.Count + 1}" }));
    }

    public void RenameMarker(Marker marker, string name) => Edit("마커 이름 변경", () => marker.Name = name.Trim());

    public void DeleteMarker(Marker marker) => Edit("마커 삭제", () => Project?.Sequence.Markers.Remove(marker));

    // ───── 탐색과 배치 ─────

    /// <summary>
    /// 스냅이 켜져 있으면 tolerance 안의 클립 경계·마커·재생 위치에 붙인 시각을 돌려준다.
    /// exclude의 클립(지금 옮기는 클립들)의 경계에는 붙지 않는다.
    /// </summary>
    public TimeSpan SnapTime(TimeSpan time, TimeSpan tolerance, IReadOnlyCollection<Clip>? exclude = null, bool toPlayhead = true)
    {
        if (!SnapEnabled || Project is not { } project) return time;

        var points = project.Sequence.Tracks.SelectMany(track => track.Clips)
            .Where(clip => exclude is null || !exclude.Contains(clip))
            .SelectMany(clip => new[] { clip.Range.Start, clip.Range.End })
            .Concat(project.Sequence.Markers.Select(marker => marker.Time))
            .Append(TimeSpan.Zero);
        if (toPlayhead) points = points.Append(Position);
        return Sequence.Snap(time, points, tolerance);
    }

    /// <summary>타임라인에서 고른 시각으로 간다.</summary>
    public void SeekFromTimeline(TimeSpan time, TimeSpan snapTolerance)
    {
        if (_engine is null) return;
        _engine.Seek(SnapTime(time, snapTolerance, toPlayhead: false));
        Tick();
    }

    /// <summary>보관함의 원본을 타임라인의 지정한 트랙·시각에 놓는다. 자리가 없으면 트랙 맨 뒤에 놓인다.</summary>
    public void AddToTimelineAt(AssetItem item, Track? track, TimeSpan time, TimeSpan snapTolerance)
    {
        if (Project is not { } project) return;
        time = SnapTime(time, snapTolerance);
        Edit("클립 추가", () => project.Sequence.Add(item.Asset, track, time));
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
