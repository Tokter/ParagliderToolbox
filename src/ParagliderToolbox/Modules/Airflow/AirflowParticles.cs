using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Airflow;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// Particles streaming with an airflow's mean wind, like streamers: short trails that fade, colored by the vertical wind
/// (lift red, sink blue), sped up so the flow reads at a glance. They start around <see cref="Height"/> above the ground
/// where the camera looks (<see cref="Focus"/>; from afar the whole terrain) and live a few seconds.
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
    private float _height = 300;

    // Where they start (around the focus) and where they live (twice as far around it), within the field.
    private Area _start, _alive;

    private readonly record struct Area(double X0, double Z0, double X1, double Z1)
    {
        public bool Contains(Vector3 p) => p.X >= X0 && p.X <= X1 && p.Z >= Z0 && p.Z <= Z1;
    }

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

    /// <summary>
    /// Gets or sets the height above the ground they start around (m): from half of it to one and a half times it, 10 m
    /// more either way, but at least a meter above the ground. Changing it starts them all again.
    /// </summary>
    public float Height
    {
        get => _height;
        set
        {
            if (value == _height) return;
            _height = value;
            if (_field != null) SpawnAll();
        }
    }

    /// <summary>
    /// Gets or sets where they start: a point on the ground (x, z) and how far around it (m), or <c>null</c> for the whole
    /// field. Those that stream twice as far away start again, so a close look has them all close together.
    /// </summary>
    public Func<(Vector2 Center, float Radius)>? Focus { get; set; }

    /// <summary>Streams through <paramref name="field"/> over the ground of <paramref name="ground"/>; <c>null</c> hides them.</summary>
    public void Show(AirflowField? field, Func<double, double, float>? ground)
    {
        bool fresh = _field is null || field is null || field.Nx != _field.Nx || field.Nz != _field.Nz;
        _field = field;
        _ground = ground;
        _instance.IsVisible = field != null;
        if (field is null || !fresh) return;
        SpawnAll();
    }

    /// <summary>Moves them on by <paramref name="dt"/> seconds; returns whether they stream (another frame is wanted).</summary>
    public bool Step(double dt)
    {
        if (_field is not { } field) return false;
        Locate(field);
        var wind = Wind ?? ((x, y, z) => field.WindAt(x, y, z));
        float step = (float)dt * SpeedUp;
        var alive = _alive;
        Parallel.For(0, Count, p =>
        {
            int head = p * Trail;
            _age[p] += (float)dt;
            var position = _positions[head];
            var air = wind(position.X, position.Y, position.Z);
            var next = position + air * step;
            if (_age[p] > _life[p] || air == Vector3.Zero || !alive.Contains(next))
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

    private void SpawnAll()
    {
        Locate(_field!);
        for (int p = 0; p < Count; p++)
        {
            Spawn(p);
            // Spread over their lives, so they don't all start (and die) together.
            _age[p] = (float)_random.NextDouble() * _life[p];
        }
    }

    // The areas they start and live in: around the focus, or the whole field.
    private void Locate(AirflowField field)
    {
        var whole = new Area(field.X0, field.Z0, field.X0 + field.Nx * field.CellSize, field.Z0 + field.Nz * field.CellSize);
        _start = _alive = whole;
        if (Focus?.Invoke() is not (var center, var radius)) return;
        radius = Math.Max(radius, 4 * (float)field.CellSize);
        Area Around(float r) => new(Math.Max(whole.X0, center.X - r), Math.Max(whole.Z0, center.Y - r),
            Math.Min(whole.X1, center.X + r), Math.Min(whole.Z1, center.Y + r));
        var start = Around(radius);
        // A focus off the terrain: all of it.
        if (start.X0 >= start.X1 || start.Z0 >= start.Z1) return;
        _start = start;
        _alive = Around(2 * radius);
    }

    private void Spawn(int p)
    {
        var field = _field!;
        double x = _start.X0 + _random.NextDouble() * (_start.X1 - _start.X0);
        double z = _start.Z0 + _random.NextDouble() * (_start.Z1 - _start.Z0);
        float height = StartHeight(field, (float)_random.NextDouble());
        var position = new Vector3((float)x, AirflowScene.AirGround(field, _ground, x, z) + height, (float)z);
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

    // A height above the ground in the band around Height (r from 0 to 1).
    private float StartHeight(AirflowField field, float r)
    {
        float spread = 10 + _height / 2;
        float low = Math.Max(1, _height - spread), high = Math.Max(low, Math.Min(_height + spread, (float)(field.Top - field.Y0)));
        return low + r * (high - low);
    }
}
