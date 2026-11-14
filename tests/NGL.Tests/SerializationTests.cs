using NGL.Core;
using NGL.IO;

namespace NGL.Tests;

public class SerializationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ngvid-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Project SampleProject()
    {
        var project = new Project { Name = "여행 브이로그 1편" };
        project.Sequence.Preset = SequencePreset.Portrait;
        project.Sequence.FrameRate = 60;

        var video = new MediaAsset { Kind = MediaKind.Video, Name = "interview", RelativePath = "media/interview.mp4", Duration = Sec(90) };
        var bgm = new MediaAsset { Kind = MediaKind.Audio, Name = "bgm", RelativePath = "media/bgm.mp3", Duration = Sec(180) };
        var logo = new MediaAsset { Kind = MediaKind.Image, Name = "logo", RelativePath = "media/logo.png" };
        project.Assets.AddRange([video, bgm, logo]);

        var videoTrack = new VideoTrack { Name = "영상 1", Color = "#FF8800" };
        videoTrack.Clips.Add(new VideoClip { AssetId = video.Id, Range = new TimeRange(Sec(0), Sec(10)), SourceStart = Sec(5) });
        videoTrack.Clips.Add(new ImageClip { AssetId = logo.Id, Range = new TimeRange(Sec(10), Sec(3)) });
        videoTrack.Clips.Add(new TextClip { Text = "제목 \"첫 장면\"", Range = new TimeRange(project.Sequence.TimeAt(781), Sec(2)) });

        var audioTrack = new AudioTrack { Name = "BGM" };
        audioTrack.Clips.Add(new AudioClip { AssetId = bgm.Id, Range = new TimeRange(Sec(0), Sec(15)), SourceStart = Sec(1.5) });

        var captionTrack = new CaptionTrack { Name = "한국어 자막" };
        captionTrack.Clips.Add(new CaptionClip { Text = "안녕하세요", Range = new TimeRange(Sec(1), Sec(2)) });

        project.Sequence.Tracks.AddRange([videoTrack, audioTrack, captionTrack]);
        return project;
    }

    [Fact]
    public void 프로젝트를_저장했다가_읽으면_같은_내용이다()
    {
        var project = SampleProject();

        var json = ProjectSerializer.Serialize(project);
        var loaded = ProjectSerializer.Deserialize(json);

        Assert.Equal(json, ProjectSerializer.Serialize(loaded));

        Assert.Equal("여행 브이로그 1편", loaded.Name);
        Assert.Equal(SequencePreset.Portrait, loaded.Sequence.Preset);
        Assert.Equal(60, loaded.Sequence.FrameRate);
        Assert.Equal(project.Assets.Select(a => a.Id), loaded.Assets.Select(a => a.Id));
        Assert.Equal(MediaKind.Audio, loaded.Assets[1].Kind);
        Assert.Equal(Sec(180), loaded.Assets[1].Duration);

        Assert.Collection(loaded.Sequence.Tracks,
            track => Assert.IsType<VideoTrack>(track),
            track => Assert.IsType<AudioTrack>(track),
            track => Assert.IsType<CaptionTrack>(track));
        Assert.Equal(project.Sequence.Tracks[0].Id, loaded.Sequence.Tracks[0].Id);
        Assert.Equal("#FF8800", loaded.Sequence.Tracks[0].Color);

        var clips = loaded.Sequence.Tracks[0].Clips;
        var videoClip = Assert.IsType<VideoClip>(clips[0]);
        Assert.Equal(project.Assets[0].Id, videoClip.AssetId);
        Assert.Equal(new TimeRange(Sec(0), Sec(10)), videoClip.Range);
        Assert.Equal(Sec(5), videoClip.SourceStart);
        Assert.IsType<ImageClip>(clips[1]);
        var textClip = Assert.IsType<TextClip>(clips[2]);
        Assert.Equal("제목 \"첫 장면\"", textClip.Text);
        Assert.Equal(project.Sequence.Tracks[0].Clips[2].Range, textClip.Range);

        Assert.Equal(Sec(1.5), Assert.IsType<AudioClip>(loaded.Sequence.Tracks[1].Clips[0]).SourceStart);
        Assert.Equal("안녕하세요", Assert.IsType<CaptionClip>(loaded.Sequence.Tracks[2].Clips[0]).Text);
        Assert.Equal(project.Sequence.Duration, loaded.Sequence.Duration);
    }

    [Fact]
    public void 저장한_JSON은_사람이_읽을_수_있다()
    {
        var json = ProjectSerializer.Serialize(SampleProject());

        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"name\": \"여행 브이로그 1편\"", json);
        Assert.Contains("\"type\": \"VideoClip\"", json);
        Assert.Contains("\"type\": \"CaptionTrack\"", json);
        Assert.Contains("\"kind\": \"Video\"", json);
        Assert.Contains("\"relativePath\": \"media/interview.mp4\"", json);
        Assert.DoesNotContain("\"end\"", json);
    }

    [Fact]
    public void 파일로_저장하고_읽는다()
    {
        var path = Path.Combine(_folder, "내프로젝트", "project.ngvid");
        var project = SampleProject();

        ProjectSerializer.Save(project, path);
        ProjectSerializer.Save(project, path); // 덮어쓰기
        var loaded = ProjectSerializer.Load(path);

        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(loaded));
        Assert.Equal(["project.ngvid"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void 빈_프로젝트도_저장하고_읽는다()
    {
        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new Project()));

        Assert.Empty(loaded.Sequence.Tracks);
        Assert.Empty(loaded.Assets);
        Assert.Equal(SequencePreset.Landscape, loaded.Sequence.Preset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("이건 JSON이 아니다")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "version": 999 }""")]
    [InlineData("""{ "version": 1, "sequence": { "frameRate": 24 } }""")]
    [InlineData("""{ "version": 1, "sequence": { "tracks": [ { "type": "없는트랙" } ] } }""")]
    [InlineData("""{ "version": 1, "sequence": { "tracks": [ { "type": "AudioTrack", "clips": [ { "type": "VideoClip" } ] } ] } }""")]
    [InlineData("""{ "version": 1, "sequence": { "tracks": [ { "type": "VideoTrack", "clips": [ { "type": "VideoClip", "range": { "start": "00:00:00", "duration": "-00:00:01" } } ] } ] } }""")]
    public void 잘못된_파일은_InvalidDataException으로_알린다(string json) =>
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));

    [Fact]
    public void type이_맨_앞이_아니어도_읽는다()
    {
        var json = """
            { "version": 1, "sequence": { "tracks": [
              { "name": "영상", "clips": [ { "text": "제목", "type": "TextClip" } ], "type": "VideoTrack" }
            ] } }
            """;

        var track = Assert.IsType<VideoTrack>(Assert.Single(ProjectSerializer.Deserialize(json).Sequence.Tracks));
        Assert.Equal("제목", Assert.IsType<TextClip>(Assert.Single(track.Clips)).Text);
    }

    [Fact]
    public void 설정_파일이_없거나_깨졌으면_기본값이다()
    {
        var path = Path.Combine(_folder, "settings.json");
        Assert.Equal(50, AppSettingsStore.Load(path).UndoLimit);

        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ 깨진 파일");
        Assert.Equal(50, AppSettingsStore.Load(path).UndoLimit);
    }

    [Fact]
    public void 설정을_저장하고_읽는다()
    {
        var path = Path.Combine(_folder, "settings.json");

        AppSettingsStore.Save(new AppSettings { UndoLimit = 80 }, path);

        Assert.Equal(80, AppSettingsStore.Load(path).UndoLimit);
    }

    [Fact]
    public void 실행_취소_단계는_1에서_100_사이로_맞춘다()
    {
        Assert.Equal(100, new AppSettings { UndoLimit = 500 }.UndoLimit);
        Assert.Equal(1, new AppSettings { UndoLimit = 0 }.UndoLimit);

        var path = Path.Combine(_folder, "settings.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, """{ "undoLimit": 9999 }""");
        Assert.Equal(100, AppSettingsStore.Load(path).UndoLimit);
    }
}
