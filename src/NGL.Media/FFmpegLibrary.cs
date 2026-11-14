using FFmpeg.AutoGen;

namespace NGL.Media;

/// <summary>FFmpeg 공유 DLL을 찾아 불러온다. FFmpeg를 쓰는 모든 코드가 먼저 호출한다.</summary>
public static class FFmpegLibrary
{
    private const string ProbeFile = "avcodec-63.dll";
    private static readonly object Gate = new();
    private static bool _loaded;

    /// <exception cref="DllNotFoundException">FFmpeg DLL을 찾지 못했을 때.</exception>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;

            var folder = FindFolder() ?? throw new DllNotFoundException(
                "FFmpeg 9.0.x DLL을 찾을 수 없습니다. scripts/install-ffmpeg.ps1로 ffmpeg 폴더에 배치해 주세요.");

            ffmpeg.RootPath = folder;
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _loaded = true;
        }
    }

    /// <summary>
    /// 실행 파일 옆의 ffmpeg 폴더를 먼저 보고, 없으면 상위 폴더로 올라가며 찾는다.
    /// (개발 중에는 저장소 루트의 ffmpeg 폴더를 쓴다.)
    /// </summary>
    private static string? FindFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ffmpeg");
            if (File.Exists(Path.Combine(candidate, ProbeFile))) return candidate;
        }

        return null;
    }
}

internal static unsafe class FFmpegError
{
    /// <summary>FFmpeg 함수의 반환값이 오류(음수)면 설명을 붙여 예외를 던진다.</summary>
    public static int ThrowIfFailed(int result, string action)
    {
        if (result >= 0) return result;

        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(result, buffer, size);
        var reason = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer);
        throw new InvalidDataException($"{action}: {reason} ({result})");
    }

    public static TimeSpan ToTimeSpan(long value, AVRational timeBase) =>
        new(ffmpeg.av_rescale_q(value, timeBase, TickBase));

    public static long FromTimeSpan(TimeSpan time, AVRational timeBase) =>
        ffmpeg.av_rescale_q(time.Ticks, TickBase, timeBase);

    private static readonly AVRational TickBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
}
