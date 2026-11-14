namespace NGL.Core;

/// <summary>프로젝트와 무관한 프로그램 설정.</summary>
public sealed class AppSettings
{
    public const int DefaultUndoLimit = 50;
    public const int MaxUndoLimit = 100;

    private int _undoLimit = DefaultUndoLimit;
    private int _backupMinutes = 5;
    private int _backupCount = 20;

    /// <summary>실행 취소 단계 수. 1~100을 벗어난 값은 범위 안으로 맞춘다.</summary>
    public int UndoLimit
    {
        get => _undoLimit;
        set => _undoLimit = Math.Clamp(value, 1, MaxUndoLimit);
    }

    /// <summary>자동 백업 간격(분). 0이면 백업하지 않는다.</summary>
    public int BackupMinutes
    {
        get => _backupMinutes;
        set => _backupMinutes = Math.Clamp(value, 0, 120);
    }

    /// <summary>프로젝트마다 남겨 둘 백업의 수. 넘치면 오래된 것부터 지운다.</summary>
    public int BackupCount
    {
        get => _backupCount;
        set => _backupCount = Math.Clamp(value, 1, 200);
    }

    /// <summary>프록시가 있는 영상은 미리보기에서 프록시를 쓴다. 내보내기는 언제나 원본을 쓴다.</summary>
    public bool UseProxies { get; set; } = true;
}
