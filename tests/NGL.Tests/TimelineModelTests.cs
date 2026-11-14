using NGL.Core;
using NGL.IO;
using NGL.Media;
using NGL.Playback;
using NGL.Rendering;
using SkiaSharp;

namespace NGL.Tests;

public class TimelineModelTests
{
    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void 새_트랙은_번호가_붙고_자막_영상_오디오_순으로_놓인다()
    {
        var sequence = new Sequence();

        var audio1 = sequence.AddTrack<AudioTrack>();
        var video1 = sequence.AddTrack<VideoTrack>();
        var caption1 = sequence.AddTrack<CaptionTrack>();
        var video2 = sequence.AddTrack<VideoTrack>();
        var audio2 = sequence.AddTrack<AudioTrack>();

        // 새 영상 트랙은 기존 영상 트랙 위(자막 아래)에 생긴다.
        Assert.Equal([caption1, video2, video1, audio1, audio2], sequence.Tracks);
        Assert.Equal(["자막 1", "영상 2", "영상 1", "오디오 1", "오디오 2"], sequence.Tracks.Select(track => track.Name));
        Assert.Equal(["#C79A2B", "#4C8DFF", "#4C8DFF", "#3FA87A", "#3FA87A"], sequence.Tracks.Select(track => track.Color));
    }

    [Fact]
    public void 트랙을_위아래로_옮긴다()
    {
        var sequence = new Sequence();
        var a = sequence.AddTrack<VideoTrack>();
        var b = sequence.AddTrack<AudioTrack>();
        var c = sequence.AddTrack<AudioTrack>();

        sequence.MoveTrack(c, -1);
        Assert.Equal([a, c, b], sequence.Tracks);
        sequence.MoveTrack(a, 2);
        Assert.Equal([c, b, a], sequence.Tracks);

        sequence.MoveTrack(a, 1);  // 맨 아래에서 더 아래로
        sequence.MoveTrack(c, -1); // 맨 위에서 더 위로
        sequence.MoveTrack(new VideoTrack(), 1); // 시퀀스에 없는 트랙
        Assert.Equal([c, b, a], sequence.Tracks);
    }

