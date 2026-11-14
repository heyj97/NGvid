using System.Text.Json;
using NGL.Core;

namespace NGL.IO;

/// <summary>애니메이션 프리셋을 JSON 파일로 저장하고 읽는다. 프로젝트와 무관하게 프로그램에 남는다.</summary>
public static class AnimationPresetStore
{
    /// <summary>%AppData%\NGvid\animation-presets.json</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NGvid", "animation-presets.json");

    private static TimeSpan Half => TimeSpan.FromSeconds(0.5);

    /// <summary>처음 쓸 때 들어 있는 프리셋. 모두 클립의 처음 0.5초 동안 등장하는 움직임이다.</summary>
    public static List<AnimationPreset> Defaults() =>
    [
        new("페이드 인", new()
        {
            [AnimatedProperty.Opacity] = [new(TimeSpan.Zero, 0), new(Half, 1)],
        }),
        new("확대 등장", new()
        {
            [AnimatedProperty.Scale] = [new(TimeSpan.Zero, 0.5, Easing.EaseOut), new(Half, 1)],
            [AnimatedProperty.Opacity] = [new(TimeSpan.Zero, 0), new(Half, 1)],
        }),
        new("왼쪽에서 등장", new()
        {
            [AnimatedProperty.X] = [new(TimeSpan.Zero, -600, Easing.EaseOut), new(Half, 0)],
        }),
    ];

    /// <summary>파일이 없거나 읽을 수 없으면 기본 프리셋을 돌려준다.</summary>
    public static List<AnimationPreset> Load(string? path = null)
    {
        try
        {
            var json = File.ReadAllText(path ?? DefaultPath);
            return JsonSerializer.Deserialize<List<AnimationPreset>>(json, NgvidJson.Options) ?? Defaults();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Defaults();
        }
    }

    public static void Save(List<AnimationPreset> presets, string? path = null) =>
        NgvidJson.WriteFileSafely(path ?? DefaultPath, JsonSerializer.Serialize(presets, NgvidJson.Options));
}
