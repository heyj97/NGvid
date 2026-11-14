using NGL.Core;
using NGL.Media;

namespace NGL.Export;

/// <summary>영상 원본의 프록시(세로 540 이하의 작은 MP4)를 만든다. 큰 원본도 미리보기가 가벼워진다.</summary>
public static class ProxyBuilder
{
    public const int MaxHeight = 540;

    /// <summary>원본보다 새 프록시가 있는가.</summary>
    public static bool Exists(string projectFolder, MediaAsset asset)
    {
        var proxy = ProjectPaths.ProxyFile(projectFolder, asset);
        return File.Exists(proxy) && File.GetLastWriteTimeUtc(proxy) >= File.GetLastWriteTimeUtc(ProjectPaths.AssetFile(projectFolder, asset));
    }

    /// <summary>미리보기에서 쓸 파일: 프록시가 있으면 프록시, 없으면 원본.</summary>
    public static string PreviewFile(string projectFolder, MediaAsset asset) =>
        asset.Kind == MediaKind.Video && Exists(projectFolder, asset)
            ? ProjectPaths.ProxyFile(projectFolder, asset)
            : ProjectPaths.AssetFile(projectFolder, asset);

    /// <exception cref="InvalidDataException">영상이 아니거나 읽을 수 없을 때.</exception>
    /// <exception cref="OperationCanceledException">취소했을 때. 만들던 파일은 남지 않는다.</exception>
    // shortcut: 원본을 그 클립 하나만 있는 시퀀스로 내보내는 방식이라 프레임레이트가 30 또는 60으로 맞춰진다.
    // 24fps 원본은 프록시에서 한 프레임까지 어긋나 보일 수 있다. 거슬리면 원본의 프레임을 그대로 옮기는 변환기를 따로 만든다.
    public static void Build(string projectFolder, MediaAsset asset, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var info = MediaProbe.Probe(ProjectPaths.AssetFile(projectFolder, asset));
        if (asset.Kind != MediaKind.Video || info.Video is not { Width: > 0, Height: > 0 } video)
            throw new InvalidDataException($"프록시를 만들 수 없는 원본입니다 ({asset.Name})");

        var height = Math.Min(MaxHeight, video.Height) / 2 * 2;
        var width = Math.Max(2, (int)Math.Round(video.Width * (double)height / video.Height / 2) * 2);

        var project = new Project();
        project.Assets.Add(asset);
        project.Sequence.Preset = new SequencePreset("프록시", width, height);
        project.Sequence.FrameRate = video.FrameRate > 45 ? 60 : 30;
        project.Sequence.AddTrack<VideoTrack>().Clips.Add(
            new VideoClip { AssetId = asset.Id, Range = new TimeRange(TimeSpan.Zero, info.Duration) });

        // 다 만든 뒤에 제자리로 옮긴다. 만들다 만 프록시가 쓰이지 않게.
        var target = ProjectPaths.ProxyFile(projectFolder, asset);
        var temp = target + ".tmp.mp4";
        new Mp4Exporter().Export(project, projectFolder,
            new ExportSettings(temp, ExportSettings.BitrateFor(ExportQuality.Low, project.Sequence)), progress, cancellation);
        File.Move(temp, target, overwrite: true);
    }
}
