using System.Numerics;

namespace ParagliderToolbox.Airflow;

/// <summary>Streamlines through an <see cref="AirflowField"/>'s mean wind: the paths air (or a streamer) follows.</summary>
public static class Streamlines
{
    /// <summary>
    /// Traces the mean wind from <paramref name="start"/> (midpoint rule, steps of equal length) until
    /// <paramref name="length"/> meters, the ground, the grid's edges or still air.
    /// </summary>
    /// <param name="field">The airflow.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="length">The longest path (m).</param>
    /// <param name="step">The step (m).</param>
    /// <param name="backward">Whether to trace where the air came from instead.</param>
    /// <returns>The points, from the start; at least the start.</returns>
    public static List<Vector3> Trace(AirflowField field, Vector3 start, double length, double step, bool backward = false)
    {
        var points = new List<Vector3> { start };
        float direction = backward ? -1 : 1, h = (float)step;
        var p = start;
        double x1 = field.X0 + field.Nx * field.CellSize, z1 = field.Z0 + field.Nz * field.CellSize;
        for (double travelled = 0; travelled < length; travelled += step)
        {
            var v = Direction(field, p) * direction;
            if (v == Vector3.Zero) break;
            var mid = p + v * (h / 2);
            var w = Direction(field, mid) * direction;
            if (w == Vector3.Zero) break;
            p += w * h;
            if (p.X < field.X0 || p.X > x1 || p.Z < field.Z0 || p.Z > z1 || p.Y > field.Top) break;
            if (p.Y < field.GroundAt(p.X, p.Z)) break;
            points.Add(p);
        }
        return points;
    }

    // The wind's direction at a point, or zero where the air is (nearly) still.
    private static Vector3 Direction(AirflowField field, Vector3 p)
    {
        var wind = field.WindAt(p.X, p.Y, p.Z);
        float speed = wind.Length();
        return speed < 0.05f ? Vector3.Zero : wind / speed;
    }
}
