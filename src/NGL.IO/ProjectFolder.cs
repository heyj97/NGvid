using NGL.Core;

namespace NGL.IO;

/// <summary>프로젝트 폴더(project.ngvid + media/)를 만들고 읽고 저장한다.</summary>
public static class ProjectFolder
{
    /// <exception cref="IOException">그 폴더에 이미 프로젝트가 있을 때.</exception>
    public static Project Create(string folder, string name, SequencePreset preset, int frameRate)
    {
        if (File.Exists(ProjectPaths.ProjectFile(folder)))
            throw new IOException($"이미 프로젝트가 있는 폴더입니다: {folder}");

        var project = new Project { Name = name };
        project.Sequence.Preset = preset;
        project.Sequence.FrameRate = frameRate;

        Directory.CreateDirectory(ProjectPaths.MediaFolder(folder));
        Save(project, folder);
        return project;
    }

    public static Project Load(string folder) => ProjectSerializer.Load(ProjectPaths.ProjectFile(folder));

    public static void Save(Project project, string folder) =>
        ProjectSerializer.Save(project, ProjectPaths.ProjectFile(folder));
}
