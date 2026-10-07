using System.Globalization;
using Atelier.Core.Inspection;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Paraglider.Simulation;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// A recorded flight of a paraglider (see <see cref="FlightRecording"/>): stored under the paraglider with the design it
/// was flown with, the proxy's motion and the pilot's inputs. The detail view replays it (pause, scrub, orbit) and
/// exports it as an animation; the inputs fly it again exactly in the simulator.
/// </summary>
[Inspectable]
public partial class RecordingNode : ProjectNode
{
    private FlightRecording _recording = new();

    /// <summary>Initializes an empty recording.</summary>
    public RecordingNode()
    {
        Name = "Flight";
    }

    /// <summary>Gets or sets the recording.</summary>
    [InspectableIgnore]
    public FlightRecording Recording
    {
        get => _recording;
        set
        {
            if (!SetProperty(ref _recording, value ?? new FlightRecording())) return;
            OnPropertyChanged(nameof(Recorded));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(Started));
            OnPropertyChanged(nameof(Complexity));
        }
    }

    [InspectableProperty("Recorded", "Recording", Order = 1, IsReadOnly = true)]
    public string Recorded => _recording.Recorded.ToString("g", CultureInfo.CurrentCulture);

    [InspectableProperty("Duration", "Recording", Order = 2, IsReadOnly = true, Description = "The recorded time and frames (30 a second).")]
    public string Duration => string.Create(CultureInfo.CurrentCulture, $"{_recording.Duration:0.0} s ({_recording.FrameCount} frames)");

    [InspectableProperty("Started at", "Recording", Order = 3, IsReadOnly = true,
        Description = "The simulated time the recording started at; the inputs from the start of the flight are kept, so it can be flown again exactly.")]
    public string Started => string.Create(CultureInfo.CurrentCulture, $"{_recording.FirstStep * _recording.StepTime:0.0} s into the flight");

    [InspectableProperty("Proxy", "Recording", Order = 4, IsReadOnly = true, Description = "The proxy complexity and node count it was simulated with.")]
    public string Complexity => $"{_recording.Design?.ProxyComplexity.ToString() ?? "?"}, {_recording.ProxyNodes} nodes";
}
