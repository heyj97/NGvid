using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NGL.Core;

namespace NGL.IO;

/// <summary>.ngvid와 설정 파일이 함께 쓰는 JSON 규칙.</summary>
internal static class NgvidJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 한글을 \uXXXX로 바꾸지 않고 그대로 적는다.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 손으로 고친 파일에서 "type"이 맨 앞에 없어도 읽는다.
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { PopulateReadOnlyObjects, AddDerivedTypes } },
    };

    /// <summary>Sequence.Tracks처럼 교체할 수 없는(set이 없는) 객체·목록은 기존 객체에 채워 넣는다.</summary>
    private static void PopulateReadOnlyObjects(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (property.Set is null && !property.PropertyType.IsValueType && property.PropertyType != typeof(string))
                property.ObjectCreationHandling = JsonObjectCreationHandling.Populate;
        }
    }

    /// <summary>
    /// Track과 Clip은 "type": "VideoClip"처럼 클래스 이름으로 종류를 구분한다.
    /// Core에 새 종류의 클래스를 추가하면 자동으로 저장·읽기 대상이 된다.
    /// </summary>
    private static void AddDerivedTypes(JsonTypeInfo info)
    {
        if (info.Type != typeof(Track) && info.Type != typeof(Clip)) return;

        info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "type" };
        foreach (var type in info.Type.Assembly.GetTypes())
        {
            if (!type.IsAbstract && type.IsSubclassOf(info.Type))
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
        }
    }

    /// <summary>임시 파일에 먼저 쓰고 바꿔치기한다. 쓰는 도중 꺼져도 기존 파일이 깨지지 않는다.</summary>
    public static void WriteFileSafely(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
