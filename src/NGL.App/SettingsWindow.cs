using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using NGL.Core;
using NGL.IO;

namespace NGL.App;

/// <summary>프로그램 설정 창과 백업 고르기 창.</summary>
public static class SettingsWindow
{
    /// <summary>확인하면 고친 설정을, 취소하면 null을 돌려준다.</summary>
    public static AppSettings? Ask(Window owner, AppSettings current)
    {
        var undo = Box("UndoLimitBox", current.UndoLimit);
        var minutes = Box("BackupMinutesBox", current.BackupMinutes);
        var count = Box("BackupCountBox", current.BackupCount);
        var proxies = new CheckBox { IsChecked = current.UseProxies, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(proxies, "UseProxiesBox");

        var panel = new StackPanel
        {
            Width = 440,
            Children =
            {
                Row("실행 취소 단계", "기본 50단계, 최대 100단계까지 설정할 수 있습니다.", undo),
                Row("자동 백업 간격 (분)", "0이면 백업하지 않습니다.", minutes),
                Row("남겨 둘 백업 수", "넘치면 오래된 것부터 지웁니다 (1~200).", count),
                Row("미리보기에서 프록시 사용", "프록시가 있는 영상만 해당합니다. 내보내기는 언제나 원본을 씁니다.", proxies),
            },
        };
        if (!Show(owner, "설정", panel, "확인")) return null;

        // 숫자가 아닌 칸은 원래 값을 그대로 둔다. 범위는 설정이 스스로 맞춘다.
        return new AppSettings
        {
            UndoLimit = int.TryParse(undo.Text, out var a) ? a : current.UndoLimit,
            BackupMinutes = int.TryParse(minutes.Text, out var b) ? b : current.BackupMinutes,
            BackupCount = int.TryParse(count.Text, out var c) ? c : current.BackupCount,
            UseProxies = proxies.IsChecked == true,
            // 이 창에서 고치지 않는 값은 그대로 가져간다.
            LibraryWidth = current.LibraryWidth,
            InspectorWidth = current.InspectorWidth,
            TimelineHeight = current.TimelineHeight,
        };
    }

    /// <summary>백업 목록(새것부터)에서 하나를 고른다. 취소하면 null.</summary>
    public static string? AskBackup(Window owner, IReadOnlyList<string> backups)
    {
        var list = new ListBox { Width = 360, Height = 260, SelectedIndex = 0, Padding = new Thickness(4), FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Mono") };
        AutomationProperties.SetAutomationId(list, "BackupList");
        foreach (var backup in backups) list.Items.Add(ProjectBackup.TimeOf(backup).ToString("yyyy-MM-dd HH:mm:ss"));
        var note = new TextBlock
        {
            Text = backups.Count == 0 ? "아직 백업이 없습니다." : "고른 시점의 편집 상태로 되돌립니다. 지금 상태도 백업으로 남습니다.",
            Foreground = DialogParts.Brush("Muted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Width = 360,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var chosen = Show(owner, "백업에서 복구", new StackPanel { Children = { note, list } }, "복구");
        return chosen && list.SelectedIndex >= 0 ? backups[list.SelectedIndex] : null;
    }

    private static TextBox Box(string id, int value)
    {
        var box = new TextBox { Text = value.ToString(), Width = 90, Height = 38, FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Mono") };
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    /// <summary>이름과 설명이 왼쪽에, 값을 고치는 컨트롤이 오른쪽에 있는 한 줄.</summary>
    private static DockPanel Row(string label, string hint, FrameworkElement control)
    {
        DockPanel.SetDock(control, Dock.Right);
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            Children =
            {
                new TextBlock { Text = label },
                new TextBlock { Text = hint, Foreground = DialogParts.Brush("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
            },
        };
        return new DockPanel { Margin = new Thickness(0, 0, 0, 18), Children = { control, text } };
    }

    private static bool Show(Window owner, string title, UIElement content, string okText)
    {
        var ok = DialogParts.Button(okText, primary: true);
        ok.IsDefault = true;
        var cancel = DialogParts.Button("취소");
        cancel.IsCancel = true;
        AutomationProperties.SetAutomationId(ok, "DialogOkButton");

        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Content = DialogParts.Layout(content, cancel, ok),
        };
        DialogParts.Apply(window);
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }
}
