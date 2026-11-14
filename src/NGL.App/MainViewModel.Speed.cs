using System.IO;
using CommunityToolkit.Mvvm.Input;
using NGL.Commands;
using NGL.Core;
using NGL.Media;
using NGL.Playback;
using SkiaSharp;

namespace NGL.App;

/// <summary>속성 패널: 선택한 영상·오디오 클립의 재생 속도와 정지 프레임.</summary>
public partial class MainViewModel
{
    private static readonly string[] SpeedProperties = [nameof(HasSpeedSelection), nameof(SpeedPercent), nameof(SpeedKey)];

    private static readonly TimeSpan FreezeDuration = TimeSpan.FromSeconds(2);

    private MediaClip? FirstSpeedClip =>
        SelectedClips().OfType<MediaClip>().FirstOrDefault(clip => SequenceEdits.CanAnimate(clip, AnimatedProperty.Speed));

    public bool HasSpeedSelection => FirstSpeedClip is not null;

    /// <summary>재생 속도(%). 키프레임이 없으면 길이가 속도에 맞게 바뀌고, 있으면 재생 위치에 키프레임을 찍는다.</summary>
    public string SpeedPercent
    {
        get => Percent(Shown(FirstSpeedClip, AnimatedProperty.Speed, FirstSpeedClip?.Speed));
        set => SetNumber(value, MediaClip.MinSpeed * 100, MediaClip.MaxSpeed * 100, number => SetAnimated("속도 변경",
            AnimatedProperty.Speed, number / 100, (MediaClip clip, double speed) => SequenceEdits.SetSpeed(Project!.Sequence, clip, speed)));
    }

    public string SpeedKey => KeyMark(AnimatedProperty.Speed);

    /// <summary>
    /// 재생 위치의 화면을 이미지로 저장해 보관함에 올리고, 그 자리에 2초짜리 정지 화면으로 끼워 넣는다.
    /// 선택한 영상 클립 중 재생 위치에 걸친 것이 대상이다.
    /// </summary>
    [RelayCommand]
    private void FreezeFrame()
    {
        if (Project is not { } project || _folder is not { } folder) return;

        var sequence = project.Sequence;
        var time = sequence.TimeAt(sequence.FrameAt(Position));
        var clip = SelectedClips().OfType<VideoClip>().FirstOrDefault(each => each.Range.Start <= time && time < each.Range.End);
        if (clip is null || sequence.TrackOf(clip) is not { Locked: false })
        {
            StatusText = "정지 프레임: 재생 위치에 걸친 영상 클립을 선택하세요.";
            return;
        }

        MediaAsset still;
        try
        {
            using var frames = new VideoFrameSource(id => project.FindAsset(id) is { } asset ? ProjectPaths.AssetFile(folder, asset) : null);
            using var image = frames.GetImage(clip, time);
            if (image is null)
            {
                StatusText = "정지 프레임을 만들지 못했습니다: 원본을 읽을 수 없습니다.";
                return;
            }

            // 이름을 정해 임시 폴더에 쓴 뒤 가져오기에 맡긴다(같은 이름이 있으면 번호가 붙는다).
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var temp = Path.Combine(Path.GetTempPath(), $"{project.FindAsset(clip.AssetId)?.Name ?? "영상"} 정지 {sequence.FrameAt(time)}.png");
            File.WriteAllBytes(temp, data.ToArray());
            try
            {
                still = MediaImporter.Import(folder, temp);
            }
            finally
            {
                File.Delete(temp);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = "정지 프레임을 만들지 못했습니다: " + e.Message;
            return;
        }

        project.Assets.Add(still);
        _allAssets.Add(NewItem(still));
        RefreshLibrary();

        ImageClip? added = null;
        Edit("정지 프레임", () => added = SequenceEdits.InsertFreezeFrame(sequence, clip, time, still.Id, FreezeDuration));
        if (added is not null) Select(added);
    }
}
