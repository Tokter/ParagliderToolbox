using System.Globalization;
using Atelier.Core.Inspection;
using CommunityToolkit.Mvvm.ComponentModel;
using ParagliderToolbox.Paraglider.Polar;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The editable settings of a polar recording, shown in the dialog before it starts: how finely the speed bar and the
/// brakes are stepped, how long each setting is flown and sampled, and when a setting counts as steady or stalled.
/// </summary>
/// <remarks>Converts to and from the UI-free <see cref="PolarRecorderSettings"/> (<see cref="FromSettings"/>, <see cref="ToSettings"/>).</remarks>
[Inspectable]
public sealed partial class PolarRecorderOptions : ObservableObject
{
    /// <summary>The categories, in display order.</summary>
    public static readonly string[] CategoryOrder = ["Sweep", "Timing", "Detection", "Estimate"];

    private static readonly string[] Derived = [nameof(SpeedBarSettings), nameof(BrakeSettings), nameof(SamplesPerSetting), nameof(FlightTime)];

    private int _speedBarSteps = 4;
    private int _brakeSteps = 10;
    private double _maxBrake = 1;
    private double _startSeconds = 12;
    private double _rampSeconds = 1.5;
    private double _settleSeconds = 8;
    private double _measureSeconds = 8;
    private double _samplesPerSecond = 4;
    private double _stallAngle = 22;
    private double _stableSpread = 0.6;

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return false;
        foreach (string derived in Derived) OnPropertyChanged(derived);
        return true;
    }

    #region Sweep

    [InspectableProperty("Speed bar steps", "Sweep", Order = 1,
        Description = "Settings flown between trim and full speed bar, evenly spaced: 4 flies 25, 50, 75 and 100%; 0 skips the speed bar.")]
    public int SpeedBarSteps { get => _speedBarSteps; set => Set(ref _speedBarSteps, Math.Clamp(value, 0, 50)); }

    [InspectableProperty("Brake steps", "Sweep", Order = 2,
        Description = "Symmetric brake settings flown after the speed bar, evenly spaced up to the maximum brake: 10 steps of 10% by default. The sweep ends at the first stalled setting.")]
    public int BrakeSteps { get => _brakeSteps; set => Set(ref _brakeSteps, Math.Clamp(value, 0, 100)); }

    [InspectableProperty("Maximum brake", "Sweep", Order = 3, Description = "The last brake setting (0.1–1, 1 = full brakes).")]
    public double MaxBrake { get => _maxBrake; set => Set(ref _maxBrake, Math.Clamp(value, 0.1, 1)); }

    [InspectableProperty("Speed bar settings", "Sweep", Order = 4, IsReadOnly = true)]
    public string SpeedBarSettings => Describe(PolarRecorderSettings.EvenSteps(_speedBarSteps));

    [InspectableProperty("Brake settings", "Sweep", Order = 5, IsReadOnly = true)]
    public string BrakeSettings => Describe(PolarRecorderSettings.EvenSteps(_brakeSteps, (float)_maxBrake));

    private static string Describe(float[] steps) => steps.Length switch
    {
        0 => "none",
        1 => PolarRecorderSettings.Percent(steps[0]),
        2 => $"{PolarRecorderSettings.Percent(steps[0])}, {PolarRecorderSettings.Percent(steps[1])}",
        _ => $"{PolarRecorderSettings.Percent(steps[0])}, {PolarRecorderSettings.Percent(steps[1])} … {PolarRecorderSettings.Percent(steps[^1])}",
    };

    #endregion

    #region Timing

    [InspectableProperty("Start (s)", "Timing", Order = 10, Description = "Extra time in trim at the start, for the launch transient to die out.")]
    public double StartSeconds { get => _startSeconds; set => Set(ref _startSeconds, Math.Clamp(value, 0, 120)); }

    [InspectableProperty("Ramp (s)", "Timing", Order = 11, Description = "Time the controls take to move to the next setting, like a pilot's hands.")]
    public double RampSeconds { get => _rampSeconds; set => Set(ref _rampSeconds, Math.Clamp(value, 0, 30)); }

    [InspectableProperty("Settle (s)", "Timing", Order = 12,
        Description = "Time after the ramp before measuring, for the pitch oscillation to die out. Increase it if points come out unsteady.")]
    public double SettleSeconds { get => _settleSeconds; set => Set(ref _settleSeconds, Math.Clamp(value, 0, 300)); }

    [InspectableProperty("Measure (s)", "Timing", Order = 13,
        Description = "Time the airspeed and sink rate are averaged over at each setting; longer averages out slow oscillations.")]
    public double MeasureSeconds { get => _measureSeconds; set => Set(ref _measureSeconds, Math.Clamp(value, 0.5, 300)); }

    [InspectableProperty("Samples per second", "Timing", Order = 14,
        Description = "How often the continuous samples are stored (plotted and exported as CSV), 1–60. The averages use every simulation step (60 per second) regardless.")]
    public double SamplesPerSecond { get => _samplesPerSecond; set => Set(ref _samplesPerSecond, Math.Clamp(value, 1, 60)); }

    #endregion

    #region Detection

    [InspectableProperty("Stall angle of attack (°)", "Detection", Order = 20,
        Description = "A setting counts as stalled when the mean center angle of attack exceeds this (or it peaks 15° above it, or cells deflate).")]
    public double StallAngle { get => _stallAngle; set => Set(ref _stallAngle, Math.Clamp(value, 5, 60)); }

    [InspectableProperty("Steady spread (m/s)", "Detection", Order = 21,
        Description = "A setting counts as steady when the vertical speed's standard deviation while measuring is below this. Only steady points are used for the fitted curve.")]
    public double StableSpread { get => _stableSpread; set => Set(ref _stableSpread, Math.Clamp(value, 0.01, 10)); }

    #endregion

    #region Estimate

    [InspectableProperty("Samples per setting", "Estimate", Order = 30, IsReadOnly = true,
        Description = "Continuous samples stored while measuring each setting.")]
    public int SamplesPerSetting => (int)Math.Floor(_measureSeconds * _samplesPerSecond);

    [InspectableProperty("Flight time", "Estimate", Order = 31, IsReadOnly = true,
        Description = "Simulated flight time of the whole sweep. It is flown faster than real time, and ends early at the stall.")]
    public string FlightTime
    {
        get
        {
            var settings = ToSettings();
            int count = 1 + settings.SpeedBarSteps.Length + settings.BrakeSteps.Length;
            var time = TimeSpan.FromSeconds(settings.FlightSeconds);
            return string.Create(CultureInfo.CurrentCulture, $"up to {(int)time.TotalMinutes} min {time.Seconds:00} s, {count} settings");
        }
    }

    #endregion

    /// <summary>Returns the recorder settings.</summary>
    public PolarRecorderSettings ToSettings() => new()
    {
        SpeedBarSteps = PolarRecorderSettings.EvenSteps(_speedBarSteps),
        BrakeSteps = PolarRecorderSettings.EvenSteps(_brakeSteps, (float)_maxBrake),
        StartSeconds = (float)_startSeconds,
        RampSeconds = (float)_rampSeconds,
        SettleSeconds = (float)_settleSeconds,
        MeasureSeconds = (float)_measureSeconds,
        SampleInterval = (float)(1 / _samplesPerSecond),
        StallAngleOfAttack = (float)_stallAngle,
        StableSpread = (float)_stableSpread,
    };

    // The settings are floats: round so the grid shows 0.6, not 0.6000000238.
    private static double R(float value) => Math.Round(value, 3);

    /// <summary>Creates options from recorder settings (step counts and the maximum brake from the step lists).</summary>
    public static PolarRecorderOptions FromSettings(PolarRecorderSettings settings) => new()
    {
        SpeedBarSteps = settings.SpeedBarSteps.Length,
        BrakeSteps = settings.BrakeSteps.Length,
        MaxBrake = settings.BrakeSteps.Length > 0 ? R(settings.BrakeSteps.Max()) : 1,
        StartSeconds = R(settings.StartSeconds),
        RampSeconds = R(settings.RampSeconds),
        SettleSeconds = R(settings.SettleSeconds),
        MeasureSeconds = R(settings.MeasureSeconds),
        SamplesPerSecond = settings.SampleInterval > 0 ? Math.Round(1 / settings.SampleInterval, 2) : 4,
        StallAngle = R(settings.StallAngleOfAttack),
        StableSpread = R(settings.StableSpread),
    };
}
