using Atelier.Controls;
using Atelier.Core.Events;
using Atelier.Core.Primitives;
using Atelier.Core.Properties;
using Atelier.Rendering;
using Atelier.Theming;
using ParagliderToolbox.Framework.Infrastructure;
using ParagliderToolbox.Paraglider.Mathematics;
using SkiaSharp;

namespace ParagliderToolbox.Modules.Paraglider.Views;

/// <summary>
/// Shows and edits a <see cref="Curve"/>: drag the points (the first and last stay at x = 0 and 1), double-click to add
/// a point, right-click a point to remove it. With <see cref="IsReadOnly"/> it is a small preview.
/// </summary>
public sealed class CurveEditor : Control
{
    /// <summary>Identifies the <see cref="Curve"/> property.</summary>
    public static readonly BindableProperty<Curve?> CurveProperty =
        BindableProperty.Register<CurveEditor, Curve?>(nameof(Curve), null, (s, _, _) => ((CurveEditor)s).InvalidateVisual());

    /// <summary>Identifies the <see cref="IsReadOnly"/> property.</summary>
    public static readonly BindableProperty<bool> IsReadOnlyProperty =
        BindableProperty.Register<CurveEditor, bool>(nameof(IsReadOnly), false, (s, _, _) => ((CurveEditor)s).InvalidateVisual());

    private const float EdgePadding = 10;
    private int _dragging = -1;
    private int _hovered = -1;

    static CurveEditor() => ThemeExtensions.Register(theme => theme.Renderers.Register(new CurveEditorRenderer()));

    /// <summary>Initializes an editor.</summary>
    public CurveEditor()
    {
        IsFocusable = true;
        ClipToBounds = true;
    }

    /// <summary>Gets or sets the curve.</summary>
    public Curve? Curve { get => GetValue(CurveProperty); set => SetValue(CurveProperty, value); }

    /// <summary>Gets or sets whether the curve is only shown.</summary>
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    /// <summary>Occurs when the user changed the curve.</summary>
    public event EventHandler<Curve>? CurveEdited;

    /// <summary>Gets the point under the pointer, or −1.</summary>
    internal int HoveredPoint => _hovered;

    /// <summary>Gets the vertical range shown: the points' range with some margin, always including 0 and 1.</summary>
    internal (double Min, double Max) Range()
    {
        if (Curve is not { } curve) return (0, 1);
        double min = Math.Min(0, curve.Points.Min(p => p.Y));
        double max = Math.Max(1, curve.Points.Max(p => p.Y));
        double margin = (max - min) * 0.08;
        return (min - margin, max + margin);
    }

    internal Point ToScreen(CurvePoint p)
    {
        var (min, max) = Range();
        float pad = IsReadOnly ? 2 : EdgePadding;
        float w = Bounds.Width - 2 * pad, h = Bounds.Height - 2 * pad;
        return new Point(pad + (float)p.X * w, pad + (float)((max - p.Y) / (max - min)) * h);
    }

    private CurvePoint FromScreen(Point s)
    {
        var (min, max) = Range();
        float w = Bounds.Width - 2 * EdgePadding, h = Bounds.Height - 2 * EdgePadding;
        double x = Math.Clamp((s.X - EdgePadding) / w, 0, 1);
        double y = max - (s.Y - EdgePadding) / h * (max - min);
        return new CurvePoint(x, y);
    }

    private int HitPoint(Point position)
    {
        if (Curve is not { } curve) return -1;
        for (int i = 0; i < curve.Points.Count; i++)
        {
            var p = ToScreen(curve.Points[i]);
            if (Math.Abs(p.X - position.X) < 8 && Math.Abs(p.Y - position.Y) < 8) return i;
        }
        return -1;
    }

