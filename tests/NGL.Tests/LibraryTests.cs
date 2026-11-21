using NGL.Audio;
using NGL.Core;
using NGL.IO;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.Tests;

/// <summary>
/// 추가 시험 파일: picture.png(64×48, 왼쪽 위 1/4만 흰색), tone-cover.mp3(1초 440Hz, 표지 그림 포함).
/// </summary>
public class LibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ngvid-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static string Sample(string file) => Path.Combine(AppContext.BaseDirectory, "media", file);

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>썸네일의 (x, y) 픽셀 밝기(초록 채널).</summary>
    private static int Green(Thumbnail thumbnail, int x, int y) => thumbnail.Pixels[(y * thumbnail.Width + x) * 4 + 1];

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", true)]
    [InlineData("http://example.com/v.mp4", true)]
    [InlineData("--exec calc", false)] // yt-dlp 옵션으로 읽히면 안 된다
    [InlineData("C:\\video.mp4", false)]
    [InlineData("", false)]
    public void 링크는_http_주소만_받는다(string text, bool expected) =>
        Assert.Equal(expected, LinkDownloader.IsLink(text));

    [Fact]
    public void 새_프로젝트는_폴더와_파일을_만들고_다시_읽을_수_있다()
    {
        var project = ProjectFolder.Create(_folder, "내 프로젝트", SequencePreset.Portrait, 60);

        Assert.True(File.Exists(Path.Combine(_folder, "project.ngvid")));
        Assert.True(Directory.Exists(Path.Combine(_folder, "media")));

        var loaded = ProjectFolder.Load(_folder);
        Assert.Equal("내 프로젝트", loaded.Name);
        Assert.Equal(SequencePreset.Portrait, loaded.Sequence.Preset);
        Assert.Equal(60, loaded.Sequence.FrameRate);
        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(loaded));

        // 이미 프로젝트가 있는 폴더에는 새로 만들지 않는다.
        Assert.Throws<IOException>(() => ProjectFolder.Create(_folder, "다른 이름", SequencePreset.Square, 30));
        Assert.Equal("내 프로젝트", ProjectFolder.Load(_folder).Name);
    }

    /// <summary>스튜디오가 외부 저장한 모양의 폴더를 만든다. 0.bin은 영상, 1.bin은 소리.</summary>
    private string StudioFolder(string assetFile = "0.bin")
    {
        var studio = Path.Combine(_folder, "studio");
        Directory.CreateDirectory(Path.Combine(studio, "media"));
        File.Copy(Sample("frames-h264.mp4"), Path.Combine(studio, "media", "0.bin"));
        File.Copy(Sample("tone-cover.mp3"), Path.Combine(studio, "media", "1.bin"));
        File.WriteAllText(Path.Combine(studio, "project.json"), $$"""
            {
              "format": "new-gen-lab-project", "version": 1,
              "project": {
                "name": "현지화 베이스",
                "videoTracks": [
                  { "id": "main", "name": "영상 1" },
                  { "id": "voice", "type": "audio", "name": "내레이션" },
                  { "id": "music", "type": "bgm", "name": "배경음" }
                ],
                "scenes": [
                  { "id": "a", "asset": "v", "filename": "원본.mp4", "start": 0.5, "end": 1, "timelineStart": 0, "videoX": 10, "videoScale": 150 },
                  { "id": "b", "asset": "v", "filename": "원본.mp4", "start": 0, "end": 0.5, "timelineStart": 0.5, "removed": true },
                  { "id": "c", "asset": "없는-미디어", "start": 0, "end": 1, "timelineStart": 2 }
                ],
                "captions": [
                  { "id": "1", "caption": "둘째 화자", "start": 0.1, "end": 0.4, "speaker": 1 },
                  { "id": "2", "caption": "겹치는 자막", "start": 0.2, "end": 0.3, "speaker": 0 },
                  { "id": "3", "caption": "  ", "start": 1, "end": 2 }
                ],
                "audioClips": [
                  { "id": "n", "trackId": "voice", "asset": "s", "filename": "tone.mp3", "timelineStart": 0, "duration": 0.5 },
                  { "id": "m", "trackId": "music", "asset": "s", "timelineStart": 1, "duration": 0.5, "sourceStart": 0.2, "volume": 0.4, "fadeIn": 0.1, "duck": true }
                ],
                "hooks": [ { "asset": "v", "time": 0.7, "rank": 1, "reason": "놀라운 주장" }, { "asset": "v", "time": 0.2, "rank": 2, "reason": "지운 구간" } ]
              },
              "assets": [ { "id": "v", "file": "{{assetFile}}", "type": "video/mp4" }, { "id": "s", "file": "1.bin", "type": "audio/mpeg" } ]
            }
            """);
        return studio;
    }

    [Fact]
    public void 스튜디오의_외부_저장_폴더를_프로젝트로_바꾼다()
    {
        var target = Path.Combine(_folder, "ngvid");
        var (project, skipped) = StudioProjectImporter.Import(StudioFolder(), target);
        var sequence = project.Sequence;

        Assert.Equal("현지화 베이스", project.Name);
        Assert.Equal(SequencePreset.Portrait, sequence.Preset);
        Assert.Equal(2, skipped); // 미디어가 없는 장면, 겹치는 자막
        Assert.True(File.Exists(Path.Combine(target, "media", "원본.mp4")));
        Assert.True(File.Exists(Path.Combine(target, "media", "tone.mp3")));

        // 자막 트랙이 맨 위, 그 아래 영상, 그 뒤로 오디오.
        Assert.Equal(["자막", "영상 1"], sequence.Tracks.OfType<VideoTrack>().Select(track => track.Name));
        var caption = Assert.IsType<TextClip>(Assert.Single(sequence.Tracks[0].Clips));
        Assert.Equal("둘째 화자", caption.Text);
        Assert.Equal("#AAADFF", caption.Style.Color);
        Assert.Equal(new TimeRange(Sec(0.1), Sec(0.3)), caption.Range);

        // 지운 장면은 빠지고, 남은 장면은 원본의 0.5초부터 0.5초 동안 타임라인 맨 앞에 놓인다.
        var clip = Assert.IsType<VideoClip>(Assert.Single(sequence.Tracks[1].Clips));
        Assert.Equal(new TimeRange(Sec(0), Sec(0.5)), clip.Range);
        Assert.Equal(Sec(0.5), clip.SourceStart);
        Assert.Equal(108, clip.X, 3);
        Assert.Equal(1.5, clip.Scale);

        var music = Assert.IsType<AudioTrack>(sequence.Tracks.Single(track => track.Name == "배경음"));
        var sound = Assert.IsType<AudioClip>(Assert.Single(music.Clips));
        Assert.Equal((Sec(0.2), 0.4, Sec(0.1)), (sound.SourceStart, sound.Volume, sound.FadeIn));
        Assert.Equal(sequence.Tracks.Single(track => track.Name == "내레이션").Id, music.DuckUnder);

        // 후킹 추천은 타임라인에 놓인 구간의 것만 마커가 된다. 원본 0.7초 → 타임라인 0.2초.
        var marker = Assert.Single(sequence.Markers);
        Assert.Equal((Sec(0.2), "★1 놀라운 주장"), (marker.Time, marker.Name));

        // 저장한 뒤 다시 읽어도 같다.
        ProjectFolder.Save(project, target);
        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(ProjectFolder.Load(target)));
    }

    [Fact]
    public void 스튜디오_폴더가_아니거나_미디어_경로가_수상하면_가져오지_않는다()
    {
        Assert.Throws<InvalidDataException>(() => StudioProjectImporter.Import(_folder, Path.Combine(_folder, "a")));
        Assert.Throws<InvalidDataException>(() => StudioProjectImporter.Import(StudioFolder("../0.bin"), Path.Combine(_folder, "b")));
    }

    [Fact]
    public void 가져온_파일은_media로_복사되고_종류와_길이가_기록된다()
    {
        var video = MediaImporter.Import(_folder, Sample("frames-h264.mp4"));
        var image = MediaImporter.Import(_folder, Sample("picture.png"));
        var sound = MediaImporter.Import(_folder, Sample("tone-cover.mp3"));

        Assert.Equal((MediaKind.Video, "frames-h264", "media/frames-h264.mp4", true),
            (video.Kind, video.Name, video.RelativePath, video.HasAudio));
        Assert.InRange(video.Duration.TotalSeconds, 1.95, 2.1);

        Assert.Equal((MediaKind.Image, "picture", "media/picture.png", false),
            (image.Kind, image.Name, image.RelativePath, image.HasAudio));
        Assert.Equal(TimeSpan.Zero, image.Duration);

        // 표지 그림이 있어도 소리 파일이다.
        Assert.Equal((MediaKind.Audio, "tone-cover", "media/tone-cover.mp3", false),
            (sound.Kind, sound.Name, sound.RelativePath, sound.HasAudio));
        Assert.InRange(sound.Duration.TotalSeconds, 0.95, 1.1);

        foreach (var asset in new[] { video, image, sound })
            Assert.True(File.Exists(ProjectPaths.AssetFile(_folder, asset)));
    }

    [Fact]
    public void 같은_이름을_다시_가져오면_번호가_붙고_media_안의_파일은_복사하지_않는다()
    {
        var first = MediaImporter.Import(_folder, Sample("picture.png"));
        var second = MediaImporter.Import(_folder, Sample("picture.png"));
        var third = MediaImporter.Import(_folder, Sample("picture.png"));
        var inside = MediaImporter.Import(_folder, ProjectPaths.AssetFile(_folder, second));

        Assert.Equal("media/picture.png", first.RelativePath);
        Assert.Equal("media/picture (2).png", second.RelativePath);
        Assert.Equal("media/picture (3).png", third.RelativePath);
        Assert.Equal("picture (2)", second.Name);
        Assert.Equal("media/picture (2).png", inside.RelativePath);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_folder, "media")).Length);
    }

    [Fact]
    public void 미디어가_아닌_파일은_가져오지_않는다()
    {
        Directory.CreateDirectory(_folder);
        var text = Path.Combine(_folder, "메모.txt");
        File.WriteAllText(text, "영상이 아닙니다");

        Assert.Throws<InvalidDataException>(() => MediaImporter.Import(_folder, text));
        Assert.False(Directory.Exists(Path.Combine(_folder, "media")));
    }

    [Fact]
    public void 원본을_트랙_끝에_놓으면_종류에_맞는_트랙과_클립이_생긴다()
    {
        var sequence = new Sequence();
        var video = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2), HasAudio = true };
        var silent = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(1) };
        var image = new MediaAsset { Kind = MediaKind.Image };
        var sound = new MediaAsset { Kind = MediaKind.Audio, Duration = Sec(10) };

        var first = sequence.AddToEnd(video);
        Assert.Collection(sequence.Tracks, t => Assert.IsType<VideoTrack>(t), t => Assert.IsType<AudioTrack>(t));
        Assert.Equal(("영상 1", "오디오 1"), (sequence.Tracks[0].Name, sequence.Tracks[1].Name));
        Assert.Collection(first,
            c => Assert.Equal((video.Id, new TimeRange(Sec(0), Sec(2))), (Assert.IsType<VideoClip>(c).AssetId, c.Range)),
            c => Assert.Equal((video.Id, new TimeRange(Sec(0), Sec(2))), (Assert.IsType<AudioClip>(c).AssetId, c.Range)));

        // 소리 파일은 오디오 트랙 끝(2초)에 → 오디오 트랙이 12초까지 찬다.
        Assert.Equal(new TimeRange(Sec(2), Sec(10)), Assert.Single(sequence.AddToEnd(sound)).Range);

        // 소리 없는 영상은 영상 트랙 끝(2초)에, 영상 클립만.
        Assert.Equal(new TimeRange(Sec(2), Sec(1)), Assert.IsType<VideoClip>(Assert.Single(sequence.AddToEnd(silent))).Range);

        // 이미지는 영상 트랙 끝(3초)에 5초 길이로.
        Assert.Equal(new TimeRange(Sec(3), Sec(5)), Assert.IsType<ImageClip>(Assert.Single(sequence.AddToEnd(image))).Range);

        // 소리 있는 영상은 영상·오디오가 같은 자리에 놓이도록 더 늦게 끝나는 트랙(오디오 12초) 뒤에.
        var last = sequence.AddToEnd(video);
        Assert.All(last, c => Assert.Equal(new TimeRange(Sec(12), Sec(2)), c.Range));

        Assert.Equal(2, sequence.Tracks.Count);
        Assert.Equal(4, sequence.Tracks[0].Clips.Count);
        Assert.Equal(3, sequence.Tracks[1].Clips.Count);
    }

    [Fact]
    public void 썸네일은_영상과_이미지에서_만들고_소리에는_없다()
    {
        var video = ThumbnailService.Create(Sample("frames-h264.mp4"), MediaKind.Video, Sec(2))!;
        Assert.Equal((160, 90), (video.Width, video.Height)); // 320을 240 이하로: 1/2
        // 2초의 10% = 0.2초 → 6번 프레임의 밝기
        Assert.Equal(6, (int)Math.Round(Green(video, 80, 45) * 219.0 / 255.0 / 3));

        var image = ThumbnailService.Create(Sample("picture.png"), MediaKind.Image, TimeSpan.Zero)!;
        Assert.Equal((64, 48), (image.Width, image.Height));
        Assert.InRange(Green(image, 5, 5), 200, 255);
        Assert.InRange(Green(image, 60, 5), 0, 50);
        Assert.InRange(Green(image, 5, 44), 0, 50);

        Assert.Null(ThumbnailService.Create(Sample("tone-cover.mp3"), MediaKind.Audio, Sec(1)));
        Assert.Null(ThumbnailService.Create(Path.Combine(_folder, "없는파일.mp4"), MediaKind.Video, Sec(1)));
    }

    [Theory]
    [InlineData("rotated-90.mp4", 10, 310)]  // 왼쪽 아래
    [InlineData("rotated-270.mp4", 170, 10)] // 오른쪽 위
    public void 회전된_영상의_썸네일도_돌려서_만든다(string file, int whiteX, int whiteY)
    {
        var thumbnail = ThumbnailService.Create(Sample(file), MediaKind.Video, Sec(0.2))!;

        Assert.Equal((180, 320), (thumbnail.Width, thumbnail.Height));
        foreach (var (x, y) in new[] { (10, 10), (170, 10), (10, 310), (170, 310) })
        {
            var expectWhite = x == whiteX && y == whiteY;
            Assert.InRange(Green(thumbnail, x, y), expectWhite ? 200 : 0, expectWhite ? 255 : 50);
        }
    }

    [Fact]
    public void 사라진_원본을_찾아_다시_연결한다()
    {
        var project = ProjectFolder.Create(_folder, "테스트", SequencePreset.Landscape, 30);
        project.Assets.Add(MediaImporter.Import(_folder, Sample("frames-h264.mp4")));
        project.Assets.Add(MediaImporter.Import(_folder, Sample("picture.png")));
        Assert.Empty(MediaRelinker.FindMissing(project, _folder));

        foreach (var asset in project.Assets) File.Delete(ProjectPaths.AssetFile(_folder, asset));
        Assert.Equal(project.Assets, MediaRelinker.FindMissing(project, _folder));

        // 파일 하나를 직접 지정 (이름이 달라도 된다)
        var renamed = Path.Combine(_folder, "다른 이름.mp4");
        File.Copy(Sample("frames-h264.mp4"), renamed);
        MediaRelinker.Relink(_folder, project.Assets[0], renamed);
        Assert.Equal([project.Assets[1]], MediaRelinker.FindMissing(project, _folder));
        Assert.Equal("media/frames-h264.mp4", project.Assets[0].RelativePath);

        // 미디어가 아닌 파일로는 연결하지 않는다.
        var text = Path.Combine(_folder, "메모.txt");
        File.WriteAllText(text, "영상이 아닙니다");
        Assert.Throws<InvalidDataException>(() => MediaRelinker.Relink(_folder, project.Assets[1], text));

        // 폴더를 지정하면 같은 이름을 하위 폴더까지 찾는다.
        Assert.Equal(0, MediaRelinker.RelinkFromFolder(project, _folder, Path.GetTempPath() + "ngvid-없는폴더-" + Guid.NewGuid()));
        Assert.Equal(1, MediaRelinker.RelinkFromFolder(project, _folder, Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\'))!));
        Assert.Empty(MediaRelinker.FindMissing(project, _folder));
    }

    [Fact]
    public void 이미지_클립도_미리보기에_그려진다()
    {
        var project = ProjectFolder.Create(_folder, "테스트", SequencePreset.Landscape, 30);
        var image = MediaImporter.Import(_folder, Sample("picture.png"));
        project.Assets.Add(image);
        project.Sequence.AddToEnd(image);

        using var engine = new PlaybackEngine(project.Sequence,
            id => project.FindAsset(id) is { } asset ? ProjectPaths.AssetFile(_folder, asset) : null, new StopwatchAudioOutput());
        using var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
        using var canvas = new SKCanvas(bitmap);

        // 64×48(4:3)을 1920×1080에 맞추면 가운데 1440×1080. 그림의 왼쪽 위 1/4이 흰색이다.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            engine.Render(canvas);
            Thread.Sleep(5);
        } while (bitmap.GetPixel(400, 200).Green < 200 && DateTime.UtcNow < deadline);

        Assert.InRange(bitmap.GetPixel(400, 200).Green, 200, 255); // 흰 부분
        Assert.InRange(bitmap.GetPixel(1500, 900).Green, 0, 50);   // 그림의 검은 부분
        Assert.Equal(SKColors.Black, bitmap.GetPixel(100, 500));   // 그림 밖
    }
}
