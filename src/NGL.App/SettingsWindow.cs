using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NGL.Core;
using NGL.IO;

namespace NGL.App;

/// <summary>프로그램 설정 창과 백업 고르기 창.</summary>
public static class SettingsWindow
{
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xE6, 0xE7, 0xEA));

    /// <summary>확인하면 고친 설정을, 취소하면 null을 돌려준다.</summary>
    public static AppSettings? Ask(Window owner, AppSettings current)
    {
        var undo = Box("UndoLimitBox", current.UndoLimit);
        var minutes = Box("BackupMinutesBox", current.BackupMinutes);
        var count = Box("BackupCountBox", current.BackupCount);
        var proxies = new CheckBox
        {
            Content = "프록시가 있는 영상은 미리보기에서 프록시 사용",
            IsChecked = current.UseProxies,
            Foreground = Text,
            Margin = new Thickness(0, 8, 0, 0),
        };
        AutomationProperties.SetAutomationId(proxies, "UseProxiesBox");

        var panel = new StackPanel
        {
            Children =
            {
                Row("실행 취소 단계 (1~100)", undo),
                Row("자동 백업 간격 (분, 0이면 끔)", minutes),
                Row("남겨 둘 백업 수 (1~200)", count),
                proxies,
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
        };
    }

    /// <summary>백업 목록(새것부터)에서 하나를 고른다. 취소하면 null.</summary>
    public static string? AskBackup(Window owner, IReadOnlyList<string> backups)
    {
        var list = new ListBox { Width = 320, Height = 260, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(list, "BackupList");
        foreach (var backup in backups) list.Items.Add(ProjectBackup.TimeOf(backup).ToString("yyyy-MM-dd HH:mm:ss"));
        var note = new TextBlock
        {
            Text = backups.Count == 0 ? "아직 백업이 없습니다." : "고른 시점의 편집 상태로 되돌립니다. 지금 상태도 백업으로 남습니다.",
            Foreground = Text,
            TextWrapping = TextWrapping.Wrap,
            Width = 320,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var chosen = Show(owner, "백업에서 복구", new StackPanel { Children = { note, list } }, "복구");
        return chosen && list.SelectedIndex >= 0 ? backups[list.SelectedIndex] : null;
    }

    private static TextBox Box(string id, int value)
    {
        var box = new TextBox { Text = value.ToString(), Width = 70, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    private static DockPanel Row(string label, TextBox box)
    {
        DockPanel.SetDock(box, Dock.Right);
        var text = new TextBlock { Text = label, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        return new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = { box, text } };
    }

    private static bool Show(Window owner, string title, UIElement content, string okText)
    {
        var ok = new Button { Content = okText, IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        AutomationProperties.SetAutomationId(ok, "DialogOkButton");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, ok },
        };

        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1C)),
            Content = new StackPanel { Margin = new Thickness(20), Children = { content, buttons } },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }
}
