namespace NGL.Media;

/// <summary>
/// 미디어 파일 하나의 소리를 꺼낸다. 원본 형식과 무관하게 항상 48kHz 스테레오 float로 준다.
/// 위치와 길이의 단위는 "샘플 프레임"(좌우 한 쌍)이다. 여러 스레드에서 동시에 쓸 수 없다.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    TimeSpan Duration { get; }

    /// <summary>다음 Read가 시작할 위치를 옮긴다.</summary>
    void Seek(long sampleFrame);

    /// <summary>
    /// 좌·우가 번갈아 놓인 샘플을 채운다. 채운 샘플 프레임 수를 돌려주며,
    /// 요청보다 적으면 파일이 끝난 것이다.
    /// </summary>
    int Read(Span<float> buffer);
}
