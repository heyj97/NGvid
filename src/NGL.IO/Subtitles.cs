using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NGL.Core;

namespace NGL.IO;

/// <summary>자막 한 줄: 보이는 구간과 문구.</summary>
public sealed record Cue(TimeRange Range, string Text);

/// <summary>SRT·VTT 자막 파일을 읽고 SRT로 쓴다.</summary>
public static partial class Subtitles
{
    // "00:01:02,500 --> 00:01:04.000" 또는 VTT의 "01:02.500 --> 01:04.000" (뒤에 위치 설정이 붙을 수 있다)
    [GeneratedRegex(@"^\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    /// <summary>확장자(.srt, .vtt)와 무관하게 읽는다. 두 형식은 시간 줄의 모양이 같다.</summary>
    public static List<Cue> Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// 시간 줄을 찾고 그 아래 빈 줄 전까지를 문구로 읽는다. 번호 줄, WEBVTT 머리말, 주석은 건너뛴다.
    /// 끝이 시작보다 앞서는 자막은 버린다.
    /// </summary>
    public static List<Cue> Parse(string content)
    {
        var cues = new List<Cue>();
        var lines = content.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimeLine().Match(lines[i]);
            if (!match.Success) continue;

            var start = Time(match, 1);
            var end = Time(match, 5);
            var text = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) text.Add(Tag().Replace(lines[i], "").Trim());

            if (end > start && text.Count > 0) cues.Add(new Cue(new TimeRange(start, end - start), string.Join("\n", text)));
        }

        return cues;
    }

    private static TimeSpan Time(Match match, int group)
    {
        int Number(int index) => match.Groups[index].Success ? int.Parse(match.Groups[index].Value, CultureInfo.InvariantCulture) : 0;
        // "5"는 0.5초, "50"은 0.05초가 아니라 자릿수를 채워 읽는다: ".5" = 500ms
        var milliseconds = int.Parse(match.Groups[group + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return new TimeSpan(0, Number(group), Number(group + 1), Number(group + 2), milliseconds);
    }

    /// <summary>자막들을 SRT 형식의 글로 만든다. 시작 시각 순으로 번호를 붙인다.</summary>
    public static string ToSrt(IEnumerable<Cue> cues)
    {
        var builder = new StringBuilder();
        var number = 1;
        foreach (var cue in cues.OrderBy(cue => cue.Range.Start))
        {
            builder.Append(number++).Append('\n')
                .Append(Stamp(cue.Range.Start)).Append(" --> ").Append(Stamp(cue.Range.End)).Append('\n')
                .Append(cue.Text.Replace("\r", "")).Append("\n\n");
        }

        return builder.ToString();
    }

    private static string Stamp(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");

    /// <summary>SRT 파일로 저장한다. (UTF-8, BOM 포함: 일부 플레이어가 BOM이 없으면 한글을 깨뜨린다.)</summary>
    public static void SaveSrt(string path, IEnumerable<Cue> cues) =>
        File.WriteAllText(path, ToSrt(cues), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}

/// <summary>프리셋 목록을 %AppData%\NGvid 아래의 JSON 파일로 저장하고 읽는다.</summary>
public static class PresetFile
{
    public static string PathOf(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NGvid", fileName);

    /// <summary>파일이 없거나 읽을 수 없으면 defaults를 돌려준다.</summary>
    public static List<T> Load<T>(string path, Func<List<T>> defaults)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), NgvidJson.Options) ?? defaults();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return defaults();
        }
    }

    public static void Save<T>(string path, List<T> presets) =>
        NgvidJson.WriteFileSafely(path, System.Text.Json.JsonSerializer.Serialize(presets, NgvidJson.Options));
}
