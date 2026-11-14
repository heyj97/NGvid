namespace NGL.Core;

/// <summary>프로젝트 폴더 안의 파일 위치 규칙.</summary>
public static class ProjectPaths
{
    public const string ProjectFileName = "project.ngvid";
    public const string MediaFolderName = "media";

    public static string ProjectFile(string projectFolder) => Path.Combine(projectFolder, ProjectFileName);

    public static string MediaFolder(string projectFolder) => Path.Combine(projectFolder, MediaFolderName);

    /// <summary>자동 백업이 쌓이는 폴더.</summary>
    public static string BackupFolder(string projectFolder) => Path.Combine(projectFolder, "backups");

    /// <summary>원본 파일의 전체 경로.</summary>
    public static string AssetFile(string projectFolder, MediaAsset asset) =>
        Path.GetFullPath(Path.Combine(projectFolder, asset.RelativePath));

    /// <summary>영상 원본의 프록시(미리보기용 작은 사본) 경로. 지워도 다시 만들 수 있다.</summary>
    public static string ProxyFile(string projectFolder, MediaAsset asset) =>
        Path.Combine(projectFolder, "proxy", asset.Id + ".mp4");
}
