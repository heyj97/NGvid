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
    TimeSpan Duration { get; }

    /// <summary>
    /// 주어진 시각에 보이는 프레임. 끝을 넘으면 마지막 프레임을 돌려준다.
    /// 프레임을 하나도 읽지 못하면 null.
    /// </summary>
    VideoFrame? GetFrameAt(TimeSpan time);
}