    [Fact]
    public void 원본을_지정한_트랙과_시각에_놓고_자리가_없으면_맨_뒤에_놓는다()
    {
        var sequence = new Sequence();
        var lower = sequence.AddTrack<VideoTrack>();
        var upper = sequence.AddTrack<VideoTrack>();
        var silent = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2) };
        var withSound = new MediaAsset { Kind = MediaKind.Video, Duration = Sec(2), HasAudio = true };
        var sound = new MediaAsset { Kind = MediaKind.Audio, Duration = Sec(4) };

        // 지정한 트랙, 지정한 시각(프레임에 맞춤)
        var first = Assert.Single(sequence.Add(silent, upper, Sec(3.01)));
        Assert.Same(first, Assert.Single(upper.Clips));
        Assert.Equal(new TimeRange(Sec(3), Sec(2)), first.Range);

        // 겹치면 그 트랙의 맨 뒤로
        Assert.Equal(new TimeRange(Sec(5), Sec(2)), Assert.Single(sequence.Add(silent, upper, Sec(4))).Range);
        // 앞의 빈자리에는 들어간다
        Assert.Equal(new TimeRange(Sec(1), Sec(2)), Assert.Single(sequence.Add(silent, upper, Sec(1))).Range);
        // 음수 시각은 0초
        Assert.Equal(new TimeRange(Sec(0), Sec(2)), Assert.Single(sequence.Add(silent, lower, Sec(-5))).Range);

        // 종류가 맞지 않는 트랙을 주면 그 종류의 첫 트랙(없으면 새로)
        var audioClip = Assert.Single(sequence.Add(sound, upper, Sec(1)));
        var audioTrack = Assert.IsType<AudioTrack>(sequence.Tracks[^1]);
        Assert.Same(audioClip, Assert.Single(audioTrack.Clips));
        Assert.Equal(new TimeRange(Sec(1), Sec(4)), audioClip.Range);

        // 소리 있는 영상: 영상 자리는 비어도 오디오 자리(1~5초)가 겹치면 둘 다 맨 뒤(영상 2초, 오디오 5초 중 늦은 쪽)로
        var pair = sequence.Add(withSound, lower, Sec(4));
        Assert.All(pair, clip => Assert.Equal(new TimeRange(Sec(5), Sec(2)), clip.Range));
        Assert.Equal(2, audioTrack.Clips.Count);
    }

    [Fact]
    public void 스냅은_허용_범위_안의_가장_가까운_지점으로_붙는다()
    {
        var sequence = new Sequence();
        var track = sequence.AddTrack<VideoTrack>();
        track.Clips.Add(new VideoClip { Range = new TimeRange(Sec(2), Sec(3)) });
        sequence.Markers.Add(new Marker { Time = Sec(7), Name = "인트로 끝" });

        var points = sequence.SnapPoints().ToList();
        Assert.Equal([Sec(0), Sec(2), Sec(5), Sec(7)], points.Order());

        var tolerance = Sec(0.2);
        Assert.Equal(Sec(2), Sequence.Snap(Sec(2.15), points, tolerance));
        Assert.Equal(Sec(5), Sequence.Snap(Sec(4.9), points, tolerance));
        Assert.Equal(Sec(7), Sequence.Snap(Sec(7.2), points, tolerance)); // 경계 포함
        Assert.Equal(Sec(3.5), Sequence.Snap(Sec(3.5), points, tolerance)); // 범위 밖은 그대로
        Assert.Equal(Sec(2), Sequence.Snap(Sec(2.1), [Sec(2.3), Sec(2), Sec(1.8)], Sec(1))); // 여럿이면 가장 가까운 것
        Assert.Equal(Sec(1), Sequence.Snap(Sec(1), [], tolerance));
    }

    [Fact]
    public void 트랙_상태와_마커가_저장된다()
    {
        var project = new Project();
        var video = project.Sequence.AddTrack<VideoTrack>();
        var audio = project.Sequence.AddTrack<AudioTrack>();
        video.Locked = true;
        video.Hidden = true;
        audio.Muted = true;
        audio.Solo = true;
        project.Sequence.Markers.Add(new Marker { Time = Sec(12.5), Name = "하이라이트" });

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.Equal((true, true, false, false), (loaded.Sequence.Tracks[0].Locked, loaded.Sequence.Tracks[0].Hidden, loaded.Sequence.Tracks[0].Muted, loaded.Sequence.Tracks[0].Solo));
        Assert.Equal((false, false, true, true), (loaded.Sequence.Tracks[1].Locked, loaded.Sequence.Tracks[1].Hidden, loaded.Sequence.Tracks[1].Muted, loaded.Sequence.Tracks[1].Solo));
        var marker = Assert.Single(loaded.Sequence.Markers);
        Assert.Equal((project.Sequence.Markers[0].Id, Sec(12.5), "하이라이트"), (marker.Id, marker.Time, marker.Name));
    }

    [Fact]
    public void 숨긴_영상_트랙은_그리지_않는다()
    {
        var sequence = new Sequence();
        var bottom = sequence.AddTrack<VideoTrack>();
        var top = sequence.AddTrack<VideoTrack>();
        var topClip = new VideoClip { Range = new TimeRange(Sec(0), Sec(2)) };
        top.Clips.Add(topClip);
        bottom.Clips.Add(new VideoClip { Range = new TimeRange(Sec(0), Sec(2)) });

        
        using var redSurface = SKSurface.Create(new SKImageInfo(16, 9));
        redSurface.Canvas.Clear(SKColors.Red);
        using var redImage = redSurface.Snapshot();
        using var blueSurface = SKSurface.Create(new SKImageInfo(16, 9));
        blueSurface.Canvas.Clear(SKColors.Blue);
        using var blueImage = blueSurface.Snapshot();

        SKColor Center()
        {
            using var bitmap = new SKBitmap(new SKImageInfo(1920, 1080));
            using var canvas = new SKCanvas(bitmap);
            new SkiaCompositor().Render(canvas, sequence, Sec(1), clip => clip == topClip ? redImage : blueImage);
            return bitmap.GetPixel(960, 540);
        }

        Assert.Equal(SKColors.Red, Center());
        top.Hidden = true;
        Assert.Equal(SKColors.Blue, Center());
        bottom.Hidden = true;
        Assert.Equal(SKColors.Black, Center());
    }

    [Fact]
    public void 음소거와_솔로가_소리에_반영된다()
    {
        // 두 오디오 트랙에 같은 소리(진폭 0.5)를 놓는다.
        var sequence = new Sequence();
        var first = sequence.AddTrack<AudioTrack>();
        var second = sequence.AddTrack<AudioTrack>();
        first.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)) });
        second.Clips.Add(new AudioClip { Range = new TimeRange(Sec(0), Sec(2)) });
        using var reader = new SequenceAudioReader(sequence, _ => MediaTests.SamplePath);

        float Peak()
        {
            reader.Seek(TimeSpan.Zero);
            var buffer = new float[IAudioDecoder.SampleRate * 2 * 2];
            reader.Read(buffer);
            return buffer.Max();
        }

        Assert.InRange(Peak(), 0.9f, 1.1f);  // 둘 다
        first.Muted = true;
        Assert.InRange(Peak(), 0.4f, 0.6f);  // 하나만
        second.Muted = true;
        Assert.Equal(0, Peak());             // 모두 음소거

        first.Solo = true;                   // 솔로여도 음소거면 소리가 없다
        Assert.Equal(0, Peak());
        first.Muted = false;
        second.Muted = false;
        Assert.InRange(Peak(), 0.4f, 0.6f);  // 솔로인 첫 트랙만
        second.Solo = true;
        Assert.InRange(Peak(), 0.9f, 1.1f);  // 둘 다 솔로
    }

    [Fact]
    public void 파형은_100분의_1초마다_가장_큰_크기를_담는다()
    {
        var waveform = WaveformService.Create(MediaTests.SamplePath)!;

        Assert.InRange(waveform.Peaks.Length, 195, 205); // 2초
        Assert.All(waveform.Peaks[..98], peak => Assert.InRange(peak, 0f, 0.01f));    // 1초까지 무음
        Assert.All(waveform.Peaks[102..195], peak => Assert.InRange(peak, 0.4f, 0.6f)); // 이후 진폭 0.5

        Assert.Null(WaveformService.Create(Path.Combine(AppContext.BaseDirectory, "media", "picture.png")));
        Assert.Null(WaveformService.Create(Path.Combine(Path.GetTempPath(), "없는파일.wav")));
    }
}
