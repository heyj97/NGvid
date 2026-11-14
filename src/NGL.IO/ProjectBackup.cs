using NGL.Core;

namespace NGL.IO;

/// <summary>프로젝트 파일의 사본을 backups 폴더에 쌓아 두고 되돌린다. 미디어 파일은 백업하지 않는다.</summary>
public static class ProjectBackup
{
    private const string Pattern = "project-*.ngvid";

    /// <summary>백업 파일들. 새것부터.</summary>
    public static IReadOnlyList<string> List(string projectFolder)
    {
        var folder = ProjectPaths.BackupFolder(projectFolder);
        // 이름에 시각이 들어 있어서 이름순이 곧 시간순이다.
        return Directory.Exists(folder) ? Directory.GetFiles(folder, Pattern).OrderDescending().ToList() : [];
    }

    /// <summary>
    /// 지금의 프로젝트 파일을 백업한다. 가장 최근 백업과 내용이 같으면 하지 않는다.
    /// keep개를 넘는 오래된 백업은 지운다. 백업을 만들었으면 true.
    /// </summary>
    public static bool Create(string projectFolder, int keep, DateTime? now = null)
    {
        var source = ProjectPaths.ProjectFile(projectFolder);
        if (!File.Exists(source)) return false;

        var existing = List(projectFolder);
        var content = File.ReadAllBytes(source);
        if (existing.Count > 0 && File.ReadAllBytes(existing[0]).AsSpan().SequenceEqual(content)) return false;

        var folder = ProjectPaths.BackupFolder(projectFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"project-{now ?? DateTime.Now:yyyyMMdd-HHmmss-fff}.ngvid");
        File.WriteAllBytes(path, content);

        foreach (var old in List(projectFolder).Skip(Math.Max(1, keep))) File.Delete(old);
        return true;
    }

    /// <summary>백업으로 프로젝트 파일을 바꾼다. 바꾸기 전의 상태도 백업해 둔다.</summary>
    /// <exception cref="InvalidDataException">읽을 수 없는 백업일 때. 이때는 아무것도 바꾸지 않는다.</exception>
    public static void Restore(string projectFolder, string backupPath, int keep)
    {
        ProjectSerializer.Load(backupPath); // 깨진 백업으로 덮어쓰지 않게 먼저 읽어 본다
        var content = File.ReadAllText(backupPath);
        Create(projectFolder, keep + 1);
        NgvidJson.WriteFileSafely(ProjectPaths.ProjectFile(projectFolder), content);
    }

    /// <summary>백업 파일 이름에 적힌 시각. 읽을 수 없으면 파일의 수정 시각.</summary>
    public static DateTime TimeOf(string backupPath) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(backupPath)["project-".Length..], "yyyyMMdd-HHmmss-fff",
            null, System.Globalization.DateTimeStyles.None, out var time) ? time : File.GetLastWriteTime(backupPath);
}
