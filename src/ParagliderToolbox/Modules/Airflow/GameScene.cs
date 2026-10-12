using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Convection;
using ParagliderToolbox.Airflow.Sun;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// A game's sky in a terrain's scene: a <see cref="ConvectionModel"/>'s thermals as leaning tubes colored by their climb
/// now, its cumulus as puffy domes from their base (fainter while wisps), their shadows on the ground along the sun, and
/// the lift at the slice's height above the ground (the base flow with the thermals').
/// </summary>
public sealed class GameScene
{
    private const int Sides = 12;

    private readonly Mesh3D _thermals = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _clouds = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _shadows = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _slice = new(PrimitiveTopology.Triangles);
    private readonly MeshInstance3D _thermalInstance, _cloudInstance, _shadowInstance, _sliceInstance;

    // The slice: its points over the terrain, the base flow's vertical wind there, the colors.
    private Vector3[] _slicePoints = [];
    private float[] _baseLift = [];
    private Vector4[] _sliceColors = [];
    private uint[] _sliceIndices = [];
    private int[] _stamp = [];
    private int _stampValue, _sliceColumns, _sliceRows;
    private double _sliceHeight = double.NaN, _sliceStep;
    private AirflowField? _sliceField;

    /// <summary>Initializes the sky's meshes in <paramref name="scene"/>, hidden.</summary>
    public GameScene(Scene3D scene)
    {
        var see = Color.FromArgb(254, 255, 255, 255);
        _sliceInstance = new MeshInstance3D(_slice, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true }) { IsVisible = false };
        _shadowInstance = new MeshInstance3D(_shadows, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true }) { IsVisible = false };
        _thermalInstance = new MeshInstance3D(_thermals, new Material3D { BaseColor = see, Roughness = 0.9f, DoubleSided = true }) { IsVisible = false };
        _cloudInstance = new MeshInstance3D(_clouds, new Material3D { BaseColor = see, Roughness = 1, DoubleSided = true }) { IsVisible = false };
        scene.Instances.Add(_sliceInstance);
        scene.Instances.Add(_shadowInstance);
        scene.Instances.Add(_thermalInstance);
        scene.Instances.Add(_cloudInstance);
    }

    /// <summary>Hides everything.</summary>
    public void Clear() => _thermalInstance.IsVisible = _cloudInstance.IsVisible = _shadowInstance.IsVisible = _sliceInstance.IsVisible = false;

    /// <summary>Shows the model's sky as it is now.</summary>
    /// <param name="model">The model.</param>
    /// <param name="thermals">Whether the thermals show.</param>
    /// <param name="clouds">Whether the cumulus and their shadows show.</param>
    /// <param name="sliceHeight">The slice's height above the ground (m), or NaN for none.</param>
    /// <param name="ground">The terrain's height at a point (NaN outside).</param>
    public void Show(ConvectionModel model, bool thermals, bool clouds, double sliceHeight, Func<double, double, float> ground)
    {
        var field = model.Field;
        float Ground(double x, double z)
        {
            float h = ground(x, z);
            return float.IsNaN(h) ? field.GroundAt(x, z) : h;
        }
        Set(_thermalInstance, _thermals, thermals ? Thermals(model) : null);
        Set(_cloudInstance, _clouds, clouds ? Clouds(model) : null);
        var sun = new SunPosition(field.Info.SunAzimuth, field.Info.SunElevation);
        Set(_shadowInstance, _shadows, clouds && sun.IsUp ? Shadows(model, sun.Direction, Ground) : null);
        if (double.IsNaN(sliceHeight))
        {
            _sliceInstance.IsVisible = false;
            return;
        }
        Slice(model, sliceHeight, Ground);
    }

    private static void Set(MeshInstance3D instance, Mesh3D mesh, Geometry? geometry)
    {
        instance.IsVisible = geometry is { Indices.Count: > 0 };
        if (instance.IsVisible) mesh.SetGeometry([.. geometry!.Positions], [.. geometry.Indices], geometry.Normals.Count > 0 ? [.. geometry.Normals] : null, colors: [.. geometry.Colors]);
    }

    // A tube per thermal from its bottom to its top along its leaning axis, as wide as its core; its color its climb now,
    // fainter as it fades.
    private static Geometry Thermals(ConvectionModel model)
    {
        var mesh = new Geometry();
        foreach (var thermal in model.Thermals)
        {
            if (thermal.Strength <= 0.05f) continue;
            float bottom = thermal.Ground + thermal.Bottom;
            var low = thermal.AxisAt(bottom);
            var high = thermal.AxisAt(thermal.Top);
            float share = Math.Clamp(thermal.Strength / Math.Max(0.1f, thermal.Climb), 0, 1.2f);
            var color = new Vector4(AirflowColors.Lift.At(thermal.Strength), 0.06f + 0.22f * share);
            mesh.Tube(low, high, thermal.Diameter / 2, color);
        }
        return mesh;
    }

    // A cumulus as the run had it: a box per cloudy column from its bottom to its top, the denser the more water it holds
    // (as the run's film draws them). Without a recorded shape, a dome: flat at its base, as wide as it is now and as
    // high, with a few puffs on its top.
    private static Geometry Clouds(ConvectionModel model)
    {
        var mesh = new Geometry();
        foreach (var cloud in model.Clouds)
        {
            if (cloud.Shape is { } shape)
            {
                float half = shape.CellSize / 2;
                foreach (var column in cloud.Columns())
                {
                    var white = new Vector4(1, 1, 1, 0.12f + 0.6f * Math.Min(1, column.Water / 0.15f));
                    mesh.Box(new Vector3(column.X, (column.Bottom + column.Top) / 2, column.Z), new Vector3(half, (column.Top - column.Bottom) / 2, half), white);
                }
                continue;
            }
            float radius = Math.Max(15, cloud.DiameterNow / 2), height = Math.Max(20, cloud.TopNow - cloud.Base);
            var color = new Vector4(1, 1, 1, 0.55f + 0.45f * cloud.Share);
            var center = new Vector3(cloud.X0, cloud.Base, cloud.Z0);
            mesh.Dome(center, radius, height, color);
            // Puffs, the same for a cloud all its life (from when it formed).
            var random = new Random((int)(cloud.Born * 1000) ^ (int)cloud.StartX);
            for (int p = 0; p < 3; p++)
            {
                double angle = random.NextDouble() * Math.PI * 2, out_ = 0.25 + random.NextDouble() * 0.35;
                float r = radius * (0.35f + 0.25f * (float)random.NextDouble());
                var puff = center + new Vector3((float)(Math.Cos(angle) * out_ * radius), height * 0.35f, (float)(Math.Sin(angle) * out_ * radius));
                mesh.Dome(puff, r, height * (0.55f + 0.3f * (float)random.NextDouble()), color);
            }
        }
        return mesh;
    }

    // A cumulus' shadow on the ground, away from the sun by its height: a dark square under every column (the darker the
    // more water), or a disc under a dome.
    private static Geometry Shadows(ConvectionModel model, Vector3 toSun, Func<double, double, float> ground)
    {
        var mesh = new Geometry();
        if (toSun.Y <= 0.05f) return mesh;
        foreach (var cloud in model.Clouds)
        {
            if (cloud.Shape is { } shape)
            {
                float half = shape.CellSize / 2;
                foreach (var column in cloud.Columns())
                {
                    float height = column.Bottom - ground(column.X, column.Z);
                    float sx = column.X - toSun.X / toSun.Y * height, sz = column.Z - toSun.Z / toSun.Y * height;
                    var shade = new Vector4(0, 0, 0.05f, 0.45f * Math.Min(1, column.Water / 0.15f));
                    uint corner = (uint)mesh.Positions.Count;
                    foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                    {
                        float x = sx + a * half, z = sz + b * half;
                        mesh.Positions.Add(new Vector3(x, ground(x, z) + 4, z));
                        mesh.Colors.Add(shade);
                    }
                    mesh.Indices.AddRange([corner, corner + 1, corner + 2, corner, corner + 2, corner + 3]);
                }
                continue;
            }
            float radius = cloud.DiameterNow / 2;
            if (radius < 10) continue;
            float below = cloud.Base - ground(cloud.X0, cloud.Z0);
            float cx = cloud.X0 - toSun.X / toSun.Y * below, cz = cloud.Z0 - toSun.Z / toSun.Y * below;
            uint first = (uint)mesh.Positions.Count;
            var dark = new Vector4(0, 0, 0.05f, 0.4f * cloud.Share);
            mesh.Positions.Add(new Vector3(cx, ground(cx, cz) + 4, cz));
            mesh.Colors.Add(dark);
            for (int s = 0; s <= Sides * 2; s++)
            {
                double a = Math.PI * s / Sides;
                float x = cx + (float)Math.Cos(a) * radius, z = cz + (float)Math.Sin(a) * radius;
                mesh.Positions.Add(new Vector3(x, ground(x, z) + 4, z));
                mesh.Colors.Add(dark with { W = 0 });
            }
            for (uint s = 0; s < Sides * 2; s++) mesh.Indices.AddRange([first, first + 1 + s, first + 2 + s]);
        }
        return mesh;
    }

    // The lift at the slice's height: the base flow's everywhere (worked out once), the thermals' added near them.
    private void Slice(ConvectionModel model, double height, Func<double, double, float> ground)
    {
        var field = model.Field;
        if (_sliceField != field || height != _sliceHeight) Prepare(field, height, ground);
        Array.Copy(_baseColorsCache, _sliceColors, _sliceColors.Length);
        int stamp = ++_stampValue;
        foreach (var thermal in model.Thermals)
        {
            var axis = thermal.AxisAt(thermal.Ground + (float)height);
            float reach = thermal.Diameter / 2 * (thermal.Skirt > 0 ? 7.5f : 2.5f);
            int i0 = Math.Max(0, (int)((axis.X - reach - field.X0) / _sliceStep)), i1 = Math.Min(_sliceColumns - 1, (int)((axis.X + reach - field.X0) / _sliceStep) + 1);
            int k0 = Math.Max(0, (int)((axis.Z - reach - field.Z0) / _sliceStep)), k1 = Math.Min(_sliceRows - 1, (int)((axis.Z + reach - field.Z0) / _sliceStep) + 1);
            for (int k = k0; k <= k1; k++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    int n = k * _sliceColumns + i;
                    if (_stamp[n] == stamp) continue;
                    _stamp[n] = stamp;
                    var p = _slicePoints[n];
                    _sliceColors[n] = LiftColor(_baseLift[n] + model.LiftAt(p.X, p.Y, p.Z));
                }
            }
        }
        _slice.SetGeometry(_slicePoints, _sliceIndices, colors: _sliceColors);
        _sliceInstance.IsVisible = true;
    }

    private Vector4[] _baseColorsCache = [];

    private void Prepare(AirflowField field, double height, Func<double, double, float> ground)
    {
        _sliceField = field;
        _sliceHeight = height;
        _sliceStep = field.CellSize;
        _sliceColumns = field.Nx + 1;
        _sliceRows = field.Nz + 1;
        int count = _sliceColumns * _sliceRows;
        _slicePoints = new Vector3[count];
        _baseLift = new float[count];
        _baseColorsCache = new Vector4[count];
        _sliceColors = new Vector4[count];
        _stamp = new int[count];
        var points = _slicePoints;
        var lift = _baseLift;
        var colors = _baseColorsCache;
        int columns = _sliceColumns;
        Parallel.For(0, _sliceRows, k =>
        {
            for (int i = 0; i < columns; i++)
            {
                double x = field.X0 + i * field.CellSize, z = field.Z0 + k * field.CellSize;
                int n = k * columns + i;
                points[n] = new Vector3((float)x, ground(x, z) + Math.Max(AirflowScene.SliceClearance, (float)height), (float)z);
                lift[n] = field.BaseWindAt(x, AirflowScene.AirGround(field, ground, x, z) + height, z).Y;
                colors[n] = LiftColor(lift[n]);
            }
        });
        var indices = new uint[(_sliceColumns - 1) * (_sliceRows - 1) * 6];
        int t = 0;
        for (int k = 0; k + 1 < _sliceRows; k++)
        {
            for (int i = 0; i + 1 < _sliceColumns; i++)
            {
                uint v = (uint)(k * _sliceColumns + i), below = v + (uint)_sliceColumns;
                indices[t++] = v;
                indices[t++] = below;
                indices[t++] = v + 1;
                indices[t++] = v + 1;
                indices[t++] = below;
                indices[t++] = below + 1;
            }
        }
        _sliceIndices = indices;
    }

    private static Vector4 LiftColor(float lift) => new(AirflowColors.Lift.At(lift), 0.08f + 0.67f * Math.Min(1, Math.Abs(lift) / 2.5f));

    // Vertices, normals, colors and triangles.
    private sealed class Geometry
    {
        public readonly List<Vector3> Positions = [], Normals = [];
        public readonly List<Vector4> Colors = [];
        public readonly List<uint> Indices = [];

        // A tube from bottom to top (its rings level), fainter at the bottom.
        public void Tube(Vector3 bottom, Vector3 top, float radius, Vector4 color)
        {
            uint first = (uint)Positions.Count;
            foreach (var (center, alpha) in new[] { (bottom, color.W * 0.3f), (top, color.W) })
            {
                for (int s = 0; s <= Sides; s++)
                {
                    double a = 2 * Math.PI * s / Sides;
                    var normal = new Vector3((float)Math.Cos(a), 0, (float)Math.Sin(a));
                    Positions.Add(center + normal * radius);
                    Normals.Add(normal);
                    Colors.Add(color with { W = alpha });
                }
            }
            for (uint s = 0; s < Sides; s++)
            {
                uint a = first + s, b = a + Sides + 1;
                Indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }

        // A box around a center, half as large as size each way, lit by its faces.
        public void Box(Vector3 center, Vector3 size, Vector4 color)
        {
            foreach (var normal in s_faces)
            {
                var u = Math.Abs(normal.Y) > 0 ? Vector3.UnitX : Vector3.UnitY;
                var w = Vector3.Cross(normal, u);
                uint first = (uint)Positions.Count;
                foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    Positions.Add(center + (normal + u * a + w * b) * size);
                    Normals.Add(normal);
                    Colors.Add(color);
                }
                Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
        }

        private static readonly Vector3[] s_faces = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

        // The upper half of an ellipsoid over a disc: a cumulus' flat base and rounded top.
        public void Dome(Vector3 center, float radius, float height, Vector4 color)
        {
            const int rings = 5;
            uint first = (uint)Positions.Count;
            for (int r = 0; r <= rings; r++)
            {
                double up = Math.PI / 2 * r / rings;
                float across = (float)Math.Cos(up), rise = (float)Math.Sin(up);
                for (int s = 0; s <= Sides; s++)
                {
                    double a = 2 * Math.PI * s / Sides;
                    var direction = new Vector3((float)Math.Cos(a) * across, rise, (float)Math.Sin(a) * across);
                    Positions.Add(center + new Vector3(direction.X * radius, direction.Y * height, direction.Z * radius));
                    Normals.Add(Vector3.Normalize(new Vector3(direction.X / radius, direction.Y / height, direction.Z / radius)));
                    Colors.Add(color);
                }
            }
            for (uint r = 0; r < rings; r++)
            {
                for (uint s = 0; s < Sides; s++)
                {
                    uint a = first + r * (Sides + 1) + s, b = a + Sides + 1;
                    Indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
                }
            }
            // The flat base.
            uint middle = (uint)Positions.Count;
            Positions.Add(center);
            Normals.Add(-Vector3.UnitY);
            Colors.Add(color with { W = color.W * 0.8f });
            for (uint s = 0; s < Sides; s++) Indices.AddRange([middle, first + s + 1, first + s]);
        }
    }
}
