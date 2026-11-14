using NGL.Core;

namespace NGL.Media;

/// <summary>미디어 파일을 프로젝트의 media 폴더로 복사하고 보관함에 올릴 원본 정보를 만든다.</summary>
public static class MediaImporter
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <exception cref="InvalidDataException">영상·소리·이미지 파일이 아닐 때. 이때는 아무것도 복사하지 않는다.</exception>
    public static MediaAsset Import(string projectFolder, string sourcePath)
    {
        var info = MediaProbe.Probe(sourcePath);
        var isImage = ImageExtensions.Contains(Path.GetExtension(sourcePath).ToLowerInvariant());
        var kind = info.Video is not null ? (isImage ? MediaKind.Image : MediaKind.Video)
            : info.Audio is not null ? MediaKind.Audio
            : throw new InvalidDataException($"영상·소리·이미지 파일이 아닙니다 ({sourcePath})");

        var mediaFolder = ProjectPaths.MediaFolder(projectFolder);
        Directory.CreateDirectory(mediaFolder);

        // 이미 media 폴더에 있는 파일은 그대로 쓴다.
        var target = Path.GetFullPath(sourcePath);
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(mediaFolder), StringComparison.OrdinalIgnoreCase))
        {
            target = UniquePath(mediaFolder, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, target);
        }

        return new MediaAsset
        {
            Kind = kind,
            Name = Path.GetFileNameWithoutExtension(target),
            RelativePath = $"{ProjectPaths.MediaFolderName}/{Path.GetFileName(target)}",
            Duration = kind == MediaKind.Image ? TimeSpan.Zero : info.Duration,
            HasAudio = kind == MediaKind.Video && info.Audio is not null,
            Width = info.Video?.Width ?? 0,
            Height = info.Video?.Height ?? 0,
        };
    }

    /// <summary>같은 이름이 있으면 "이름 (2).mp4"처럼 번호를 붙인다.</summary>
    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        for (var number = 2; File.Exists(path); number++)
        {
            path = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(fileName)} ({number}){Path.GetExtension(fileName)}");
        }

        return path;
    }
}

/// <summary>프로젝트의 원본 파일이 사라졌는지 확인하고 새 파일로 다시 연결한다.</summary>
public static class MediaRelinker
{
    public static IReadOnlyList<MediaAsset> FindMissing(Project project, string projectFolder) =>
        project.Assets.Where(asset => !File.Exists(ProjectPaths.AssetFile(projectFolder, asset))).ToList();

    /// <summary>새 파일을 원본의 원래 자리로 복사한다. 클립은 손대지 않아도 다시 보인다.</summary>
    /// <exception cref="InvalidDataException">미디어 파일이 아닐 때.</exception>
    public static void Relink(string projectFolder, MediaAsset asset, string newPath)
    {
        MediaProbe.Probe(newPath);
        var target = ProjectPaths.AssetFile(projectFolder, asset);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(newPath, target, overwrite: true);
    }

    /// <summary>폴더(하위 폴더 포함)에서 사라진 원본과 같은 이름의 파일을 찾아 다시 연결한다. 연결한 개수를 돌려준다.</summary>
    public static int RelinkFromFolder(Project project, string projectFolder, string searchFolder)
    {
        var count = 0;
        if (!Directory.Exists(searchFolder)) return count;

        foreach (var asset in FindMissing(project, projectFolder))
        {
            var name = Path.GetFileName(asset.RelativePath);
            var found = Directory.EnumerateFiles(searchFolder, name, SearchOption.AllDirectories).FirstOrDefault();
            if (found is null) continue;

            try
            {
                Relink(projectFolder, asset, found);
                count++;
            }
            catch (InvalidDataException)
            {
                // 이름만 같고 미디어가 아닌 파일은 건너뛴다.
            }
        }

        return count;
    }
}
