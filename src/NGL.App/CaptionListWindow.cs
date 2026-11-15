using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using NGL.Core;

namespace NGL.App;

/// <summary>자막 목록의 한 줄. 칸을 고치면 바로 편집으로 반영된다.</summary>
public sealed class CaptionRow(MainViewModel owner, TextClip clip)
{
    public Guid Id { get; } = clip.Id;
    public TimeSpan StartTime { get; } = clip.Range.Start;

    public string Start
    {
        get => CaptionListWindow.Format(clip.Range.Start);
        set
        {
            if (CaptionListWindow.ParseTime(value) is { } start) owner.EditCaption(Id, start, clip.Range.End, clip.Text);
        }
    }

    public string End
    {
        get => CaptionListWindow.Format(clip.Range.End);
        set
        {
            if (CaptionListWindow.ParseTime(value) is { } end) owner.EditCaption(Id, clip.Range.Start, end, clip.Text);
        }
    }

    public string Text
    {
        get => clip.Text;
        set => owner.EditCaption(Id, clip.Range.Start, clip.Range.End, value);
    }
}

/// <summary>텍스트가 있는 한 트랙의 문구와 시간을 표로 고치고, SRT·VTT를 가져오거나 SRT로 내보낸다.</summary>
public sealed class CaptionListWindow : Window
{
    private readonly MainViewModel _owner;
    private readonly ComboBox _tracks = new() { MinWidth = 140, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        Margin = new Thickness(0, 12, 0, 0),
    };

    public CaptionListWindow(MainViewModel owner)
    {
        _owner = owner;
        Title = "텍스트 목록";
        Width = 820;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DialogParts.Apply(this);
        ShowInTaskbar = true;


        _grid.Columns.Add(new DataGridTextColumn { Header = "시작", Binding = new Binding(nameof(CaptionRow.Start)), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "끝", Binding = new Binding(nameof(CaptionRow.End)), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "문구",
            Binding = new Binding(nameof(CaptionRow.Text)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        AutomationProperties.SetAutomationId(_grid, "CaptionGrid");
        AutomationProperties.SetAutomationId(_tracks, "CaptionTrackBox");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_tracks);
        buttons.Children.Add(Button("행 추가", "CaptionAddButton", () => { if (SelectedTrack is { } track) _owner.AddCaptionAtEnd(track); }));
        buttons.Children.Add(Button("행 삭제", "CaptionDeleteButton", () => { if (_grid.SelectedItem is CaptionRow row) _owner.DeleteCaption(row.Id); }));
        buttons.Children.Add(Button("SRT·VTT 가져오기", "CaptionImportButton", Import));
        buttons.Children.Add(Button("SRT 내보내기", "CaptionExportButton", Export));
        buttons.Children.Add(Button("모든 텍스트에 스타일 적용", "CaptionStyleButton", ApplyStyle));

        var panel = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(buttons, Dock.Top);
        panel.Children.Add(buttons);
        panel.Children.Add(_grid);
        Content = panel;

        _tracks.SelectionChanged += (_, _) => RefreshRows();
        // 줄을 고르면 그 자막으로 가서 선택한다.
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is CaptionRow row) _owner.GoToCaption(row.Id);
        };
        // 편집이 끝난 뒤(칸의 값이 반영된 뒤)에 표를 다시 만든다.
        _owner.SequenceEdited += OnSequenceEdited;
        Closed += (_, _) => _owner.SequenceEdited -= OnSequenceEdited;

        RefreshTracks();
    }

    private VideoTrack? SelectedTrack =>
        _owner.TextTracks() is var tracks && _tracks.SelectedIndex >= 0 && _tracks.SelectedIndex < tracks.Count
            ? tracks[_tracks.SelectedIndex]
            : null;

    private void OnSequenceEdited() => Dispatcher.BeginInvoke(RefreshTracks);

    private void RefreshTracks()
    {
        var index = Math.Max(0, _tracks.SelectedIndex);
        var names = _owner.TextTracks().Select(track => track.Name).ToList();
        _tracks.ItemsSource = names;
        _tracks.SelectedIndex = names.Count == 0 ? -1 : Math.Min(index, names.Count - 1);
        RefreshRows();
    }

    private void RefreshRows()
    {
        var selected = (_grid.SelectedItem as CaptionRow)?.Id;
        var rows = SelectedTrack?.Clips.OfType<TextClip>().Select(clip => new CaptionRow(_owner, clip)).ToList() ?? [];
        _grid.ItemsSource = rows;
        _grid.SelectedItem = rows.FirstOrDefault(row => row.Id == selected);
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "자막 가져오기", Filter = "자막 파일|*.srt;*.vtt|모든 파일|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        _owner.ImportSubtitles(dialog.FileName);
        _tracks.SelectedIndex = 0; // 가져온 자막은 맨 위의 새 트랙에 들어간다
    }

    private void Export()
    {
        if (SelectedTrack is not { } track) return;
        var dialog = new SaveFileDialog { Title = "자막 내보내기", Filter = "SRT 자막|*.srt", FileName = track.Name + ".srt" };
        if (dialog.ShowDialog(this) == true) _owner.ExportSubtitles(track, dialog.FileName);
    }

    /// <summary>고른 줄(없으면 첫 줄)의 스타일을 이 트랙의 모든 텍스트에 복사한다.</summary>
    private void ApplyStyle()
    {
        var id = (_grid.SelectedItem as CaptionRow)?.Id ?? (_grid.Items.Count > 0 ? ((CaptionRow)_grid.Items[0]).Id : (Guid?)null);
        if (id is { } source) _owner.ApplyCaptionStyleToTrack(source);
    }

    private static Button Button(string text, string automationId, Action click)
    {
        var button = new Button { Content = text, Height = 32, Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>분:초.밀리초 (예: 1:02.500)</summary>
    public static string Format(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}");

    /// <summary>"초", "분:초", "시:분:초"를 읽는다. 초에는 소수를 쓸 수 있다. 읽을 수 없으면 null.</summary>
    public static TimeSpan? ParseTime(string text)
    {
        var total = 0.0;
        foreach (var part in text.Trim().Replace(',', '.').Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0) return null;
            total = total * 60 + number;
        }

        return TimeSpan.FromSeconds(total);
    }
}
