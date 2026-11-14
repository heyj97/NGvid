using System.Windows;
using System.Windows.Media;

namespace NGL.App;

/// <summary>재생 중인 소리의 좌·우 크기를 막대로 보여준다. 최대치를 넘으면 빨갛게 표시한다.</summary>
public sealed class AudioMeterView : FrameworkElement
{
    private const double FloorDecibels = -60; // 막대의 왼쪽 끝

    private static readonly Brush Back = Brushes.Black;
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x3F, 0xA8, 0x7A));
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

    protected override void OnRender(DrawingContext dc)
    {
        var barHeight = (ActualHeight - 2) / 2;
        DrawBar(dc, _left, 0, barHeight);
        DrawBar(dc, _right, barHeight + 2, barHeight);
    }

    private void DrawBar(DrawingContext dc, double level, double top, double height)
    {
        dc.DrawRectangle(Back, null, new Rect(0, top, ActualWidth, height));
        if (level <= 0) return;

        // 소리 크기는 데시벨로 바꿔야 귀에 들리는 크기와 막대 길이가 비슷해진다.
        var decibels = 20 * Math.Log10(level);
        var fraction = Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1);
        dc.DrawRectangle(level > 1 ? Clipping : Normal, null, new Rect(0, top, ActualWidth * fraction, height));
    }
}
