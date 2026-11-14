using System.Collections.Concurrent;
using System.Globalization;
using NGL.Core;

namespace NGL.IO;

/// <summary>.cube 형식의 3D LUT 파일을 읽는다.</summary>
public static class CubeLutParser
{
    public const string FolderName = "luts";

    private static readonly ConcurrentDictionary<string, (DateTime Written, CubeLut? Lut)> Cache = [];

    /// <summary>
    /// "LUT_3D_SIZE n" 줄과 그 뒤의 n³개 "R G B" 줄을 읽는다. 제목·주석·범위 줄은 건너뛴다.
    /// </summary>
    /// <exception cref="InvalidDataException">3D LUT가 아니거나 색의 개수가 맞지 않을 때.</exception>
    public static CubeLut Parse(string content)
    {
        var size = 0;
        var values = new List<float>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "LUT_3D_SIZE" && parts.Length == 2 && int.TryParse(parts[1], out var parsed)) size = parsed;
            else if (parts.Length == 3 && parts.All(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                values.AddRange(parts.Select(part => float.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture)));
        }

        if (size is < 2 or > 256) throw new InvalidDataException("3D LUT(.cube) 파일이 아닙니다.");
        if (values.Count != size * size * size * 3)
            throw new InvalidDataException($"LUT의 색 개수가 맞지 않습니다. ({size}³ = {size * size * size}개여야 하는데 {values.Count / 3}개)");
        return new CubeLut(size, values.ToArray());
    }

    /// <summary>파일을 읽어 둔다. 파일이 바뀌면 다시 읽고, 없거나 읽을 수 없으면 null.</summary>
    public static CubeLut? Load(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var cached) && cached.Written == written) return cached.Lut;

            CubeLut? lut = null;
            try
            {
                lut = Parse(File.ReadAllText(path));
            }
            catch (InvalidDataException)
            {
                // 잘못된 파일은 LUT가 없는 것으로 다룬다.
            }

            Cache[path] = (written, lut);
            return lut;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// LUT 파일을 프로젝트의 luts 폴더로 복사하고, 클립에 적을 상대 경로(예: luts/film.cube)를 돌려준다.
    /// </summary>
    /// <exception cref="InvalidDataException">3D LUT 파일이 아닐 때. 이때는 복사하지 않는다.</exception>
    public static string Import(string projectFolder, string sourcePath)
    {
        Parse(File.ReadAllText(sourcePath));

        var folder = Path.Combine(projectFolder, FolderName);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(folder, name);
        // 같은 이름이 이미 있으면 내용이 같을 때만 그대로 쓰고, 다르면 번호를 붙인다.
        for (var number = 2; File.Exists(target) && !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(sourcePath)); number++)
        {
            name = $"{Path.GetFileNameWithoutExtension(sourcePath)} ({number}){Path.GetExtension(sourcePath)}";
            target = Path.Combine(folder, name);
        }

        if (!File.Exists(target)) File.Copy(sourcePath, target);
        return $"{FolderName}/{name}";
    }

    /// <summary>프로젝트 폴더를 기준으로 클립의 LUT 경로를 읽어 주는 함수.</summary>
    public static Func<string, CubeLut?> LoaderFor(string projectFolder) =>
        relative => Load(Path.GetFullPath(Path.Combine(projectFolder, relative)));
}
