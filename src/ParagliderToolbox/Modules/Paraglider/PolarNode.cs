using System.Globalization;
using Atelier.Core.Inspection;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Paraglider.Polar;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// A recorded polar of a paraglider (see <see cref="PolarRecorder"/>): stored under the paraglider with the design it
/// was flown with, every sample and the steady points, so recordings can be compared and an optimizer can build on them.
/// The properties show the key figures; the detail view plots the curve.
/// </summary>
[Inspectable]
public partial class PolarNode : ProjectNode
{
    private PolarRecording _recording = new();

    /// <summary>Initializes an empty polar.</summary>
    public PolarNode()
    {
        Name = "Polar";
    }

    /// <summary>Gets or sets the recording.</summary>
    [InspectableIgnore]
    public PolarRecording Recording
    {
        get => _recording;
        set
        {
            if (!SetProperty(ref _recording, value ?? new PolarRecording())) return;
            foreach (string name in FigureNames) OnPropertyChanged(name);
        }
    }

    private static readonly string[] FigureNames =
    [
        nameof(Recorded), nameof(Trim), nameof(FullSpeed), nameof(MinSink), nameof(BestGlide), nameof(MinSpeed), nameof(MaxSink),
        nameof(SpeedRange), nameof(StallBrake), nameof(Complexity), nameof(SteadyPoints),
    ];

    private PolarSummary Summary => _recording.Summary;

    private static string Format(PolarValue value) =>
        value.Airspeed <= 0 ? "–" : string.Create(CultureInfo.CurrentCulture, $"{value.Airspeed * 3.6:0.0} km/h · {value.SinkRate:0.00} m/s · 1:{value.GlideRatio:0.0}");

    [InspectableProperty("Recorded", "Recording", Order = 1, IsReadOnly = true)]
    public string Recorded => _recording.Recorded.ToString("g", CultureInfo.CurrentCulture) + (_recording.IsComplete ? "" : " (canceled)");

    [InspectableProperty("Proxy", "Recording", Order = 2, IsReadOnly = true, Description = "The proxy complexity and node count it was simulated with.")]
    public string Complexity => $"{_recording.Design?.ProxyComplexity.ToString() ?? "?"}, {_recording.ProxyNodes} nodes";

    [InspectableProperty("Steady points", "Recording", Order = 3, IsReadOnly = true, Description = "Control settings where the glider flew steadily, of all flown.")]
    public string SteadyPoints => $"{_recording.Points.Count(p => p.IsStable)} of {_recording.Points.Count}";

    [InspectableProperty("Trim", "Polar", Order = 10, IsReadOnly = true, Description = "Hands up: no brakes, no speed bar.")]
    public string Trim => Format(Summary.Trim);

    [InspectableProperty("Full speed", "Polar", Order = 11, IsReadOnly = true, Description = "Full speed bar.")]
    public string FullSpeed => Format(Summary.FullSpeed);

    [InspectableProperty("Best glide", "Polar", Order = 12, IsReadOnly = true, Description = "Where the tangent from the origin touches the fitted curve.")]
    public string BestGlide => Format(Summary.BestGlide);

    [InspectableProperty("Min sink", "Polar", Order = 13, IsReadOnly = true, Description = "The lowest sink rate on the fitted curve.")]
    public string MinSink => Format(Summary.MinSink);

    [InspectableProperty("Min speed", "Polar", Order = 14, IsReadOnly = true, Description = "The slowest steady flight before the stall.")]
    public string MinSpeed => Format(Summary.MinSpeed);

    [InspectableProperty("Max sink", "Polar", Order = 15, IsReadOnly = true, Description = "The largest steady sink rate.")]
    public string MaxSink => Format(Summary.MaxSink);

    [InspectableProperty("Speed range (km/h)", "Polar", Order = 16, IsReadOnly = true, Description = "Full speed minus the slowest steady speed.")]
    public double SpeedRange => Math.Round(Summary.SpeedRange * 3.6, 1);

    [InspectableProperty("Stall at brakes", "Polar", Order = 17, IsReadOnly = true, Description = "The symmetric brake input the glider stalled at.")]
    public string StallBrake => Summary.StallBrake is { } brake ? brake.ToString("P0", CultureInfo.CurrentCulture) : "no stall";
}
