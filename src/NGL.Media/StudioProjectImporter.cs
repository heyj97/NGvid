using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NGL.Core;

namespace NGL.Media;

/// <summary>
/// New-Gen Lab 스튜디오(웹)에서 "프로젝트 외부 저장"으로 만든 폴더(project.json + media/숫자.bin)를 NGvid 프로젝트로 바꾼다.
/// 영상 클립, 자막, 오디오·BGM, 후킹 추천(마커)만 옮긴다. VFX 클립과 자막 효과는 옮기지 않는다.
/// </summary>
public static partial class StudioProjectImporter
{
    private const string Format = "new-gen-lab-project";

    // 스튜디오가 화자 번호에 입히는 자막 색 (new-gen-lab-studio의 src/studio/captions/styles.js와 같은 순서).
    private static readonly string[] SpeakerColors = ["#D6F676", "#AAADFF", "#FFAD8D", "#7FD6E8", "#F5A3D0", "#F2D06B"];

    [GeneratedRegex(@"^\d+\.bin$")]
    private static partial Regex AssetFileName();

    /// <summary>원본을 projectFolder의 media로 복사하고 프로젝트를 만든다. 저장은 하지 않는다.</summary>
    /// <returns>프로젝트와, 옮기지 못하고 건너뛴 클립·자막의 수.</returns>
    /// <exception cref="InvalidDataException">스튜디오의 프로젝트 폴더가 아니거나 내용이 잘못됐을 때.</exception>
    public static (Project Project, int Skipped) Import(string studioFolder, string projectFolder)
    {
        try
        {
            var manifestPath = Path.Combine(studioFolder, "project.json");
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("New-Gen Lab 프로젝트 폴더가 아닙니다. (project.json이 없습니다)");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            if (manifest?["project"] is not JsonObject source || (string?)manifest["format"] != Format || (int?)manifest["version"] != 1)
                throw new InvalidDataException("지원하는 New-Gen Lab 프로젝트 폴더가 아닙니다.");

            var project = new Project { Name = (string?)source["name"] ?? "" };
            var sequence = project.Sequence;
            sequence.Preset = SequencePreset.Portrait;
            sequence.FrameRate = 30;
            var skipped = 0;

            TimeRange Range(double start, double end)
            {
                var from = sequence.SnapToFrame(TimeSpan.FromSeconds(Math.Max(0, start)));
                return new TimeRange(from, sequence.SnapToFrame(TimeSpan.FromSeconds(end)) - from);
            }

            var assets = ImportAssets(manifest, source, studioFolder, projectFolder);
            project.Assets.AddRange(assets.Values);

            // 스튜디오의 트랙. 저장된 목록이 없으면 영상 1개이고, 자막 트랙은 목록에 없어도 있는 것으로 본다.
            var tracks = Items(source, "videoTracks").ToList();
            if (tracks.Count == 0) tracks.Add(new JsonObject { ["id"] = "main", ["name"] = "영상 1" });
            if (!tracks.Any(track => Text(track, "id") == "captions"))
                tracks.Add(new JsonObject { ["id"] = "captions", ["name"] = "자막", ["type"] = "caption" });

            var textTracks = new List<Track>();
            var videoTracks = new List<Track>();
            var soundTracks = new List<Track>();
            var audioTracks = new List<Track>();
            var ducking = new List<AudioTrack>();
            AudioTrack? narration = null;
            var placed = new List<(JsonObject Scene, VideoClip Clip)>();

            foreach (var track in tracks)
            {
                var id = Text(track, "id");
                var name = Text(track, "name") ?? "";
                switch (Text(track, "type") ?? "video")
                {
                    case "video":
                    {
                        var video = new VideoTrack { Name = name };
                        AudioTrack? sound = null;
                        foreach (var scene in Items(source, "scenes"))
                        {
                            if ((Text(scene, "trackId") ?? "main") != id || (bool?)scene["removed"] == true) continue;
                            var range = Range(Number(scene, "timelineStart"),
                                Number(scene, "timelineStart") + Number(scene, "end") - Number(scene, "start"));
                            if (Text(scene, "asset") is not { } assetId || !assets.TryGetValue(assetId, out var asset) ||
                                asset.Kind != MediaKind.Video || range.Duration <= TimeSpan.Zero)
                            {
                                skipped++;
                                continue;
                            }

                            var clip = new VideoClip
                            {
                                AssetId = asset.Id,
                                Range = range,
                                SourceStart = TimeSpan.FromSeconds(Number(scene, "start")),
                                X = Number(scene, "videoX") / 100 * sequence.Preset.Width,
                                Y = Number(scene, "videoY") / 100 * sequence.Preset.Height,
                                Scale = Number(scene, "videoScale", 100) / 100,
                            };
                            video.Clips.Add(clip);
                            placed.Add((scene, clip));
                            if (!asset.HasAudio) continue;

                            // NGvid는 영상의 소리를 연결된 오디오 클립으로 따로 둔다.
                            clip.LinkId = Guid.NewGuid();
                            sound ??= new AudioTrack { Name = name + " 소리" };
                            sound.Clips.Add(new AudioClip
                            {
                                AssetId = asset.Id, Range = range, SourceStart = clip.SourceStart,
                                LinkId = clip.LinkId, Volume = Number(scene, "volume", 1),
                            });
                        }

                        video.Clips.SortByStart();
                        videoTracks.Add(video);
                        if (sound is not null)
                        {
                            sound.Clips.SortByStart();
                            soundTracks.Add(sound);
                        }

                        break;
                    }
                    case "caption":
                    {
                        var captions = new VideoTrack { Name = name };
                        foreach (var cue in Items(source, "captions").OrderBy(cue => Number(cue, "start")))
                        {
                            if ((Text(cue, "trackId") ?? "captions") != id || string.IsNullOrWhiteSpace(Text(cue, "caption"))) continue;
                            // 시간이 겹치는 자막은 한 트랙에 놓을 수 없어 건너뛴다.
                            if (sequence.AddSubtitle(captions, Range(Number(cue, "start"), Number(cue, "end")), Text(cue, "caption")!) is not { } clip)
                            {
                                skipped++;
                                continue;
                            }

                            var speaker = (int)Number(cue, "speaker");
                            clip.Style.Color = SpeakerColors[(speaker % SpeakerColors.Length + SpeakerColors.Length) % SpeakerColors.Length];
                        }

                        if (captions.Clips.Count > 0) textTracks.Add(captions);
                        break;
                    }
                    case "audio" or "bgm":
                    {
                        var audio = new AudioTrack { Name = name };
                        var ducks = false;
                        foreach (var item in Items(source, "audioClips"))
                        {
                            if (Text(item, "trackId") != id) continue;
                            var start = Number(item, "timelineStart");
                            var range = Range(start, start + Number(item, "duration"));
                            if (Text(item, "asset") is not { } assetId || !assets.TryGetValue(assetId, out var asset) ||
                                !(asset.Kind == MediaKind.Audio || asset.HasAudio) || range.Duration <= TimeSpan.Zero)
                            {
                                skipped++;
                                continue;
                            }

                            audio.Clips.Add(new AudioClip
                            {
                                AssetId = asset.Id, Range = range,
                                SourceStart = TimeSpan.FromSeconds(Number(item, "sourceStart")),
                                Volume = Number(item, "volume", 1),
                                FadeIn = TimeSpan.FromSeconds(Number(item, "fadeIn")),
                                FadeOut = TimeSpan.FromSeconds(Number(item, "fadeOut")),
                            });
                            ducks |= (bool?)item["duck"] == true;
                        }

                        audio.Clips.SortByStart();
                        audioTracks.Add(audio);
                        if (Text(track, "type") == "audio") narration ??= audio;
                        // 스튜디오의 자동 감쇠는 BGM 클립마다 켜지만 NGvid는 트랙 단위다. 하나라도 켜져 있으면 트랙에 건다.
                        else if (ducks) ducking.Add(audio);
                        break;
                    }
                }
            }

            foreach (var track in ducking) track.DuckUnder = narration?.Id;

            // NGvid는 첫 트랙이 화면 맨 위다. 스튜디오는 자막이 항상 위이고 영상은 아래쪽 트랙이 위에 그려진다.
            videoTracks.Reverse();
            sequence.Tracks.AddRange([.. textTracks, .. videoTracks, .. soundTracks, .. audioTracks]);

            // 후킹 추천은 원본 기준 시각이다. 그 구간이 타임라인에 놓여 있을 때만 마커로 옮긴다.
            foreach (var hook in Items(source, "hooks"))
            {
                var time = Number(hook, "time");
                var hit = placed.FirstOrDefault(each => Text(each.Scene, "asset") == Text(hook, "asset") &&
                    time >= Number(each.Scene, "start") && time < Number(each.Scene, "end"));
                if (hit.Clip is null) continue;
                sequence.Markers.Add(new Marker
                {
                    Time = sequence.SnapToFrame(hit.Clip.Range.Start + TimeSpan.FromSeconds(time - Number(hit.Scene, "start"))),
                    Name = $"★{(int)Number(hook, "rank")} {Text(hook, "reason")}".Trim(),
                });
            }

            return (project, skipped);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("New-Gen Lab 프로젝트를 읽을 수 없습니다: " + e.Message, e);
        }
    }

