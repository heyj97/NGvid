namespace NGL.Core;

/// <summary>프로젝트와 무관한 프로그램 설정.</summary>
public sealed class AppSettings
{
    public const int DefaultUndoLimit = 50;
    public const int MaxUndoLimit = 100;

    private int _undoLimit = DefaultUndoLimit;

    /// <summary>실행 취소 단계 수. 1~100을 벗어난 값은 범위 안으로 맞춘다.</summary>
    public int UndoLimit
    {
        get => _undoLimit;
        set => _undoLimit = Math.Clamp(value, 1, MaxUndoLimit);
    }
}
