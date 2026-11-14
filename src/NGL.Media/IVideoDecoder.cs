namespace NGL.Media;

/// <summary>
/// 디코딩한 프레임 한 장. 픽셀은 BGRA 8비트이고 디코더가 소유한다.
/// 같은 디코더에서 다음 프레임을 요청하거나 디코더를 닫으면 더 이상 쓸 수 없다.
/// </summary>
public readonly record struct VideoFrame(nint Pixels, int Width, int Height, int Stride, TimeSpan Time);

/// <summary>영상 파일 하나에서 프레임을 꺼낸다. 여러 스레드에서 동시에 쓸 수 없다.</summary>
public interface IVideoDecoder : IDisposable
{
    int Width { get; }
    int Height { get; }
    double FrameRate { get; }

    /// <summary>프레임을 화면에 바로 보이게 하려면 시계 방향으로 돌려야 하는 각도(0·90·180·270).</summary>
    int Rotation { get; }

    /// <summary>프레임을 원본의 1/n 크기로 내놓는다(1·2·4). 미리보기 화질을 낮춰 빠르게 할 때 쓴다.</summary>
    int Downscale { get; set; }
    TimeSpan Duration { get; }

    /// <summary>
    /// 주어진 시각에 보이는 프레임. 끝을 넘으면 마지막 프레임을 돌려준다.
    /// 프레임을 하나도 읽지 못하면 null.
    /// </summary>
    VideoFrame? GetFrameAt(TimeSpan time);
}
