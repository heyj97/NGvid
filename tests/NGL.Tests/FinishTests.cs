using NGL.Core;
using NGL.Export;
using NGL.IO;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.Tests;

/// <summary>자동 백업·복구, 프록시, 설정.</summary>
public sealed class FinishTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ngvid-finish-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static DateTime At(int minute) => new(2026, 1, 1, 10, minute, 0);

    private Project NewProject(string name = "처음")
    {
        var project = ProjectFolder.Create(_folder, name, SequencePreset.Landscape, 30);
        return project;
    }

    private void Rename(Project project, string name)
    {
        project.Name = name;
        ProjectFolder.Save(project, _folder);
    }

    // ───── 백업 ─────

    [Fact]
    public void 백업은_내용이_바뀌었을_때만_쌓이고_오래된_것부터_지워진다()
    {
        var project = NewProject();

        Assert.True(ProjectBackup.Create(_folder, keep: 3, At(0)));
        Assert.False(ProjectBackup.Create(_folder, keep: 3, At(1))); // 그대로면 쌓지 않는다

        for (var i = 2; i <= 5; i++)
        {
            Rename(project, $"이름 {i}");
            Assert.True(ProjectBackup.Create(_folder, keep: 3, At(i)));
        }

        var backups = ProjectBackup.List(_folder);
        Assert.Equal([At(5), At(4), At(3)], backups.Select(ProjectBackup.TimeOf)); // 새것부터 3개
        Assert.Equal("이름 5", ProjectSerializer.Load(backups[0]).Name);
    }

    [Fact]
    public void 복구하면_그때의_상태로_돌아가고_복구_전의_상태도_남는다()
    {
        var project = NewProject();
        ProjectBackup.Create(_folder, 10, At(0));
        Rename(project, "나중");

        ProjectBackup.Restore(_folder, ProjectBackup.List(_folder)[^1], 10);

        Assert.Equal("처음", ProjectFolder.Load(_folder).Name);
        Assert.Contains(ProjectBackup.List(_folder), backup => ProjectSerializer.Load(backup).Name == "나중");
    }

    [Fact]
    public void 깨진_백업으로는_복구하지_않는다()
    {
        NewProject();
        Directory.CreateDirectory(ProjectPaths.BackupFolder(_folder));
        var broken = Path.Combine(ProjectPaths.BackupFolder(_folder), "project-20260101-100000-000.ngvid");
        File.WriteAllText(broken, "{ 깨짐");

        Assert.ThrowsAny<Exception>(() => ProjectBackup.Restore(_folder, broken, 10));
        Assert.Equal("처음", ProjectFolder.Load(_folder).Name);
    }

    [Fact]
    public void 프로젝트가_없는_폴더에서는_백업할_것이_없다()
    {
        Directory.CreateDirectory(_folder);

        Assert.False(ProjectBackup.Create(_folder, 5));
        Assert.Empty(ProjectBackup.List(_folder));
    }

    // ───── 설정 ─────

    [Fact]
    public void 설정은_범위_안으로_맞춰지고_저장했다_읽어도_그대로다()
    {
        var settings = new AppSettings { BackupMinutes = -3, BackupCount = 0, UseProxies = false, UndoLimit = 70 };
        Assert.Equal((0, 1), (settings.BackupMinutes, settings.BackupCount));

        var path = Path.Combine(_folder, "settings.json");
        AppSettingsStore.Save(settings, path);
        var loaded = AppSettingsStore.Load(path);

        Assert.Equal((70, 0, 1, false), (loaded.UndoLimit, loaded.BackupMinutes, loaded.BackupCount, loaded.UseProxies));
        Assert.True(new AppSettings().UseProxies);
        Assert.Equal(5, AppSettingsStore.Load(Path.Combine(_folder, "없음.json")).BackupMinutes);
    }

    // ───── 프록시 ─────

    [Fact]
    public void 프록시는_작은_크기로_만들어지고_같은_시각에_같은_프레임을_보여준다()
    {
        var project = NewProject();
        var asset = MediaImporter.Import(_folder, MediaTests.SamplePath);
        project.Assets.Add(asset);
        Assert.False(ProxyBuilder.Exists(_folder, asset));
        Assert.Equal(ProjectPaths.AssetFile(_folder, asset), ProxyBuilder.PreviewFile(_folder, asset));

        var progress = new List<double>();
        ProxyBuilder.Build(_folder, asset, new SynchronousProgress(progress.Add));

        Assert.True(ProxyBuilder.Exists(_folder, asset));
        var proxy = ProxyBuilder.PreviewFile(_folder, asset);
        Assert.Equal(ProjectPaths.ProxyFile(_folder, asset), proxy);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(proxy)!, "*.tmp*")); // 만들다 만 파일이 남지 않는다
        Assert.Equal(1, progress[^1]);

        var original = MediaProbe.Probe(ProjectPaths.AssetFile(_folder, asset));
        var info = MediaProbe.Probe(proxy);
        Assert.True(info.Video!.Height <= ProxyBuilder.MaxHeight);
        Assert.Equal(original.Video!.Width * info.Video.Height / original.Video.Height, info.Video.Width, 2.0);
        Assert.InRange(info.Duration.TotalSeconds, original.Duration.TotalSeconds - 0.1, original.Duration.TotalSeconds + 0.1);

        // 프록시로 읽어도 원본과 같은 프레임 번호가 나온다(밝기가 프레임 번호다).
        using var frames = new VideoFrameSource(_ => proxy);
        var clip = new VideoClip { AssetId = asset.Id, Range = new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(2)) };
        foreach (var frame in new[] { 0, 7, 30, 59 })
        {
            frames.BeginFrame();
            using var image = frames.GetImage(clip, TimeSpan.FromSeconds((frame + 0.5) / 30));
            using var bitmap = SKBitmap.FromImage(image);
            Assert.InRange(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).Green * 219.0 / 255.0 / 3, frame - 0.7, frame + 0.7);
        }
    }

    [Fact]
    public void 영상이_아닌_원본은_프록시를_만들지_않는다()
    {
        var project = NewProject();
        var image = MediaImporter.Import(_folder, Path.Combine(AppContext.BaseDirectory, "media", "picture.png"));
        project.Assets.Add(image);

        Assert.Throws<InvalidDataException>(() => ProxyBuilder.Build(_folder, image));
        Assert.Equal(ProjectPaths.AssetFile(_folder, image), ProxyBuilder.PreviewFile(_folder, image));
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
