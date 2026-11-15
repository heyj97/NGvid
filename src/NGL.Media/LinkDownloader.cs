using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace NGL.Media;

/// <summary>yt-dlp로 링크의 영상을 받는다. yt-dlp와 ffmpeg는 처음 쓸 때 앱 데이터 폴더로 받아 둔다.</summary>
public static class LinkDownloader
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly string Tools = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NGvid", "tools");
    private static readonly string YtDlp = Path.Combine(Tools, "yt-dlp.exe");
    private static readonly string Ffmpeg = Path.Combine(Tools, "ffmpeg.exe");
    private static readonly HttpClient Http = new();

    /// <summary>http(s) 주소만 받는다. "-"로 시작하는 글자가 yt-dlp의 옵션으로 읽히는 것도 막는다.</summary>
    public static bool IsLink(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>영상을 folder에 mp4로 받고 그 파일의 경로를 돌려준다.</summary>
    /// <exception cref="InvalidOperationException">yt-dlp가 영상을 받지 못했을 때.</exception>
    public static async Task<string> DownloadAsync(string link, string folder, IProgress<string> status, CancellationToken cancel)
    {
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(folder);
        if (!File.Exists(YtDlp)) await FetchAsync(YtDlpUrl, YtDlp, "yt-dlp", status, cancel);
        if (!File.Exists(Ffmpeg))
        {
            // 고화질은 영상과 소리가 따로 와서 합칠 ffmpeg.exe가 필요하다.
            var zip = Path.Combine(Tools, "ffmpeg.zip");
            await FetchAsync(FfmpegUrl, zip, "ffmpeg", status, cancel);
            using (var archive = ZipFile.OpenRead(zip))
                archive.Entries.First(entry => entry.Name == "ffmpeg.exe").ExtractToFile(Ffmpeg + ".part", overwrite: true);
            File.Move(Ffmpeg + ".part", Ffmpeg, overwrite: true);
            File.Delete(zip);
        }

        string[] arguments =
        [
            "--no-playlist", "--newline", "--encoding", "utf-8", "--windows-filenames",
            "--ffmpeg-location", Tools,
            "-S", "vcodec:h264,res,acodec:m4a", "--merge-output-format", "mp4", // 어디서나 풀리는 H.264를 먼저 고른다
            "-P", folder, "-o", "%(title).80s.%(ext)s",
            "--progress", "--progress-template", "download:PROGRESS %(progress._percent_str)s %(info.vcodec)s %(info.acodec)s",
            "--print", "after_move:filepath",
            "--", link,
        ];

        status.Report("영상 받는 중…");
        var (ok, output) = await RunAsync(arguments, status, cancel);
        if (!ok)
        {
            // 사이트가 바뀌어 낡은 yt-dlp가 실패했을 수 있다. 최신으로 바꾸고 한 번만 다시 해 본다.
            status.Report("yt-dlp를 최신으로 바꾸는 중…");
            await RunAsync(["-U"], status, cancel);
            status.Report("영상 받는 중…");
            (ok, output) = await RunAsync(arguments, status, cancel);
        }

        return ok && File.Exists(output) ? output : throw new InvalidOperationException(output);
    }

    private static async Task FetchAsync(string url, string target, string label, IProgress<string> status, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        // 받다가 끊긴 파일이 다음에 온전한 것으로 보이지 않게 다 받은 뒤 이름을 바꾼다.
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var file = File.Create(target + ".part"))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            var shown = -1L;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0 && done * 100 / total is var percent && percent != shown)
                    status.Report($"{label} 준비 중… {shown = percent}%");
            }
        }

        File.Move(target + ".part", target, overwrite: true);
    }

    /// <summary>yt-dlp를 돌린다. 성공하면 마지막 출력 줄(받은 파일 경로)을, 실패하면 오류 줄을 돌려준다.</summary>
    private static async Task<(bool Ok, string Output)> RunAsync(string[] arguments, IProgress<string> status, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = "";
        var error = "알 수 없는 오류";
        var split = false; // 영상과 소리가 따로 온다
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            if (!e.Data.StartsWith("PROGRESS"))
            {
                output = e.Data.Trim();
                return;
            }

            // "PROGRESS 12.3% avc1.4D400C none" (퍼센트, 영상 코덱, 소리 코덱)
            var parts = e.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !double.TryParse(parts[1].TrimEnd('%'), CultureInfo.InvariantCulture, out var percent)) return;

            // 따로 오면 막대가 두 번 차지 않게 영상을 0~90%, 소리를 90~100%로 잇는다. 소리는 보통 전체의 1/10쯤이다.
            if (parts[3] == "none")
            {
                split = true;
                percent *= 0.9;
            }
            else if (split && parts[2] == "none")
            {
                percent = 90 + percent * 0.1;
            }

            status.Report($"영상 받는 중… {percent:0}%");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data?.StartsWith("ERROR") == true) error = e.Data;
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode == 0 ? (true, output) : (false, error);
    }
}
