using System.Text.Json;
using System.Text.Json.Nodes;
using NGL.Core;

namespace NGL.IO;

/// <summary>프로젝트를 .ngvid(JSON) 파일로 저장하고 읽는다.</summary>
public static class ProjectSerializer
{
    public const string FileExtension = ".ngvid";

    public static string Serialize(Project project) =>
        JsonSerializer.Serialize(project, NgvidJson.Options);

    /// <exception cref="InvalidDataException">형식이 잘못됐거나 이 프로그램보다 새 버전의 파일일 때.</exception>
    public static Project Deserialize(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("프로젝트 파일의 형식이 올바르지 않습니다.");

            var version = root["version"]?.GetValue<int>()
                ?? throw new InvalidDataException("프로젝트 파일에 version이 없습니다.");
            if (version > Project.CurrentVersion)
                throw new InvalidDataException(
                    $"이 프로젝트는 더 새로운 NGvid에서 저장됐습니다. (파일 버전 {version}, 지원 버전 {Project.CurrentVersion})");

            // 형식이 바뀌면 여기에서 version이 낮은 root를 현재 형식으로 변환한다.

            return root.Deserialize<Project>(NgvidJson.Options)
                ?? throw new InvalidDataException("프로젝트 파일이 비어 있습니다.");
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("프로젝트 파일을 읽을 수 없습니다: " + e.Message, e);
        }
    }

    public static void Save(Project project, string path) =>
        NgvidJson.WriteFileSafely(path, Serialize(project));

    public static Project Load(string path) => Deserialize(File.ReadAllText(path));
}
