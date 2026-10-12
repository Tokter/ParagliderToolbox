using System.Globalization;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>Simulated times for people: minutes and seconds, the minutes past an hour too (75:00, not 15:00).</summary>
internal static class Clock
{
    /// <summary>Gets <paramref name="seconds"/> as minutes:seconds.</summary>
    public static string Of(double seconds)
    {
        int whole = (int)Math.Max(0, Math.Round(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }
}
