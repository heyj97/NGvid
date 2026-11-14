using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NGL.Audio;
using NGL.Core;
using NGL.IO;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.App;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioOutput _output = new();
    private readonly List<AssetItem> _allAssets = [];
    private PlaybackEngine? _engine;
    private string? _folder;
    private bool _followingEngine; // 엔진의 위치를 화면에 옮기는 중 (사용자가 슬라이더를 움직인 것이 아님)
    private TimeSpan _loopStart;
    private TimeSpan? _loopEnd; // null이면 시퀀스 끝까지

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Title), nameof(SequenceText))]
    private Project? _project;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _lastFrameSeconds;
    [ObservableProperty] private string _timeText = "00:00:00:00 / 00:00:00:00";
    [ObservableProperty] private string _loopText = "";
    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private string _audioWarning = "";
    [ObservableProperty] private int _qualityIndex; // 0 전체, 1 = 1/2, 2 = 1/4
    [ObservableProperty] private int _libraryTab;   // 0 영상, 1 사운드, 2 이미지
    [ObservableProperty] private int _missingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    private bool _isPlaying;

    public bool HasProject => Project is not null;
    public string Title => Project is null ? "NGvid" : $"{Project.Name} - NGvid";
    public string PlayButtonText => IsPlaying ? "일시정지" : "재생";

    public string SequenceText => Project?.Sequence is { } sequence
        ? $"{sequence.Preset.Name} · {sequence.Preset.Width}×{sequence.Preset.Height} · {sequence.FrameRate}fps"
        : "";

    /// <summary>보관함의 현재 탭에 보이는 원본들.</summary>
    public ObservableCollection<AssetItem> VisibleAssets { get; } = [];

    /// <summary>미리보기를 다시 그려야 할 때. 화면 스레드가 아닌 곳에서도 불린다.</summary>
    public event Action? RenderRequested;

    private PreviewQuality Quality => (PreviewQuality)(1 << QualityIndex);

    // ───── 프로젝트 ─────

    /// <summary>새 프로젝트 폴더를 만들고 연다. 실패하면 이유를 돌려준다.</summary>
    public string? CreateProject(string folder, string name, SequencePreset preset, int frameRate)
    {
        try
        {
            Load(ProjectFolder.Create(folder, name, preset, frameRate), folder);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return e.Message;
        }
    }

    /// <summary>프로젝트 폴더 또는 그 안의 .ngvid 파일을 연다.</summary>
    public void OpenProject(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
        try
        {
            Load(ProjectFolder.Load(folder), folder);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText = e.Message;
        }
    }

    private void Load(Project project, string folder)
    {
        _folder = folder;
        Project = project;
        StatusText = "";

        _allAssets.Clear();
        foreach (var asset in project.Assets) _allAssets.Add(NewItem(asset));

        _loopStart = TimeSpan.Zero;
        _loopEnd = null;
        IsLooping = false;

        RebuildEngine();
        RefreshLibrary();
    }

    [RelayCommand]
    private void Save()
    {
        if (Project is null || _folder is null) return;
        try
        {
            ProjectFolder.Save(Project, _folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "저장하지 못했습니다: " + e.Message;
        }
    }

    /// <summary>원본 파일 상태가 바뀌었을 수 있을 때 엔진을 새로 만든다. 재생 위치는 유지한다.</summary>
    private void RebuildEngine()
    {
        if (Project is not { } project || _folder is not { } folder) return;

        var position = _engine?.Position ?? TimeSpan.Zero;
        _engine?.Dispose();
        _engine = new PlaybackEngine(project.Sequence,
            id => project.FindAsset(id) is { } asset ? ProjectPaths.AssetFile(folder, asset) : null, _output);
        _engine.Quality = Quality;
        _engine.FrameReady += () => RenderRequested?.Invoke();
        _engine.Seek(position);

        ApplyLoop();
        SequenceChanged();
    }

    /// <summary>클립이 추가·변경된 뒤 길이 표시를 갱신한다.</summary>
    private void SequenceChanged()
    {
        if (_engine is null) return;
        LastFrameSeconds = _engine.LastFrameTime.TotalSeconds;
        ApplyLoop();
        Tick();
    }

    // ───── 리소스 보관함 ─────

    private AssetItem NewItem(MediaAsset asset)
    {
        var item = new AssetItem(asset, Save);
        var path = ProjectPaths.AssetFile(_folder!, asset);
        item.IsMissing = !File.Exists(path);
        if (!item.IsMissing)
        {
            // 썸네일은 디코딩이 필요하므로 뒤에서 만든다.
            Task.Run(() => ThumbnailService.Create(path, asset.Kind, asset.Duration))
                .ContinueWith(task => item.SetThumbnail(task.Result), TaskScheduler.FromCurrentSynchronizationContext());
        }

        return item;
    }

    partial void OnLibraryTabChanged(int value) => RefreshLibrary();

    private void RefreshLibrary()
    {
        var kind = LibraryTab switch { 1 => MediaKind.Audio, 2 => MediaKind.Image, _ => MediaKind.Video };
        VisibleAssets.Clear();
        foreach (var item in _allAssets.Where(item => item.Asset.Kind == kind)) VisibleAssets.Add(item);
        MissingCount = _allAssets.Count(item => item.IsMissing);
    }

    /// <summary>파일들을 프로젝트로 복사해 보관함에 올린다.</summary>
    public void Import(IEnumerable<string> paths)
    {
        if (Project is null || _folder is null) return;

        var failed = new List<string>();
        MediaAsset? last = null;
        foreach (var path in paths)
        {
            try
            {
                last = MediaImporter.Import(_folder, path);
                Project.Assets.Add(last);
                _allAssets.Add(NewItem(last));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or DllNotFoundException)
            {
                failed.Add(Path.GetFileName(path));
            }
        }

        StatusText = failed.Count == 0 ? "" : "가져오지 못한 파일: " + string.Join(", ", failed);
        if (last is null) return;

        // 방금 가져온 종류의 탭을 보여준다.
        LibraryTab = last.Kind switch { MediaKind.Audio => 1, MediaKind.Image => 2, _ => 0 };
        RefreshLibrary();
        Save();
    }

    /// <summary>원본을 타임라인 맨 뒤에 클립으로 놓는다.</summary>
    [RelayCommand]
    private void AddToTimeline(AssetItem item)
    {
        if (Project is null || _engine is null) return;
        _engine.Edit(() => Project.Sequence.AddToEnd(item.Asset));
        SequenceChanged();
        Save();
    }

    /// <summary>사라진 원본 하나를 고른 파일로 다시 연결한다.</summary>
    public void Relink(AssetItem item, string newPath)
    {
        if (_folder is null) return;
        try
        {
            MediaRelinker.Relink(_folder, item.Asset, newPath);
            AfterRelink();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = "다시 연결하지 못했습니다: " + e.Message;
        }
    }

    /// <summary>폴더에서 사라진 원본들과 같은 이름의 파일을 찾아 다시 연결한다.</summary>
    public void RelinkFromFolder(string searchFolder)
    {
        if (Project is null || _folder is null) return;
        var count = MediaRelinker.RelinkFromFolder(Project, _folder, searchFolder);
        StatusText = $"{count}개를 다시 연결했습니다.";
        if (count > 0) AfterRelink();
    }

    private void AfterRelink()
    {
        // 다시 생긴 원본의 썸네일과 디코더를 새로 만든다.
        for (var i = 0; i < _allAssets.Count; i++)
        {
            if (_allAssets[i].IsMissing) _allAssets[i] = NewItem(_allAssets[i].Asset);
        }

        RefreshLibrary();
        RebuildEngine();
    }

    // ───── 재생 ─────

    partial void OnQualityIndexChanged(int value)
    {
        if (_engine is not null) _engine.Quality = Quality;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_engine is null) return;
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Play();
        AudioWarning = _engine.IsPlaying && _output.IsSilent ? "소리 장치를 열 수 없어 소리 없이 재생합니다" : "";
        Tick();
    }

    [RelayCommand]
    private void Step(string frames)
    {
        _engine?.Step(int.Parse(frames));
        Tick();
    }

    /// <summary>현재 위치를 반복 구간의 시작으로 정한다.</summary>
    [RelayCommand]
    private void SetLoopStart()
    {
        if (_engine is null || Project is null) return;
        _loopStart = _engine.Position;
        if (_loopEnd <= _loopStart) _loopEnd = null;
        ApplyLoop();
    }

    /// <summary>현재 프레임까지를 반복 구간으로 정한다.</summary>
    [RelayCommand]
    private void SetLoopEnd()
    {
        if (_engine is null || Project is null) return;
        var sequence = Project.Sequence;
        _loopEnd = sequence.TimeAt(sequence.FrameAt(_engine.Position) + 1);
        if (_loopEnd <= _loopStart) _loopStart = TimeSpan.Zero;
        ApplyLoop();
    }

    [RelayCommand]
    private void ToggleLoop() => IsLooping = !IsLooping;

    partial void OnIsLoopingChanged(bool value) => ApplyLoop();

    private void ApplyLoop()
    {
        if (_engine is null || Project is null) return;
        var end = _loopEnd ?? Project.Sequence.Duration;
        _engine.LoopRange = IsLooping && end > _loopStart ? new TimeRange(_loopStart, end - _loopStart) : null;
        LoopText = $"{Timecode(Project.Sequence, _loopStart)} ~ {Timecode(Project.Sequence, end)}";
    }

    /// <summary>엔진의 현재 위치와 재생 상태를 화면에 옮긴다. 화면을 그릴 때마다 부른다.</summary>
    public void Tick()
    {
        if (_engine is null || Project is null) return;

        var position = _engine.Position;
        _followingEngine = true;
        PositionSeconds = position.TotalSeconds;
        _followingEngine = false;

        IsPlaying = _engine.IsPlaying;
        TimeText = $"{Timecode(Project.Sequence, position)} / {Timecode(Project.Sequence, Project.Sequence.Duration)}";
    }

    // 사용자가 슬라이더를 움직였을 때
    partial void OnPositionSecondsChanged(double value)
    {
        if (_followingEngine) return;
        _engine?.Seek(TimeSpan.FromSeconds(value));
    }

    /// <summary>현재 프레임을 시퀀스 좌표의 캔버스에 그린다.</summary>
    public void Render(SKCanvas canvas) => _engine?.Render(canvas);

    /// <summary>시:분:초:프레임</summary>
    private static string Timecode(Sequence sequence, TimeSpan time)
    {
        var frames = sequence.FrameAt(time);
        var seconds = frames / sequence.FrameRate;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{frames % sequence.FrameRate:00}";
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _output.Dispose();
    }
}
