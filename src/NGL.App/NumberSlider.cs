using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NGL.App;

/// <summary>
/// 숫자 칸과 같은 값(글로 된 숫자)을 끌어서 바꾸는 슬라이더. 칸에 슬라이더 범위를 넘는 값을 적어도 되고,
/// 그때 슬라이더는 끝에 머문다. 끄는 동안의 변화는 실행 취소 한 단계로 묶인다.
/// </summary>
public sealed class NumberSlider : Slider
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NumberSlider),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberSlider)d).ShowText()));

    private bool _showing; // 값을 받아서 보여주는 중 (사용자가 움직인 것이 아님)

    public NumberSlider()
    {
        SetResourceReference(StyleProperty, typeof(Slider)); // 파생 클래스에도 공통 슬라이더 모양을 쓴다
        Focusable = false;
        IsMoveToPointEnabled = true;
        IsSnapToTickEnabled = true;
        TickFrequency = 1;
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => (DataContext as MainViewModel)?.BeginLiveEdit()));
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => (DataContext as MainViewModel)?.CommitLiveEdit()));
    }

    /// <summary>숫자 칸과 같은 속성에 묶는다.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static double? Parse(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) ? number : null;

    private void ShowText()
    {
        // 값이 없으면(선택한 클립이 없으면) 움직일 수 없게 한다.
        IsEnabled = Parse(Text) is not null;
        if (Parse(Text) is not { } number) return;

        _showing = true;
        Value = number;
        _showing = false;
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        // 값이 아직 없을 때(화면을 만드는 중이거나 선택한 클립이 없을 때)는 쓰지 않는다.
        if (_showing || Parse(Text) is not { } current || current == newValue) return;
        // SetCurrentValue라야 묶어 둔 속성과의 연결이 끊기지 않는다.
        SetCurrentValue(TextProperty, newValue.ToString("0.##", CultureInfo.CurrentCulture));
    }
}
