using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Airflow;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// Particles streaming with an airflow's mean wind, like streamers: short trails that fade, colored by the vertical wind
/// (lift red, sink blue), sped up so the flow reads at a glance. They start anywhere over the terrain at random heights
/// above the ground (more of them low, where pilots fly) and live a few seconds.
/// </summary>
public sealed class AirflowParticles
{
    private const int Count = 3000;
    private const int Trail = 10;

    // The time between a trail's points (s): long enough that a trail shows over a whole terrain.
    private const float TrailInterval = 0.08f;

    /// <summary>How much faster than the wind the particles move (a 10 km terrain in about a minute in a light wind).</summary>
    public const float SpeedUp = 60;

    private readonly Mesh3D _mesh = new(PrimitiveTopology.Lines);
    private readonly MeshInstance3D _instance;
    private readonly Vector3[] _positions = new Vector3[Count * Trail];
    private readonly Vector4[] _colors = new Vector4[Count * Trail];
    private readonly uint[] _indices = new uint[Count * (Trail - 1) * 2];
    private readonly float[] _age = new float[Count], _life = new float[Count], _sinceTrail = new float[Count];
    private readonly Random _random = new(7);
    private AirflowField? _field;
    private Func<double, double, float>? _ground;

    /// <summary>Initializes the particles in <paramref name="scene"/>, hidden.</summary>
    public AirflowParticles(Scene3D scene)
    {
        _instance = new MeshInstance3D(_mesh, new Material3D { BaseColor = Color.FromArgb(254, 255, 255, 255), Unlit = true }) { IsVisible = false };
        scene.Instances.Add(_instance);
        int n = 0;
        for (int p = 0; p < Count; p++)
        {
            for (int t = 0; t + 1 < Trail; t++)
            {
                _indices[n++] = (uint)(p * Trail + t);
                _indices[n++] = (uint)(p * Trail + t + 1);
            }
        }
    }

    /// <summary>Gets whether they stream.</summary>
    public bool IsShown => _field != null;

    /// <summary>Gets or sets the wind they ride instead of the field's mean (a game model's: the base flow with its thermals), or <c>null</c>.</summary>
    public Func<double, double, double, Vector3>? Wind { get; set; }

    /// <summary>Streams through <paramref name="field"/> over the ground of <paramref name="ground"/>; <c>null</c> hides them.</summary>
    public void Show(AirflowField? field, Func<double, double, float>? ground)
    {
        bool fresh = _field is null || field is null || field.Nx != _field.Nx || field.Nz != _field.Nz;
        _field = field;
        _ground = ground;
        _instance.IsVisible = field != null;
        if (field is null || !fresh) return;
        for (int p = 0; p < Count; p++)
        {
            Spawn(p);
            // Spread over their lives, so they don't all start (and die) together.
            _age[p] = (float)_random.NextDouble() * _life[p];
        }
    }

    /// <summary>Moves them on by <paramref name="dt"/> seconds; returns whether they stream (another frame is wanted).</summary>
    public bool Step(double dt)
    {
        if (_field is not { } field) return false;
        var wind = Wind ?? ((x, y, z) => field.WindAt(x, y, z));
        float step = (float)dt * SpeedUp;
        double x1 = field.X0 + field.Nx * field.CellSize, z1 = field.Z0 + field.Nz * field.CellSize;
        Parallel.For(0, Count, p =>
        {
            int head = p * Trail;
            _age[p] += (float)dt;
            var position = _positions[head];
            var air = wind(position.X, position.Y, position.Z);
            var next = position + air * step;
            bool gone = _age[p] > _life[p] || air == Vector3.Zero || next.X < field.X0 || next.X > x1 || next.Z < field.Z0 || next.Z > z1;
            if (gone)
            {
                lock (_random) Spawn(p);
                return;
            }
            // The head moves every frame; the trail keeps where it was every interval.
            _sinceTrail[p] += (float)dt;
            if (_sinceTrail[p] >= TrailInterval)
            {
                _sinceTrail[p] = 0;
                Array.Copy(_positions, head, _positions, head + 1, Trail - 1);
            }
            _positions[head] = next;
            var color = AirflowColors.Lift.At(air.Y);
            // Fading in and out over their lives, and along the trail.
            float life = Math.Min(1, Math.Min(_age[p] / 0.4f, (_life[p] - _age[p]) / 0.6f));
            for (int t = 0; t < Trail; t++) _colors[head + t] = new Vector4(color, life * (1 - (float)t / Trail));
        });
        _mesh.SetGeometry(_positions, _indices, colors: _colors);
        return true;
    }

    private void Spawn(int p)
    {
        var field = _field!;
        double x = field.X0 + _random.NextDouble() * field.Nx * field.CellSize;
        double z = field.Z0 + _random.NextDouble() * field.Nz * field.CellSize;
        double r = _random.NextDouble();
        float ground = _ground?.Invoke(x, z) ?? field.GroundAt(x, z);
        if (float.IsNaN(ground)) ground = field.GroundAt(x, z);
        var position = new Vector3((float)x, ground + 15 + (float)(r * r * 1800), (float)z);
        int head = p * Trail;
        for (int t = 0; t < Trail; t++)
        {
            _positions[head + t] = position;
            _colors[head + t] = default;
        }
        _age[p] = 0;
        _sinceTrail[p] = 0;
        _life[p] = 3 + (float)_random.NextDouble() * 4;
    }
}