    /// <inheritdoc/>
    public override void OnPointerPressed(PointerEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsReadOnly || Curve is not { } curve) return;
        int hit = HitPoint(e.Position);
        if (e.Button == PointerButtons.Right && hit > 0 && hit < curve.Points.Count - 1)
        {
            Edit(curve.Points.Where((_, i) => i != hit).ToList());
            e.Handled = true;
            return;
        }
        if (e.Button != PointerButtons.Left) return;
        Focus();
        if (hit < 0 && e.ClickCount >= 2)
        {
            var p = FromScreen(e.Position);
            p = p with { Y = curve.Evaluate(p.X) };
            var points = curve.Points.Append(p).OrderBy(q => q.X).ToList();
            Edit(points);
            hit = points.IndexOf(p);
        }
        if (hit >= 0)
        {
            _dragging = hit;
            CapturePointer();
        }
        e.Handled = true;
    }

    /// <inheritdoc/>
    public override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsReadOnly || Curve is not { } curve) return;
        if (_dragging >= 0 && _dragging < curve.Points.Count)
        {
            var points = curve.Points.ToList();
            var p = FromScreen(e.Position);
            // The ends stay at x = 0 and 1; the others stay between their neighbors.
            double x = _dragging == 0 ? 0 : _dragging == points.Count - 1 ? 1
                : Math.Clamp(p.X, points[_dragging - 1].X + 0.005, points[_dragging + 1].X - 0.005);
            points[_dragging] = new CurvePoint(Math.Round(x, 4), Math.Round(p.Y, 4));
            Edit(points);
            e.Handled = true;
            return;
        }
        int hovered = HitPoint(e.Position);
        if (hovered != _hovered)
        {
            _hovered = hovered;
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    public override void OnPointerReleased(PointerEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging < 0) return;
        _dragging = -1;
        ReleasePointerCapture();
        e.Handled = true;
    }

    private void Edit(IReadOnlyList<CurvePoint> points)
    {
        var curve = new Curve(points);
        Curve = curve;
        CurveEdited?.Invoke(this, curve);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) =>
        new(float.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, IsReadOnly ? 28 : 220);
}

/// <summary>Draws a <see cref="CurveEditor"/>: grid, the curve, its points and the hovered point's values.</summary>
internal sealed class CurveEditorRenderer : ControlRenderer<CurveEditor>
{
    public override void Render(CurveEditor editor, ref DrawingContext context)
    {
        var colors = ToolboxTheme.Colors;
        var canvas = context.Canvas;
        float w = editor.Bounds.Width, h = editor.Bounds.Height;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

        fill.Color = ToSk(colors.SurfaceContainerLowest);
        canvas.DrawRoundRect(new SKRect(0, 0, w, h), 6, 6, fill);
        if (editor.Curve is not { } curve) return;

        var (min, max) = editor.Range();
        if (!editor.IsReadOnly)
        {
            // Grid every 0.1 in x and at the "nice" steps in y; 0 and 1 stronger.
            stroke.Color = ToSk(colors.OutlineVariant).WithAlpha(110);
            for (int i = 0; i <= 10; i++)
            {
                var a = editor.ToScreen(new CurvePoint(i / 10.0, min));
                var b = editor.ToScreen(new CurvePoint(i / 10.0, max));
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, stroke);
            }
            double step = NiceStep((max - min) / 5);
            for (double y = Math.Ceiling(min / step) * step; y <= max; y += step)
            {
                var a = editor.ToScreen(new CurvePoint(0, y));
                var b = editor.ToScreen(new CurvePoint(1, y));
                stroke.Color = ToSk(colors.OutlineVariant).WithAlpha(Math.Abs(y) < 1e-9 || Math.Abs(y - 1) < 1e-9 ? (byte)230 : (byte)110);
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, stroke);
                using var font = new SKFont(SKTypeface.Default, 10);
                fill.Color = ToSk(colors.OnSurfaceVariant);
                canvas.DrawText(y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), a.X + 3, a.Y - 2, SKTextAlign.Left, font, fill);
            }
        }

        // The curve, sampled finely.
        using var builder = new SKPathBuilder();
        for (int i = 0; i <= 200; i++)
        {
            double x = i / 200.0;
            var p = editor.ToScreen(new CurvePoint(x, curve.Evaluate(x)));
            if (i == 0) builder.MoveTo(p.X, p.Y);
            else builder.LineTo(p.X, p.Y);
        }
        using var path = builder.Detach();
        stroke.Color = ToSk(colors.Primary);
        stroke.StrokeWidth = editor.IsReadOnly ? 1.5f : 2.2f;
        canvas.DrawPath(path, stroke);

        if (editor.IsReadOnly) return;
        for (int i = 0; i < curve.Points.Count; i++)
        {
            var p = editor.ToScreen(curve.Points[i]);
            bool hovered = i == editor.HoveredPoint;
            fill.Color = ToSk(hovered ? colors.Tertiary : colors.Primary);
            canvas.DrawCircle(p.X, p.Y, hovered ? 6 : 4.5f, fill);
            stroke.Color = ToSk(colors.OnPrimary);
            stroke.StrokeWidth = 1.5f;
            canvas.DrawCircle(p.X, p.Y, hovered ? 6 : 4.5f, stroke);
            if (hovered)
            {
                using var font = new SKFont(SKTypeface.Default, 11);
                fill.Color = ToSk(colors.OnSurface);
                string text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{curve.Points[i].X:0.###}, {curve.Points[i].Y:0.####}");
                canvas.DrawText(text, Math.Min(p.X + 8, w - 90), Math.Max(p.Y - 8, 14), SKTextAlign.Left, font, fill);
            }
        }
    }

    private static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double n = raw / magnitude;
        return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * magnitude;
    }

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
}
