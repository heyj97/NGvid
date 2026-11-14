using CommunityToolkit.Mvvm.Input;
using NGL.Commands;
using NGL.Core;

namespace NGL.App;

/// <summary>컷편집: 선택, 분할, 삭제, 이동, 트리밍, 그룹, 연결, 복사·붙여넣기.</summary>
public partial class MainViewModel
{
    // 실행 취소를 하면 클립 객체가 새로 만들어지므로 선택은 ID로 기억한다.
    private readonly HashSet<Guid> _selection = [];
    private IReadOnlyList<ClipCopy> _clipboard = [];

    private IEnumerable<Clip> AllClips => Project?.Sequence.Tracks.SelectMany(track => track.Clips) ?? [];

    public bool IsSelected(Clip clip) => _selection.Contains(clip.Id);

    public List<Clip> SelectedClips() => AllClips.Where(IsSelected).ToList();

    /// <summary>
    /// 클립을 선택한다. 연결된 클립과 같은 그룹의 클립이 함께 선택된다.
    /// toggle이면 기존 선택에 더하거나 빼고, 아니면 이 클립만 선택한다. null이면 선택을 비운다.
    /// </summary>
    public void Select(Clip? clip, bool toggle = false)
    {
        if (Project is not { } project) return;

        var related = clip is null ? [] : SequenceEdits.WithRelated(project.Sequence, [clip]).Select(each => each.Id).ToList();
        if (!toggle)
        {
            _selection.Clear();
            _selection.UnionWith(related);
        }
        else if (clip is not null && _selection.Contains(clip.Id))
        {
            _selection.ExceptWith(related);
        }
        else
        {
            _selection.UnionWith(related);
        }

        TimelineVersion++;
        RefreshInspector();
    }

    [RelayCommand]
    private void SelectAll()
    {
        _selection.Clear();
        _selection.UnionWith(AllClips.Select(clip => clip.Id));
        TimelineVersion++;
        RefreshInspector();
    }

    /// <summary>편집으로 사라진 클립을 선택에서 뺀다.</summary>
    private void PruneSelection() => _selection.IntersectWith(AllClips.Select(clip => clip.Id).ToHashSet());

    /// <summary>재생 위치에서 자른다. 선택한 클립이 있으면 그것만, 없으면 그 위치의 모든 클립을 자른다.</summary>
    [RelayCommand]
    private void Split()
    {
        if (Project is not { } project) return;
        var targets = _selection.Count > 0 ? SelectedClips() : AllClips.ToList();
        var time = Position;
        Edit("분할", () => SequenceEdits.Split(project.Sequence, targets, time));
    }

    [RelayCommand]
    private void Delete() => EditSelection("삭제", (sequence, clips) => SequenceEdits.Delete(sequence, clips, ripple: false));

    [RelayCommand]
    private void RippleDelete() => EditSelection("리플 삭제", (sequence, clips) => SequenceEdits.Delete(sequence, clips, ripple: true));

    [RelayCommand]
    private void Group() => EditSelection("그룹", (sequence, clips) => SequenceEdits.Group(sequence, clips));

    [RelayCommand]
    private void Ungroup() => EditSelection("그룹 해제", (sequence, clips) => SequenceEdits.Ungroup(sequence, clips));

    [RelayCommand]
    private void Link() => EditSelection("영상·오디오 연결", (sequence, clips) => SequenceEdits.Link(sequence, clips));

    [RelayCommand]
    private void Unlink() => EditSelection("영상·오디오 분리", (sequence, clips) => SequenceEdits.Unlink(sequence, clips));

    private void EditSelection(string name, Action<Sequence, List<Clip>> change)
    {
        if (Project is not { } project || _selection.Count == 0) return;
        var clips = SelectedClips();
        Edit(name, () => change(project.Sequence, clips));
    }

    [RelayCommand]
    private void Copy()
    {
        if (Project is { } project && _selection.Count > 0) _clipboard = SequenceEdits.Copy(project.Sequence, SelectedClips());
    }

    /// <summary>복사한 클립을 재생 위치에 붙여 넣고 그것들을 선택한다.</summary>
    [RelayCommand]
    private void Paste()
    {
        if (Project is not { } project || _clipboard.Count == 0) return;

        IReadOnlyList<Clip> pasted = [];
        var time = Position;
        Edit("붙여넣기", () => pasted = SequenceEdits.Paste(project.Sequence, _clipboard, time));

        _selection.Clear();
        _selection.UnionWith(pasted.Select(clip => clip.Id));
        TimelineVersion++;
        RefreshInspector();
    }

    /// <summary>선택한 클립들을 anchor가 newStart에 오도록 옮긴다. track은 anchor를 옮겨 놓을 트랙.</summary>
    public void MoveSelection(Clip anchor, TimeSpan newStart, Track? track)
    {
        if (Project is not { } project) return;
        var clips = SelectedClips();
        Edit("이동", () => SequenceEdits.Move(project.Sequence, clips, anchor, newStart, track));
    }

    /// <summary>클립의 앞쪽(start) 또는 뒤쪽 끝을 time으로 옮긴다.</summary>
    public void Trim(Clip clip, bool start, TimeSpan time)
    {
        if (Project is not { } project) return;
        Edit("트리밍", () =>
        {
            if (start) SequenceEdits.TrimStart(project, clip, time);
            else SequenceEdits.TrimEnd(project, clip, time);
        });
    }
}
