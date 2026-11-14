using System.Text.Json;
using System.Text.Json.Nodes;
using NGL.Core;

namespace NGL.IO;

/// <summary>
/// 시퀀스의 상태를 통째로 떠 두었다가 되돌린다. 실행 취소와 클립 복사에 쓴다.
/// .ngvid와 같은 방식으로 저장하므로 클립에 새 속성이 생겨도 따로 손댈 것이 없다.
/// </summary>
public static class SequenceSnapshot
{
    public static string Take(Sequence sequence) => JsonSerializer.Serialize(sequence, NgvidJson.Options);

    /// <summary>
    /// 시퀀스를 떠 둔 상태로 되돌린다. 시퀀스 객체는 그대로이고 그 안의 트랙·클립·마커는 새 객체로 바뀐다
    /// (ID는 같다).
    /// </summary>
    public static void Restore(Sequence sequence, string snapshot)
    {
        var saved = JsonSerializer.Deserialize<Sequence>(snapshot, NgvidJson.Options)!;
        sequence.Preset = saved.Preset;
        sequence.FrameRate = saved.FrameRate;
        sequence.Tracks.Clear();
        sequence.Tracks.AddRange(saved.Tracks);
        sequence.Markers.Clear();
        sequence.Markers.AddRange(saved.Markers);
    }

    /// <summary>클립 하나를 글로 떠 둔다. Create로 같은 내용의 새 클립을 만들 수 있다.</summary>
    public static string Take(Clip clip) => JsonSerializer.Serialize(clip, NgvidJson.Options);

    /// <summary>떠 둔 클립과 내용이 같고 ID만 새로운 클립을 만든다.</summary>
    public static Clip Create(string clipSnapshot)
    {
        var node = JsonNode.Parse(clipSnapshot)!.AsObject();
        node.Remove("id"); // ID가 없으면 새로 만들어진다.
        return node.Deserialize<Clip>(NgvidJson.Options)!;
    }

    /// <summary>내용이 같고 ID만 새로운 클립.</summary>
    public static Clip Clone(Clip clip) => Create(Take(clip));
}
