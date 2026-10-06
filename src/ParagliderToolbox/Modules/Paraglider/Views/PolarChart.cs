using System.Globalization;
using Atelier.Charts;
using Atelier.Core.Primitives;
using ParagliderToolbox.Paraglider.Polar;

namespace ParagliderToolbox.Modules.Paraglider.Views;

/// <summary>
/// Plots a polar on an <see cref="XYChart"/>: airspeed (km/h) against vertical speed (m/s, sinking downward), with the
/// continuous samples, the steady points, the fitted curve, the best glide tangent from the origin and labels on the key
/// figures (trim, full speed, best glide, min sink, min speed, max sink, stall). Also fills in live while recording.
/// </summary>
public sealed class PolarChart
{
    private const double Kmh = 3.6;

    private readonly XYChart _chart;
    private readonly XYSeries _samples = new("Samples")
    {
        ShowLine = false, Marker = ChartMarker.Circle, MarkerSize = 2.5f, Color = Color.FromArgb(70, 120, 144, 156),
    };
    private readonly XYSeries _steady = new("Steady points") { ShowLine = false, Marker = ChartMarker.Circle, MarkerSize = 7 };
    private readonly XYSeries _unsteady = new("Unsteady") { ShowLine = false, Marker = ChartMarker.Diamond, MarkerSize = 7, Color = Color.FromRgb(0xF9, 0xA8, 0x25) };
    private readonly XYSeries _stalled = new("Stalled") { ShowLine = false, Marker = ChartMarker.Triangle, MarkerSize = 8, Color = Color.FromRgb(0xE5, 0x39, 0x35) };
    private readonly XYSeries _fit = new("Polar (fitted)") { LineWidth = 2.5f };
    private readonly XYSeries _tangent = new("Best glide tangent") { LineStyle = ChartLineStyle.Dashed, LineWidth = 1.5f, Color = Color.FromRgb(0x43, 0xA0, 0x47) };

    /// <summary>Sets up <paramref name="chart"/> for a polar.</summary>
    public PolarChart(XYChart chart, string title)
    {
        _chart = chart;
        chart.Title = title;
        chart.XAxis.Title = "Airspeed (km/h)";
        chart.YAxis.Title = "Vertical speed (m/s)";
        chart.XAxis.IncludeZero = true;
        chart.YAxis.IncludeZero = true;
        chart.XAxis.LabelFormat = "0";
        chart.YAxis.LabelFormat = "0.0";
        chart.Series.Clear();
        chart.Annotations.Clear();
        foreach (var series in new[] { _samples, _tangent, _fit, _steady, _unsteady, _stalled }) chart.Series.Add(series);
    }

    /// <summary>Gets the chart.</summary>
    public XYChart Chart => _chart;

    /// <summary>Removes everything plotted.</summary>
    public void Clear()
    {
        foreach (var series in new[] { _samples, _steady, _unsteady, _stalled, _fit, _tangent }) series.Clear();
        _chart.Annotations.Clear();
        _chart.ResetView();
    }

    /// <summary>Adds a continuous sample (while recording).</summary>
    public void AddSample(PolarSample sample) => _samples.Add(sample.Airspeed * Kmh, sample.VerticalSpeed);

    /// <summary>Adds a measured point (while recording).</summary>
    public void AddPoint(PolarPoint point)
    {
        var series = point.IsStalled ? _stalled : point.IsStable ? _steady : _unsteady;
        series.Add(point.Airspeed * Kmh, -point.SinkRate);
    }

    /// <summary>Shows a whole recording: samples, points, the fitted curve, the tangent and the labels.</summary>
    public void Show(PolarRecording recording)
    {
        Clear();
        _samples.SetPoints(recording.Samples.Select(s => new ChartPoint(s.Airspeed * Kmh, s.VerticalSpeed)));
        foreach (var point in recording.Points) AddPoint(point);

        var summary = recording.Summary;
        var stable = recording.Points.Where(p => p.IsStable).ToList();
        if (summary.FitCoefficients.Length >= 2 && stable.Count >= 2)
        {
            double vMin = stable.Min(p => p.Airspeed), vMax = stable.Max(p => p.Airspeed);
            _fit.SetPoints(Enumerable.Range(0, 61).Select(i =>
            {
                double v = vMin + (vMax - vMin) * i / 60;
                return new ChartPoint(v * Kmh, -summary.FittedSink(v));
            }));
        }
        if (summary.BestGlide.Airspeed > 0)
        {
            // From the origin through the best glide point, a little beyond it.
            double scale = 1.25;
            _tangent.SetPoints([new ChartPoint(0, 0), new ChartPoint(summary.BestGlide.Airspeed * Kmh * scale, -summary.BestGlide.SinkRate * scale)]);
        }

        Label(summary.Trim, "Trim");
        Label(summary.FullSpeed, "Full speed bar");
        Label(summary.BestGlide, $"Best glide 1:{summary.BestGlide.GlideRatio:0.0}", Color.FromRgb(0x43, 0xA0, 0x47));
        Label(summary.MinSink, "Min sink");
        Label(summary.MinSpeed, "Min speed");
        if (summary.MaxSink.Airspeed != summary.FullSpeed.Airspeed) Label(summary.MaxSink, "Max sink");
        if (recording.Points.FirstOrDefault(p => p.IsStalled && p.Brake > 0) is { } stall)
        {
            _chart.Annotations.Add(new ChartAnnotation(stall.Airspeed * Kmh, -stall.SinkRate,
                string.Create(CultureInfo.CurrentCulture, $"Stall at {PolarRecorderSettings.Percent(stall.Brake)} brakes\n{stall.Airspeed * Kmh:0} km/h · −{stall.SinkRate:0.0} m/s"))
            {
                Color = Color.FromRgb(0xE5, 0x39, 0x35),
                Marker = ChartMarker.Triangle,
            });
        }
        _chart.ResetView();
    }

    private void Label(PolarValue value, string name, Color? color = null)
    {
        if (value.Airspeed <= 0) return;
        string text = string.Create(CultureInfo.CurrentCulture,
            $"{name}\n{value.Airspeed * Kmh:0.0} km/h · −{value.SinkRate:0.00} m/s · L/D {value.GlideRatio:0.0}");
        _chart.Annotations.Add(new ChartAnnotation(value.Airspeed * Kmh, -value.SinkRate, text) { Color = color });
    }
}