    /// <summary>media/숫자.bin을 원래 파일명으로 복사하고, 스튜디오의 미디어 ID로 찾을 수 있게 돌려준다. 읽을 수 없는 파일은 뺀다.</summary>
    private static Dictionary<string, MediaAsset> ImportAssets(JsonObject manifest, JsonObject source, string studioFolder, string projectFolder)
    {
        // 원래 파일명은 그 미디어를 쓰는 보관함 항목이나 클립에 적혀 있다.
        var names = new Dictionary<string, string>();
        foreach (var key in new[] { "sceneLibrary", "soundLibrary", "scenes", "audioClips" })
        {
            foreach (var item in Items(source, key))
            {
                if (Text(item, "asset") is { } id && Text(item, "filename") is { Length: > 0 } name) names.TryAdd(id, name);
            }
        }

        var media = ProjectPaths.MediaFolder(projectFolder);
        Directory.CreateDirectory(media);
        var assets = new Dictionary<string, MediaAsset>();
        foreach (var entry in Items(manifest, "assets"))
        {
            // file은 밖에서 온 값이라 media 폴더를 벗어나지 못하게 모양을 확인한다.
            if (Text(entry, "id") is not { } id || Text(entry, "file") is not { } file || !AssetFileName().IsMatch(file) || assets.ContainsKey(id))
                throw new InvalidDataException("프로젝트 미디어 목록이 올바르지 않습니다.");

            var name = string.Concat(Path.GetFileName(names.GetValueOrDefault(id, id)).Split(Path.GetInvalidFileNameChars()));
            if (name.Length == 0) name = id;
            if (!Path.HasExtension(name)) name += Extension(Text(entry, "type"));
            var target = Path.Combine(media, name);
            for (var number = 2; File.Exists(target); number++)
                target = Path.Combine(media, $"{Path.GetFileNameWithoutExtension(name)} ({number}){Path.GetExtension(name)}");

            File.Copy(Path.Combine(studioFolder, "media", file), target);
            try
            {
                assets[id] = MediaImporter.Import(projectFolder, target);
            }
            catch (InvalidDataException)
            {
                File.Delete(target); // 미디어가 아닌 파일은 버린다. 그 파일을 쓰는 클립은 건너뛴 수에 들어간다.
            }
        }

        return assets;
    }

    /// <summary>MIME 타입으로 확장자를 정한다. 이미지인지는 확장자로 가리기 때문에 필요하다.</summary>
    private static string Extension(string? mime) => (mime?.Split('/', ';') is [_, var kind, ..] ? kind.Trim().ToLowerInvariant() : "") switch
    {
        "" => "",
        "mpeg" => mime!.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".mpg",
        "jpeg" => ".jpg",
        "quicktime" => ".mov",
        "x-m4a" => ".m4a",
        var other => "." + string.Concat(other.Where(char.IsLetterOrDigit)),
    };

    private static IEnumerable<JsonObject> Items(JsonObject parent, string key) =>
        parent[key] is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string? Text(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double Number(JsonObject item, string key, double fallback = 0) =>
        item[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : fallback;
}
