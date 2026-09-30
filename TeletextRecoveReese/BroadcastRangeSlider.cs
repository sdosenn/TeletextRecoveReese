using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TeletextRecoveReese;

/// <summary>A compact two-thumb slider used to select an inclusive sample range.</summary>
public sealed class BroadcastRangeSlider : Control
{
    private const double ThumbRadius = 9;
    private bool _draggingLower;
    private bool _dragging;
    private int _minimum;
    private int _maximum;
    private int _lowerValue;
    private int _upperValue;

    public int Minimum
    {
        get => _minimum;
        set { _minimum = value; CoerceValues(); }
    }

    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(value, _minimum); CoerceValues(); }
    }

    public int LowerValue
    {
        get => _lowerValue;
        set => SetLowerValue(value, notify: false);
    }

    public int UpperValue
    {
        get => _upperValue;
        set => SetUpperValue(value, notify: false);
    }

    /// <summary>The bool is true when the lower/start thumb moved.</summary>
    public event Action<bool>? RangeChanged;

    public BroadcastRangeSlider()
    {
        Height = 42;
        MinWidth = 240;
        Focusable = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double left = ThumbRadius + 2;
        double right = Math.Max(left, Bounds.Width - ThumbRadius - 2);
        double y = Bounds.Height / 2;
        double lowerX = ValueToX(_lowerValue, left, right);
        double upperX = ValueToX(_upperValue, left, right);

        var trackPen = new Pen(new SolidColorBrush(Color.Parse("#66707A")), 4,
            lineCap: PenLineCap.Round);
        var selectedPen = new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 5,
            lineCap: PenLineCap.Round);
        context.DrawLine(trackPen, new Point(left, y), new Point(right, y));
        context.DrawLine(selectedPen, new Point(lowerX, y), new Point(upperX, y));

        DrawThumb(context, lowerX, y, _dragging && _draggingLower);
        DrawThumb(context, upperX, y, _dragging && !_draggingLower);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Point point = e.GetPosition(this);
        double left = ThumbRadius + 2;
        double right = Math.Max(left, Bounds.Width - ThumbRadius - 2);
        double lowerX = ValueToX(_lowerValue, left, right);
        double upperX = ValueToX(_upperValue, left, right);
        _draggingLower = Math.Abs(point.X - lowerX) <= Math.Abs(point.X - upperX);
        _dragging = true;
        e.Pointer.Capture(this);
        UpdateFromPointer(point.X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        UpdateFromPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    private void UpdateFromPointer(double x)
    {
        double left = ThumbRadius + 2;
        double right = Math.Max(left + 1, Bounds.Width - ThumbRadius - 2);
        double fraction = Math.Clamp((x - left) / (right - left), 0, 1);
        int value = (int)Math.Round(_minimum + fraction * (_maximum - _minimum));
        if (_draggingLower) SetLowerValue(value, notify: true);
        else SetUpperValue(value, notify: true);
    }

    private void SetLowerValue(int value, bool notify)
    {
        int coerced = Math.Clamp(value, _minimum, _upperValue);
        if (_lowerValue == coerced) return;
        _lowerValue = coerced;
        InvalidateVisual();
        if (notify) RangeChanged?.Invoke(true);
    }

    private void SetUpperValue(int value, bool notify)
    {
        int coerced = Math.Clamp(value, _lowerValue, _maximum);
        if (_upperValue == coerced) return;
        _upperValue = coerced;
        InvalidateVisual();
        if (notify) RangeChanged?.Invoke(false);
    }

    private void CoerceValues()
    {
        _lowerValue = Math.Clamp(_lowerValue, _minimum, _maximum);
        _upperValue = Math.Clamp(_upperValue, _lowerValue, _maximum);
        InvalidateVisual();
    }

    private double ValueToX(int value, double left, double right)
    {
        if (_maximum <= _minimum) return left;
        return left + (value - _minimum) * (right - left) / (_maximum - _minimum);
    }

    private static void DrawThumb(DrawingContext context, double x, double y, bool active)
    {
        IBrush fill = new SolidColorBrush(active
            ? Color.Parse("#FFFFFF")
            : Color.Parse("#E5E7EB"));
        var outline = new Pen(new SolidColorBrush(Color.Parse("#2563EB")), active ? 3 : 2);
        context.DrawEllipse(fill, outline, new Point(x, y), ThumbRadius, ThumbRadius);
    }
}
