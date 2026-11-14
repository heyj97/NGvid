using System.Text.Json;
using NGL.Core;

namespace NGL.IO;

/// <summary>프로그램 설정을 JSON 파일로 저장하고 읽는다.</summary>
public static class AppSettingsStore
{
    /// <summary>%AppData%\NGvid\settings.json</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NGvid", "settings.json");

    /// <summary>파일이 없거나 읽을 수 없으면 기본 설정을 돌려준다.</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            var json = File.ReadAllText(path ?? DefaultPath);
            return JsonSerializer.Deserialize<AppSettings>(json, NgvidJson.Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null) =>
        NgvidJson.WriteFileSafely(path ?? DefaultPath, JsonSerializer.Serialize(settings, NgvidJson.Options));
}
