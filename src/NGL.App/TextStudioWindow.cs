using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NGL.Core;
using NGL.Rendering;
using SkiaSharp;
using TextEffect = NGL.Core.TextEffect; // WPF에도 같은 이름이 있다

namespace NGL.App;

/// <summary>
/// 텍스트 프리셋을 만들고 고치는 창. 프리셋은 글자 모양(색·굵기·외곽선·그림자·배경 박스)과 효과(페이드·이동·확대·팝업)를 담는다.
/// 글꼴·크기·위치는 클립마다 속성 패널에서 정한다.
/// </summary>
public sealed class TextStudioWindow : Window
{
    /// <summary>미리보기 견본 클립의 길이(초). 효과 타임라인의 길이이기도 하다.</summary>
    private const double Sample = 3;

    private static readonly Dictionary<TextEffectKind, string> KindNames = new()
    {
        [TextEffectKind.FadeIn] = "페이드 인",
        [TextEffectKind.FadeOut] = "페이드 아웃",
        [TextEffectKind.Move] = "이동",
        [TextEffectKind.Scale] = "확대·축소",
        [TextEffectKind.Popup] = "팝업",
    };

    /// <summary>효과의 숫자 칸 하나: 이름표, 읽기, 쓰기, 허용 범위.</summary>
    private sealed record Field(string Label, Func<TextEffect, double> Get, Action<TextEffect, double> Set, double Min, double Max);

    private static readonly Field StartField = new("시작 (초)", effect => effect.Start, (effect, value) => effect.Start = value, 0, 3600);
    private static readonly Field DurationField = new("지속 시간 (초)", effect => effect.Duration, (effect, value) => effect.Duration = value, 0.01, 3600);

    private static Field[] FieldsOf(TextEffectKind kind) => kind switch
    {
        TextEffectKind.Move =>
        [
            new("시작 X (%)", effect => effect.FromX, (effect, value) => effect.FromX = value, -100, 100),
            new("시작 Y (%)", effect => effect.FromY, (effect, value) => effect.FromY = value, -100, 100),
            new("도착 X (%)", effect => effect.ToX, (effect, value) => effect.ToX = value, -100, 100),
            new("도착 Y (%)", effect => effect.ToY, (effect, value) => effect.ToY = value, -100, 100),
        ],
        TextEffectKind.Scale =>
        [
            new("시작 크기 (%)", effect => effect.From, (effect, value) => effect.From = value, 0, 1000),
            new("끝 크기 (%)", effect => effect.To, (effect, value) => effect.To = value, 0, 1000),
        ],
        TextEffectKind.Popup =>
        [
            new("시작 크기 (%)", effect => effect.From, (effect, value) => effect.From = value, 0, 1000),
            new("최대 크기 (%)", effect => effect.Peak, (effect, value) => effect.Peak = value, 100, 1000),
        ],
        _ => [],
    };

    private readonly MainViewModel _owner;
    private readonly SKSize _frame;   // 견본을 그리는 화면의 크기(열려 있는 프로젝트의 것)
    private readonly float _zoom;     // 그 화면을 미리보기 크기로 줄이는 비율
    private readonly WriteableBitmap _bitmap;
    private readonly Stopwatch _clock = new();

