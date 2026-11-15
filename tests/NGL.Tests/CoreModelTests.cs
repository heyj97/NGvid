using NGL.Core;

namespace NGL.Tests;

public class CoreModelTests
{
    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void TimeRange_끝은_포함하지_않는다()
    {
        var range = new TimeRange(Sec(1), Sec(2));

        Assert.Equal(Sec(3), range.End);
        Assert.True(range.Contains(Sec(1)));
        Assert.False(range.Contains(Sec(3)));
        Assert.True(range.Overlaps(new TimeRange(Sec(2.5), Sec(1))));
        Assert.False(range.Overlaps(new TimeRange(Sec(3), Sec(1))));
    }

    [Fact]
    public void TimeRange_음수_길이는_거부한다() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(Sec(0), Sec(-1)));

    [Fact]
    public void Clip_0초보다_앞에_놓을_수_없다() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoClip { Range = new TimeRange(Sec(-1), Sec(2)) });

    [Fact]
    public void 트랙은_맞는_종류의_클립만_받는다()
    {
        var video = new VideoTrack();
        video.Clips.Add(new VideoClip());
        video.Clips.Add(new ImageClip());
        video.Clips.Add(new TextClip());
        Assert.Throws<ArgumentException>(() => video.Clips.Add(new AudioClip()));
        Assert.Throws<ArgumentException>(() => video.Clips[0] = new AudioClip());
        Assert.Equal(3, video.Clips.Count);

        var audio = new AudioTrack();
        audio.Clips.Add(new AudioClip());
        Assert.Throws<ArgumentException>(() => audio.Clips.Add(new VideoClip()));
    }

    [Fact]
    public void Sequence_기본값은_16대9_30fps()
    {
        var sequence = new Sequence();

        Assert.Equal(SequencePreset.Landscape, sequence.Preset);
        Assert.Equal(30, sequence.FrameRate);
        Assert.Equal(TimeSpan.Zero, sequence.Duration);
    }

    [Fact]
    public void Sequence_지원하지_않는_fps는_거부한다()
    {
        var sequence = new Sequence { FrameRate = 60 };

        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.FrameRate = 24);
        Assert.Equal(60, sequence.FrameRate);
    }

    [Fact]
    public void Sequence_길이는_가장_늦게_끝나는_클립의_끝이다()
    {
        var sequence = new Sequence();
        var video = new VideoTrack();
        var audio = new AudioTrack();
        sequence.Tracks.Add(video);
        sequence.Tracks.Add(audio);
        video.Clips.Add(new VideoClip { Range = new TimeRange(Sec(0), Sec(5)) });
        audio.Clips.Add(new AudioClip { Range = new TimeRange(Sec(2), Sec(10)) });

        Assert.Equal(Sec(12), sequence.Duration);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void 프레임_번호와_시각은_서로_되돌아온다(int frameRate)
    {
        var sequence = new Sequence { FrameRate = frameRate };

        // 2시간 분량의 모든 프레임
        for (long frame = 0; frame <= frameRate * 7200; frame++)
        {
            var time = sequence.TimeAt(frame);
            Assert.Equal(frame, sequence.FrameAt(time));
            Assert.Equal(time, sequence.SnapToFrame(time));
        }
    }

    [Fact]
    public void SnapToFrame은_가까운_프레임_경계로_맞춘다()
    {
        var sequence = new Sequence { FrameRate = 30 };

        Assert.Equal(sequence.TimeAt(30), sequence.SnapToFrame(Sec(1.01)));
        Assert.Equal(sequence.TimeAt(31), sequence.SnapToFrame(Sec(1.02)));
    }

    [Fact]
    public void Project_원본을_ID로_찾는다()
    {
        var project = new Project { Name = "테스트" };
        var asset = new MediaAsset { Kind = MediaKind.Video, Name = "interview", RelativePath = "media/interview.mp4" };
        project.Assets.Add(asset);

        Assert.Equal(Project.CurrentVersion, project.Version);
        Assert.Same(asset, project.FindAsset(asset.Id));
        Assert.Null(project.FindAsset(Guid.NewGuid()));
    }
}
