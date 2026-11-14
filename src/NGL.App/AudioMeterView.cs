using System.Windows;
using System.Windows.Media;

namespace NGL.App;

/// <summary>재생 중인 소리의 좌·우 크기를 세로 막대 둘로 보여준다. 최대치를 넘으면 빨갛게 표시한다.</summary>
public sealed class AudioMeterView : FrameworkElement
{
    private const double FloorDecibels = -60; // 막대의 아래쪽 끝
    private const double BarWidth = 8;
    private const double Gap = 4;

    private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2C));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x7A));
    private static readonly Brush Clipping = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));

    private double _left;
    private double _right;

    /// <summary>화면을 그릴 때마다 부른다. 막대는 빨리 올라가고 천천히 내려온다.</summary>
    public void Refresh()
    {
        if (DataContext is not MainViewModel model) return;

        var (left, right) = model.ReadLevels();
        var (oldLeft, oldRight) = (_left, _right);
        _left = Math.Max(left, _left * 0.9);
        _right = Math.Max(right, _right * 0.9);
        if (_left < 1e-4) _left = 0;
        if (_right < 1e-4) _right = 0;
        if (_left != oldLeft || _right != oldRight) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(BarWidth * 2 + Gap, 0);

    protected override void OnRender(DrawingContext dc)
    {
        DrawBar(dc, _left, 0);
        DrawBar(dc, _right, BarWidth + Gap);
    }

    private void DrawBar(DrawingContext dc, double level, double left)
    {
        dc.DrawRoundedRectangle(Back, null, new Rect(left, 0, BarWidth, ActualHeight), 2, 2);
        if (level <= 0) return;

        // 소리 크기는 데시벨로 바꿔야 귀에 들리는 크기와 막대 길이가 비슷해진다.
        var decibels = 20 * Math.Log10(level);
        var height = ActualHeight * Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1);
        dc.DrawRoundedRectangle(level > 1 ? Clipping : Normal, null, new Rect(left, ActualHeight - height, BarWidth, height), 2, 2);
    }
}