    private readonly ListBox _list = new() { MinHeight = 200 };
    private readonly TextBox _name = Box(180);
    private readonly CheckBox _bold = new() { Content = "굵게", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _color = Box(100), _outlineColor = Box(100), _shadowColor = Box(100), _boxColor = Box(100);
    private readonly TextBox _outlineWidth = Box(52), _shadowOffset = Box(52);
    private readonly ComboBox _kind = new() { Height = 32, ItemsSource = KindNames.Values.ToList(), SelectedIndex = 0 };
    private readonly StackPanel _cards = new();
    private readonly StackPanel _rows = new();
    private readonly Button _play = new() { Content = "▶ 재생", Height = 32, Width = 96 };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = Sample, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private readonly TextBlock _clockText = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>효과마다 타임라인의 막대와 그 막대가 놓인 줄, 시작·지속 시간 칸.</summary>
    private readonly List<(TextEffect Effect, Border Bar, Canvas Track, TextBox Start, TextBox Duration)> _bars = [];

    private List<TextEffect> _effects = [];
    private (TextEffect Effect, string? Edge, double Start, double Length, double X, double Width)? _drag;
    private double _time;      // 미리보기가 보여주는 시각(초)
    private string? _selected; // 고치고 있는 프리셋의 저장된 이름
    private bool _loading;     // 칸을 코드에서 채우는 중 (사용자가 고친 것이 아님)

    public TextStudioWindow(MainViewModel owner)
    {
        _owner = owner;
        var preset = owner.Project?.Sequence.Preset;
        _frame = new SKSize(preset?.Width ?? 1920, preset?.Height ?? 1080);
        _zoom = Math.Min(560 / _frame.Width, 315 / _frame.Height);
        _bitmap = new WriteableBitmap((int)(_frame.Width * _zoom), (int)(_frame.Height * _zoom), 96, 96, PixelFormats.Pbgra32, null);

        Title = "텍스트 스튜디오";
        Width = 1200;
        SizeToContent = SizeToContent.Height;
        DialogParts.Apply(this);
        ShowInTaskbar = true;
        AutomationProperties.SetAutomationId(_list, "TextPresetList");
        AutomationProperties.SetName(_kind, "추가할 효과");
        AutomationProperties.SetName(_seek, "재생 위치");

        // 왼쪽: 프리셋 목록
        var add = new Button { Content = "새 프리셋", Height = 32, Margin = new Thickness(0, 8, 6, 0) };
        var delete = new Button { Content = "삭제", Height = 32, Margin = new Thickness(0, 8, 0, 0) };
        var listButtons = new UniformGrid { Columns = 2, Children = { add, delete } };
        DockPanel.SetDock(listButtons, Dock.Bottom);
        var left = new DockPanel { Width = 200, Margin = new Thickness(0, 0, 20, 0), Children = { listButtons, _list } };
        DockPanel.SetDock(left, Dock.Left);

        // 오른쪽: 글자 모양과 효과의 값들
        var addEffect = new Button { Content = "＋ 효과 추가", Height = 32, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(addEffect, Dock.Right);
        var settings = new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 0),
            Children =
            {
                Row("이름", _name),
                Row("글자 색", _bold, Swatch(_color), _color),
                Row("외곽선", Labeled("두께", _outlineWidth), Swatch(_outlineColor), _outlineColor),
                Row("그림자", Labeled("거리", _shadowOffset), Swatch(_shadowColor), _shadowColor),
                Row("배경 박스", Swatch(_boxColor), _boxColor),
                Heading("효과"),
                new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { addEffect, _kind } },
                _cards,
            },
        };
        _boxColor.ToolTip = "비우면 배경 박스가 없습니다. 반투명은 네모를 눌러 불투명도를 낮추세요.";
        var right = new ScrollViewer { Width = 350, MaxHeight = 560, Margin = new Thickness(20, 0, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = settings };
        DockPanel.SetDock(right, Dock.Right);

        // 가운데: 미리보기, 재생, 효과 타임라인
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(_clockText, Dock.Right);
        var center = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Height = 315, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2D)), CornerRadius = new CornerRadius(6),
                    Child = new Image { Source = _bitmap, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center },
                },
                new DockPanel { Margin = new Thickness(0, 10, 0, 0), Children = { _play, _clockText, _seek } },
                Heading("효과 타임라인"),
                _rows,
            },
        };

        var save = DialogParts.Button("저장", primary: true);
        var close = DialogParts.Button("닫기");
        close.IsCancel = true;
        Content = DialogParts.Layout(new DockPanel { Children = { left, right, center } }, _status, close, save);

        _list.SelectionChanged += (_, _) => Load(_list.SelectedItem as string);
        add.Click += (_, _) => AddPreset();
        delete.Click += (_, _) =>
        {
            if (_selected is null) return;
            _owner.DeleteTextPreset(_selected);
            Reload(null);
        };
        save.Click += (_, _) => Save();
        close.Click += (_, _) => Close();
        Closed += (_, _) => StopPlaying();

        addEffect.Click += (_, _) =>
        {
            _effects.Add(TextEffect.Create(KindNames.Keys.ElementAt(Math.Max(0, _kind.SelectedIndex))));
            RebuildEffects();
        };
        _play.Click += (_, _) =>
        {
            if (_clock.IsRunning)
            {
                StopPlaying();
                return;
            }

            _clock.Restart();
            _play.Content = "Ⅱ 일시정지";
            CompositionTarget.Rendering += OnFrame;
        };
        _seek.ValueChanged += (_, e) =>
        {
            if (_loading) return;
            StopPlaying();
            ShowTime(e.NewValue);
        };

        foreach (var box in new[] { _color, _outlineColor, _shadowColor, _boxColor, _outlineWidth, _shadowOffset }) box.TextChanged += (_, _) => Render();
        _bold.Click += (_, _) => Render();

        Reload(null);
    }

    // ───── 화면 조각 ─────

    private static TextBox Box(double width) => new() { Width = width, Height = 32, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };

    private static TextBlock Muted(string text) => new() { Text = text, Foreground = DialogParts.Brush("Muted"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 10) };

    /// <summary>이름표가 왼쪽에, 칸들이 오른쪽에 붙은 한 줄. controls는 오른쪽 끝부터 적는다.</summary>
    private static DockPanel Row(string label, params UIElement[] controls)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var control in controls)
        {
            DockPanel.SetDock(control, Dock.Right);
            row.Children.Add(control);
        }

        row.Children.Add(Muted(label));
        return row;
    }

    private static StackPanel Labeled(string label, TextBox box)
    {
        var text = Muted(label);
        text.Margin = new Thickness(0, 0, 6, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), Children = { text, box } };
    }

    /// <summary>칸의 색을 보여주는 네모. 누르면 색 고르기 창이 열리고 고른 색이 칸에 적힌다.</summary>
    private Button Swatch(TextBox box)
    {
        var swatch = new Button { Style = (Style)FindResource("Swatch") };
        box.TextChanged += (_, _) => swatch.Background = TextRenderer.ColorOf(Hex(box)) is { } color ? ColorPicker.BrushOf(color) : Brushes.Transparent;
        swatch.Click += (_, _) =>
        {
            if (ColorPicker.Pick(this, box.Text) is { } picked) box.Text = picked;
        };
        return swatch;
    }

    /// <summary>칸에 적힌 색(# 없이 적어도 된다). 비었거나 읽을 수 없으면 null.</summary>
    private static string? Hex(TextBox box)
    {
        var text = box.Text.Trim();
        if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
        return TextRenderer.ColorOf(text) is null ? null : text.ToUpperInvariant();
    }

    private static double? Number(TextBox box) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Format(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>칸들에 적힌 모양.</summary>
    private TextStyle ReadStyle() => new()
    {
        Bold = _bold.IsChecked == true,
        Color = Hex(_color) ?? "#FFFFFF",
        OutlineColor = Hex(_outlineColor) ?? "#000000",
        OutlineWidth = Math.Clamp(Number(_outlineWidth) ?? 0, 0, 100),
        ShadowColor = Hex(_shadowColor) ?? "#000000",
        ShadowOffset = Math.Clamp(Number(_shadowOffset) ?? 0, 0, 200),
        BoxColor = Hex(_boxColor),
    };

    // ───── 프리셋 ─────

    /// <summary>목록을 다시 읽고 name(없으면 첫 프리셋)을 고른다.</summary>
    private void Reload(string? name)
    {
        var names = _owner.TextPresets.Select(preset => preset.Name).ToList();
        _list.ItemsSource = names;
        _list.SelectedItem = names.Contains(name ?? "") ? name : names.FirstOrDefault();
        if (names.Count == 0) Load(null);
    }

    /// <summary>프리셋의 값을 칸들에 채운다.</summary>
    private void Load(string? name)
    {
        var preset = _owner.TextPresets.FirstOrDefault(each => each.Name == name);
        _selected = preset?.Name;
        var style = preset?.Style ?? new TextStyle();
        StopPlaying();

        _loading = true;
        _name.Text = preset?.Name ?? "";
        _bold.IsChecked = style.Bold;
        _color.Text = style.Color;
        _outlineColor.Text = style.OutlineColor;
        _outlineWidth.Text = Format(style.OutlineWidth);
        _shadowColor.Text = style.ShadowColor;
        _shadowOffset.Text = Format(style.ShadowOffset);
        _boxColor.Text = style.BoxColor ?? "";
        _loading = false;

        _status.Text = "";
        _effects = (preset?.Effects ?? []).Select(effect => effect with { }).ToList();
        RebuildEffects();
        ShowTime(RestTime());
    }

    private void AddPreset()
    {
        var names = _owner.TextPresets.Select(preset => preset.Name).ToHashSet();
        var name = "새 프리셋";
        for (var number = 2; names.Contains(name); number++) name = $"새 프리셋 {number}";

        _owner.SaveTextPreset(null, new TextStylePreset(name, new TextStyle()));
        Reload(name);
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            _status.Text = "프리셋 이름을 적어 주세요.";
            return;
        }

        _owner.SaveTextPreset(_selected, new TextStylePreset(name, ReadStyle(), _effects.Select(effect => effect with { }).ToList()));
        Reload(name);
    }

    // ───── 효과 ─────

    /// <summary>효과 목록이 바뀌었을 때(더하기·빼기·프리셋 바꾸기) 값 칸들과 타임라인의 줄을 다시 만든다.</summary>
    private void RebuildEffects()
    {
        _cards.Children.Clear();
        _rows.Children.Clear();
        _bars.Clear();
        if (_effects.Count == 0) _rows.Children.Add(Muted("효과가 없습니다. 오른쪽에서 효과를 추가하세요."));

        foreach (var effect in _effects)
        {
            var fields = new UniformGrid { Columns = 2 };
            var boxes = new List<TextBox>();
            foreach (var field in new[] { StartField, DurationField }.Concat(FieldsOf(effect.Kind)))
            {
                var box = Box(72);
                box.Text = Format(field.Get(effect));
                box.TextChanged += (_, _) =>
                {
                    if (_loading || Number(box) is not { } number) return;
                    field.Set(effect, Math.Clamp(number, field.Min, field.Max));
                    UpdateBars();
                    Render();
                };
                boxes.Add(box);
                DockPanel.SetDock(box, Dock.Right);
                fields.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 8, 8), Children = { box, Muted(field.Label) } });
            }

            var remove = new Button { Content = "효과 제거", Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 0, 10, 0) };
            remove.Click += (_, _) =>
            {
                _effects.RemoveAt(_effects.FindIndex(each => ReferenceEquals(each, effect)));
                RebuildEffects();
            };
            _cards.Children.Add(new Border
            {
                BorderBrush = DialogParts.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 2, 10), Margin = new Thickness(0, 0, 0, 8),
                Child = new StackPanel { Children = { new TextBlock { Text = KindNames[effect.Kind], FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) }, fields, remove } },
            });

            _rows.Children.Add(TimelineRow(effect, boxes[0], boxes[1]));
        }

        UpdateBars();
        Render();
    }

    /// <summary>타임라인의 한 줄: 효과 이름과, 끌어서 옮기거나 양 끝을 끌어 길이를 바꾸는 막대.</summary>
    private DockPanel TimelineRow(TextEffect effect, TextBox startBox, TextBox durationBox)
    {
        Border Grip(string edge, HorizontalAlignment side) => new()
        {
            Width = 6, HorizontalAlignment = side, Background = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), Cursor = Cursors.SizeWE, Tag = edge,
        };

        var track = new Canvas { Height = 22, ClipToBounds = true };
        var bar = new Border
        {
            Height = 16, CornerRadius = new CornerRadius(3), Background = DialogParts.Brush("Accent"), Cursor = Cursors.SizeAll,
            Child = new Grid { Children = { Grip("start", HorizontalAlignment.Left), Grip("end", HorizontalAlignment.Right) } },
        };
        Canvas.SetTop(bar, 3);
        track.Children.Add(bar);
        track.SizeChanged += (_, _) => UpdateBars();
        _bars.Add((effect, bar, track, startBox, durationBox));

        bar.MouseLeftButtonDown += (_, e) =>
        {
            StopPlaying();
            _drag = (effect, (e.OriginalSource as FrameworkElement)?.Tag as string, effect.Start, effect.Duration, e.GetPosition(track).X, track.ActualWidth);
            bar.CaptureMouse();
            e.Handled = true;
        };
        bar.MouseMove += (_, e) =>
        {
            if (!bar.IsMouseCaptured || _drag is not { Width: > 0 } drag) return;

            var delta = Math.Round((e.GetPosition(track).X - drag.X) * Sample / drag.Width, 2);
            var end = Math.Clamp(drag.Start + drag.Length, 0.01, Sample);
            if (drag.Edge == "start")
            {
                effect.Start = Math.Clamp(drag.Start + delta, 0, end - 0.01);
                effect.Duration = Math.Max(0.01, end - effect.Start);
            }
            else if (drag.Edge == "end")
            {
                effect.Start = Math.Min(drag.Start, Sample - 0.01);
                effect.Duration = Math.Max(0.01, Math.Min(Sample, drag.Start + drag.Length + delta) - effect.Start);
            }
            else
            {
                effect.Duration = Math.Min(Sample, drag.Length);
                effect.Start = Math.Clamp(drag.Start + delta, 0, Sample - effect.Duration);
            }

            (effect.Start, effect.Duration) = (Math.Round(effect.Start, 2), Math.Round(effect.Duration, 2));
            _loading = true;
            (startBox.Text, durationBox.Text) = (Format(effect.Start), Format(effect.Duration));
            _loading = false;
            UpdateBars();
            ShowTime(drag.Edge == "end" ? effect.Start + effect.Duration : effect.Start); // 끄는 쪽의 순간을 보여준다
        };
        bar.MouseLeftButtonUp += (_, _) =>
        {
            _drag = null;
            bar.ReleaseMouseCapture();
        };

        var label = Muted(KindNames[effect.Kind]);
        label.Width = 90;
        DockPanel.SetDock(label, Dock.Left);
        return new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            Children = { label, new Border { Background = DialogParts.Brush("Input"), CornerRadius = new CornerRadius(4), Child = track } },
        };
    }

    /// <summary>막대들을 효과의 시작·지속 시간에 맞는 자리와 너비로 놓는다.</summary>
    private void UpdateBars()
    {
        foreach (var (effect, bar, track, _, _) in _bars)
        {
            var width = track.ActualWidth;
            var left = Math.Min(1, effect.Start / Sample) * width;
            var right = Math.Min(1, (effect.Start + effect.Duration) / Sample) * width;
            Canvas.SetLeft(bar, Math.Max(0, Math.Min(left, width - 12)));
            bar.Width = Math.Max(12, right - left); // 아주 짧은 효과도 잡을 수 있게
        }
    }

    // ───── 미리보기 ─────

    /// <summary>재생하지 않을 때 보여줄 시각: 글자가 다 나타난 뒤, 사라지기 시작하기 전.</summary>
    private double RestTime() =>
        _effects.Where(effect => effect.Kind == TextEffectKind.FadeOut).Select(effect => effect.Start).DefaultIfEmpty(Sample).Min();

    /// <summary>화면이 그려질 때마다(모니터 주사율에 맞춰) 실제로 흐른 시간의 모습을 그린다.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var elapsed = _clock.Elapsed.TotalSeconds;
        if (elapsed < Sample)
        {
            ShowTime(elapsed);
            return;
        }

        StopPlaying();
        ShowTime(RestTime());
    }

    private void StopPlaying()
    {
        CompositionTarget.Rendering -= OnFrame;
        _clock.Reset();
        _play.Content = "▶ 재생";
    }

    /// <summary>미리보기와 재생 위치 표시를 그 시각으로 옮긴다.</summary>
    private void ShowTime(double seconds)
    {
        _time = Math.Clamp(seconds, 0, Sample);
        _loading = true;
        _seek.Value = _time;
        _loading = false;
        _clockText.Text = string.Create(CultureInfo.InvariantCulture, $"{_time:0.00} / {Sample:0.00}초");
        Render();
    }

    /// <summary>지금 칸들의 모양과 효과로 견본 글자를 그린다. 프레임마다 새 그림을 만들지 않고 화면에 걸린 비트맵에 바로 그린다.</summary>
    private void Render()
    {
        if (_loading) return;

        var style = ReadStyle();
        style.Size = 110;
        var clip = new TextClip { Text = "텍스트 미리보기", Range = new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(Sample)), Style = style, Effects = _effects };

        _bitmap.Lock();
        using (var surface = SKSurface.Create(new SKImageInfo(_bitmap.PixelWidth, _bitmap.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul), _bitmap.BackBuffer, _bitmap.BackBufferStride))
        {
            surface.Canvas.Clear(new SKColor(0x3A, 0x3F, 0x48)); // 흰 글자도 검은 글자도 보이는 중간 밝기
            surface.Canvas.Scale(_zoom);
            TextRenderer.Draw(surface.Canvas, clip, SKRect.Create(_frame.Width, _frame.Height), TimeSpan.FromSeconds(_time));
        }

        _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
        _bitmap.Unlock();
    }
}
