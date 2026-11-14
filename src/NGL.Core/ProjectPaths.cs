namespace NGL.Core;

/// <summary>프로젝트 폴더 안의 파일 위치 규칙.</summary>
public static class ProjectPaths
{
    public const string ProjectFileName = "project.ngvid";
    public const string MediaFolderName = "media";

    public static string ProjectFile(string projectFolder) => Path.Combine(projectFolder, ProjectFileName);

    public static string MediaFolder(string projectFolder) => Path.Combine(projectFolder, MediaFolderName);

    /// <summary>원본 파일의 전체 경로.</summary>
    public static string AssetFile(string projectFolder, MediaAsset asset) =>
        Path.GetFullPath(Path.Combine(projectFolder, asset.RelativePath));
}
