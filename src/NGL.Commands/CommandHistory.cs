using NGL.Core;
using NGL.IO;

namespace NGL.Commands;

/// <summary>되돌릴 수 있는 편집 하나.</summary>
public interface IEditCommand
{
    /// <summary>메뉴에 "실행 취소: 분할"처럼 보일 이름.</summary>
    string Name { get; }

    void Execute();
    void Undo();
}

/// <summary>여러 명령을 하나로 묶는다. 한 번에 실행되고 한 번에 취소된다.</summary>
public sealed class CompositeCommand(string name, IReadOnlyList<IEditCommand> commands) : IEditCommand
{
    public string Name => name;

    public void Execute()
    {
        foreach (var command in commands) command.Execute();
    }

    public void Undo()
    {
        for (var i = commands.Count - 1; i >= 0; i--) commands[i].Undo();
    }
}

/// <summary>
/// 시퀀스를 바꾸는 편집. 바꾸기 전과 후의 상태를 통째로 떠 두고, 취소·다시 실행은 그 상태로 되돌린다.
/// 어떤 편집이든 change만 주면 되돌릴 수 있고, 원본 파일은 경로만 담기므로 메모리를 거의 쓰지 않는다.
/// </summary>
public sealed class SequenceEditCommand(string name, Sequence sequence, Action change) : IEditCommand
{
    private string? _before;
    private string? _after;

    public string Name => name;

    /// <summary>실행했더니 실제로 달라진 것이 있는가. 달라진 것이 없으면 기록할 필요가 없다.</summary>
    public bool Changed => _before != _after;

    public void Execute()
    {
        if (_after is not null)
        {
            SequenceSnapshot.Restore(sequence, _after); // 다시 실행
            return;
        }

        _before = SequenceSnapshot.Take(sequence);
        change();
        _after = SequenceSnapshot.Take(sequence);
    }

    public void Undo()
    {
        if (_before is not null) SequenceSnapshot.Restore(sequence, _before);
    }
}

/// <summary>실행한 편집을 기억해 두고 실행 취소·다시 실행을 한다.</summary>
public sealed class CommandHistory
{
    private readonly List<IEditCommand> _undo = [];
    private readonly List<IEditCommand> _redo = [];
    private int _limit = AppSettings.DefaultUndoLimit;

    /// <summary>기억할 단계 수(1~100). 줄이면 오래된 것부터 버린다.</summary>
    public int Limit
    {
        get => _limit;
        set
        {
            _limit = Math.Clamp(value, 1, AppSettings.MaxUndoLimit);
            Trim();
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.LastOrDefault()?.Name;
    public string? RedoName => _redo.LastOrDefault()?.Name;

    /// <summary>명령을 실행하고 기억한다.</summary>
    public void Execute(IEditCommand command)
    {
        command.Execute();
        Record(command);
    }

    /// <summary>이미 실행한 명령을 기억한다. 새 편집을 하면 다시 실행할 것은 사라진다.</summary>
    public void Record(IEditCommand command)
    {
        _undo.Add(command);
        _redo.Clear();
        Trim();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo();
        _redo.Add(command);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Execute();
        _undo.Add(command);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void Trim()
    {
        if (_undo.Count > _limit) _undo.RemoveRange(0, _undo.Count - _limit);
    }
}
