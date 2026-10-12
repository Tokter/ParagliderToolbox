using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Simulation;

namespace ParagliderToolbox.Airflow.Convection;

/// <summary>
/// Follows the thermals and cumulus of a run that resolves them (cells of 75 m or finer) through frames of it, and
/// gathers the base flow: the mean wind of the air between them.
/// </summary>
/// <remarks>
/// <para>
/// Every frame, the vertical wind is averaged over 3 × 3 columns at every level; updrafts grow from the cells rising at
/// <see cref="SeedClimb"/> or more through those rising at <see cref="CoreClimb"/>, connected through their faces, and
/// count from <see cref="MinCells"/> cells and <see cref="MinDepth"/>. An updraft is a thermal when its air is warmer
/// (virtual potential temperature: the vapor lightens it, cloud water weighs it down) than the air in a ring
/// <see cref="Ring"/> cells wide around it at the same levels by <see cref="Buoyancy"/>, or when it holds cloud: air
/// forced up a ridge is cooler than the air beside it. Clouds are the columns holding cloud water, connected through
/// their sides and corners.
/// </para>
/// <para>
/// Both are followed from frame to frame by how their footprints overlap, the earlier one moved on by its own wind (and
/// widened by a column, so small ones aren't lost): the largest overlaps continue; a new one that overlaps one that
/// continued elsewhere split off it and belongs to its thermal (or cloud), one overlapping none rose on its own. A cloud
/// fed by a thermal reaches down to within two levels of the thermal's top, above or beside it.
/// </para>
/// <para>
/// The base flow averages, per cell, the frames in which no thermal is in it (or rises beside its core) and no cloud;
/// cells a thermal or cloud fills most of the time take the base flow around them.
/// </para>
/// </remarks>
public sealed class ConvectionCensus
{
    /// <summary>The climb an updraft grows from (m/s, over 3 × 3 columns): a thermal worth circling in.</summary>
    public const float SeedClimb = 2;

    /// <summary>The least climb of an updraft's cells (m/s, over 3 × 3 columns).</summary>
    public const float CoreClimb = 0.75f;

    /// <summary>The share of its seed's climb an updraft's core grows through (its cells above and below it only need <see cref="CoreClimb"/>).</summary>
    public const float CoreShare = 0.5f;

    /// <summary>How much warmer than the air around it an updraft's air is when it is a thermal (K).</summary>
    public const float Buoyancy = 0.05f;

    /// <summary>How deep a thermal grows at least, to be one a glider climbs in (m).</summary>
    public const double UsefulDepth = 300;

    /// <summary>How wide a thermal's core grows at least, to be one a glider circles in (m).</summary>
    public const double UsefulWidth = 150;

    /// <summary>The fewest cells of an updraft.</summary>
    public const int MinCells = 20;

    /// <summary>The least depth of an updraft (m).</summary>
    public const double MinDepth = 100;

    /// <summary>The width of the ring around an updraft its warmth is measured against (cells).</summary>
    public const int Ring = 3;

    // Within two cells of a thermal's core, air rising at this speed is still the thermal's (m/s).
    private const float Flank = 0.3f;
    private const int FlankCells = 2;

    // A cell is in the base flow's average when it is free this share of the frames; else the flow around it fills in.
    private const double FreeShare = 0.2;

    // Vapor and cloud water in the virtual potential temperature, against a reference temperature (K).
    private const float ThetaReference = 300;

    private readonly AirflowGrid _grid;
    private readonly int _nx, _ny, _nz, _columns, _cells, _top;
    private readonly double _h, _dy;
    private readonly float[] _density;
    private readonly int[] _label, _cellStamp, _columnStamp, _owner, _cloudLabel;
    private readonly bool[] _masked;
    private readonly float[] _smooth, _sumX, _sumY, _sumZ;
    private readonly ushort[] _count;
    private readonly int[] _cloudBase, _cloudTop, _cloudCells;
    private readonly float[] _cloudPath, _cloudWindX, _cloudWindZ;
    private readonly List<Track> _thermals = [], _clouds = [];
    private List<Updraft> _lastUpdrafts = [];
    private List<Cumulus> _lastClouds = [];
    private int _stamp;
    private double _first, _last;

    /// <summary>Initializes a census of a grid's flow over a sounding (its air's density weighs the clouds' water).</summary>
    public ConvectionCensus(AirflowGrid grid, Sounding sounding)
    {
        _grid = grid;
        (_nx, _ny, _nz) = (grid.Nx, grid.Ny, grid.Nz);
        _columns = _nx * _nz;
        _cells = grid.CellCount;
        _h = grid.CellSize;
        _dy = grid.LayerHeight;
        // Below the damping layer.
        _top = Math.Max(1, (int)(_ny * (1 - AirflowSetup.SpongeShare)));
        _density = new float[_ny];
        for (int j = 0; j < _ny; j++) _density[j] = (float)sounding.DensityAt(grid.LevelHeight(j));
        _label = new int[_cells];
        _cellStamp = new int[_cells];
        _masked = new bool[_cells];
        _smooth = new float[_cells];
        _sumX = new float[_cells];
        _sumY = new float[_cells];
        _sumZ = new float[_cells];
        _count = new ushort[_cells];
        _columnStamp = new int[_columns];
        _owner = new int[_columns];
        Array.Fill(_owner, -1);
        _cloudLabel = new int[_columns];
        _cloudBase = new int[_columns];
        _cloudTop = new int[_columns];
        _cloudCells = new int[_columns];
        _cloudPath = new float[_columns];
        _cloudWindX = new float[_columns];
        _cloudWindZ = new float[_columns];
    }

    /// <summary>Gets the frames added.</summary>
    public int Frames { get; private set; }

    /// <summary>Adds the flow of a moment (the cells of <see cref="IAirflowSolver.Snapshot(AirflowCells)"/>).</summary>
    /// <param name="time">The moment (s of simulated time), after the last one's.</param>
    /// <param name="cells">The flow then.</param>
    public void Add(double time, AirflowCells cells)
    {
        double dt = Frames > 0 ? time - _last : 0;
        if (Frames == 0) _first = time;
        var updrafts = FindUpdrafts(cells, time);
        var clouds = FindClouds(cells, time);
        Match(_lastUpdrafts, updrafts, dt, _thermals);
        Match(_lastClouds, clouds, dt, _clouds);
        foreach (var updraft in updrafts) updraft.Track!.Thermal.Add(updraft.Moment);
        foreach (var cloud in clouds) cloud.Track!.Cloud.Add(cloud.Moment);
        AddShapes(clouds, time);
        Link(updrafts, clouds);
        Accumulate(cells, updrafts);
        _lastUpdrafts = updrafts;
        _lastClouds = clouds;
        _last = time;
        Frames++;
    }

    #region Updrafts

    // The updrafts of a frame: connected cores, big enough, with their warmth against the air around them.
    private List<Updraft> FindUpdrafts(AirflowCells cells, double time)
    {
        var air = _grid.Air;
        float[] w = cells.Y;
        Array.Clear(_label);
        // The vertical wind over 3 × 3 columns at every level: a cell's own is noisy, a thermal's core spans several.
        Parallel.For(0, _columns, column =>
        {
            int i = column % _nx, k = column / _nx;
            for (int j = _grid.FirstLevel[column]; j < _top; j++)
            {
                int c = column * _ny + j;
                _smooth[c] = 0;
                if (air[c] <= 0) continue;
                float sum = 0;
                int n = 0;
                for (int b = Math.Max(0, k - 1); b <= Math.Min(_nz - 1, k + 1); b++)
                {
                    for (int a = Math.Max(0, i - 1); a <= Math.Min(_nx - 1, i + 1); a++)
                    {
                        int m = (b * _nx + a) * _ny + j;
                        if (air[m] <= 0) continue;
                        sum += w[m];
                        n++;
                    }
                }
                _smooth[c] = sum / n;
                if (_smooth[c] >= CoreClimb) _label[c] = -1;
            }
        });

        // The seeds, strongest first: each grows through its neighbors rising at half its own climb or more (its core), so
        // thermals side by side stay apart (a ridge's line of weak lift joins them all); seeds a stronger one took grow
        // no more.
        var seeds = new List<int>();
        for (int c = 0; c < _cells; c++)
        {
            if (_label[c] == -1 && _smooth[c] >= SeedClimb) seeds.Add(c);
        }
        seeds.Sort((a, b) => _smooth[b].CompareTo(_smooth[a]));
        var updrafts = new List<Updraft>();
        var stack = new Stack<int>();
        int label = 0, minLevels = (int)Math.Ceiling(MinDepth / _dy);
        int layer = _nx * _ny;
        foreach (int seed in seeds)
        {
            if (_label[seed] != -1) continue;
            label++;
            float least = Math.Max(CoreClimb, CoreShare * _smooth[seed]);
            var members = new List<int>();
            _label[seed] = label;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                members.Add(c);
                int j = c % _ny, column = c / _ny, i = column % _nx, k = column / _nx;
                if (j > 0) Visit(c - 1);
                if (j < _ny - 1) Visit(c + 1);
                if (i > 0) Visit(c - _ny);
                if (i < _nx - 1) Visit(c + _ny);
                if (k > 0) Visit(c - layer);
                if (k < _nz - 1) Visit(c + layer);
            }
            // Then up and down its own columns through the cells rising at CoreClimb: down to where it leaves the ground,
            // up to its top, where its climb is weaker than half the core's.
            int core = members.Count;
            for (int m = 0; m < core; m++)
            {
                int c = members[m], j = c % _ny, first = c - j;
                for (int below = c - 1; below >= first && _label[below] == -1; below--)
                {
                    _label[below] = label;
                    members.Add(below);
                }
                for (int above = c + 1; above < first + _top && _label[above] == -1; above++)
                {
                    _label[above] = label;
                    members.Add(above);
                }
            }
            int bottom = _ny, top = -1;
            foreach (int c in members)
            {
                bottom = Math.Min(bottom, c % _ny);
                top = Math.Max(top, c % _ny);
            }
            if (members.Count >= MinCells && top - bottom + 1 >= minLevels) updrafts.Add(Describe(members.ToArray(), bottom, top, cells, time));

            void Visit(int n)
            {
                if (_label[n] != -1 || _smooth[n] < least) return;
                _label[n] = label;
                stack.Push(n);
            }
        }
        return updrafts;
    }

    private float ThetaV(AirflowCells cells, int c) =>
        cells.Temperature[c] + ThetaReference * (0.608f * cells.Vapor[c] - cells.CloudWater[c]);

    private Updraft Describe(int[] members, int bottom, int top, AirflowCells cells, double time)
    {
        var air = _grid.Air;
        float[] w = cells.Y;
        int stamp = ++_stamp;
        var columns = new List<int>();
        var levelCells = new int[top - bottom + 1];
        double sumW = 0, sumX = 0, sumZ = 0, windX = 0, windZ = 0, warmth = 0;
        double rootX = 0, rootZ = 0, ground = 0, topX = 0, topZ = 0;
        int roots = 0, tops = 0;
        float peak = 0;
        bool cloudy = false;
        foreach (int c in members)
        {
            int j = c % _ny, column = c / _ny;
            double x = _grid.X0 + (column % _nx + 0.5) * _h, z = _grid.Z0 + (column / _nx + 0.5) * _h;
            float climb = w[c];
            sumW += climb;
            sumX += x * climb;
            sumZ += z * climb;
            windX += cells.X[c];
            windZ += cells.Z[c];
            peak = Math.Max(peak, climb);
            cloudy |= cells.CloudWater[c] > AirflowSetup.CloudThreshold;
            warmth += ThetaV(cells, c);
            levelCells[j - bottom]++;
            if (j == bottom)
            {
                rootX += x;
                rootZ += z;
                ground += _grid.GroundHeight[column];
                roots++;
            }
            if (j == top)
            {
                topX += x;
                topZ += z;
                tops++;
            }
            if (_columnStamp[column] != stamp)
            {
                _columnStamp[column] = stamp;
                columns.Add(column);
            }
        }

        // The air around it at its levels: a ring of cells that aren't in any core.
        double ring = 0;
        int ringCells = 0;
        foreach (int c in members)
        {
            int j = c % _ny, column = c / _ny, i = column % _nx, k = column / _nx;
            for (int b = Math.Max(0, k - Ring); b <= Math.Min(_nz - 1, k + Ring); b++)
            {
                for (int a = Math.Max(0, i - Ring); a <= Math.Min(_nx - 1, i + Ring); a++)
                {
                    int n = (b * _nx + a) * _ny + j;
                    if (_label[n] != 0 || air[n] <= 0 || _cellStamp[n] == stamp) continue;
                    _cellStamp[n] = stamp;
                    ring += ThetaV(cells, n);
                    ringCells++;
                }
            }
        }
        float excess = ringCells > 0 ? (float)(warmth / members.Length - ring / ringCells) : 0;

        // Its diameter: of a circle as large as its median level.
        Array.Sort(levelCells);
        double area = levelCells[levelCells.Length / 2] * _h * _h;
        int n2 = members.Length;
        // Its climb in the core: the mean of its stronger half.
        var climbs = Array.ConvertAll(members, c => w[c]);
        Array.Sort(climbs);
        double core = 0;
        for (int m = n2 / 2; m < n2; m++) core += climbs[m];
        core /= n2 - n2 / 2;
        var moment = new ThermalMoment(time,
            (float)(sumW > 0 ? sumX / sumW : rootX / roots), (float)(sumW > 0 ? sumZ / sumW : rootZ / roots),
            (float)(rootX / roots), (float)(rootZ / roots), (float)(ground / roots),
            (float)(_grid.Y0 + bottom * _dy), (float)(_grid.Y0 + (top + 1) * _dy), (float)(topX / tops), (float)(topZ / tops),
            (float)core, peak, (float)(2 * Math.Sqrt(area / Math.PI)), excess, cloudy, excess > Buoyancy || cloudy);
        return new Updraft
        {
            Cells = members, Columns = columns.ToArray(), WindX = (float)(windX / n2), WindZ = (float)(windZ / n2), Moment = moment,
        };
    }

    #endregion

    #region Clouds

    // The clouds of a frame: cloudy columns connected through their sides and corners.
    private List<Cumulus> FindClouds(AirflowCells cells, double time)
    {
        float[] water = cells.CloudWater;
        Parallel.For(0, _columns, column =>
        {
            int first = column * _ny, bottom = -1, top = -1, n = 0;
            float path = 0, wx = 0, wz = 0;
            for (int j = _grid.FirstLevel[column]; j < _ny; j++)
            {
                float q = water[first + j];
                if (q <= AirflowSetup.CloudThreshold) continue;
                if (bottom < 0) bottom = j;
                top = j;
                path += q * _density[j] * (float)_dy;
                wx += cells.X[first + j];
                wz += cells.Z[first + j];
                n++;
            }
            _cloudBase[column] = bottom;
            _cloudTop[column] = top;
            _cloudPath[column] = path;
            _cloudCells[column] = n;
            _cloudWindX[column] = wx;
            _cloudWindZ[column] = wz;
            _cloudLabel[column] = bottom >= 0 ? -1 : 0;
        });

        var clouds = new List<Cumulus>();
        var stack = new Stack<int>();
        int label = 0;
        for (int seed = 0; seed < _columns; seed++)
        {
            if (_cloudLabel[seed] != -1) continue;
            label++;
            _cloudLabel[seed] = label;
            stack.Push(seed);
            var members = new List<int>();
            while (stack.Count > 0)
            {
                int column = stack.Pop();
                members.Add(column);
                int i = column % _nx, k = column / _nx;
                for (int b = Math.Max(0, k - 1); b <= Math.Min(_nz - 1, k + 1); b++)
                {
                    for (int a = Math.Max(0, i - 1); a <= Math.Min(_nx - 1, i + 1); a++)
                    {
                        int n = b * _nx + a;
                        if (_cloudLabel[n] != -1) continue;
                        _cloudLabel[n] = label;
                        stack.Push(n);
                    }
                }
            }
            int bottom = _ny, top = 0, count = 0;
            double mass = 0, sumX = 0, sumZ = 0, windX = 0, windZ = 0;
            foreach (int column in members)
            {
                double x = _grid.X0 + (column % _nx + 0.5) * _h, z = _grid.Z0 + (column / _nx + 0.5) * _h;
                double m = _cloudPath[column] * _h * _h;
                mass += m;
                sumX += x * m;
                sumZ += z * m;
                bottom = Math.Min(bottom, _cloudBase[column]);
                top = Math.Max(top, _cloudTop[column]);
                windX += _cloudWindX[column];
                windZ += _cloudWindZ[column];
                count += _cloudCells[column];
            }
            double area = members.Count * _h * _h;
            var moment = new CloudMoment(time, (float)(mass > 0 ? sumX / mass : 0), (float)(mass > 0 ? sumZ / mass : 0),
                (float)(_grid.Y0 + bottom * _dy), (float)(_grid.Y0 + (top + 1) * _dy), (float)(2 * Math.Sqrt(area / Math.PI)), (float)(mass / 1000));
            clouds.Add(new Cumulus
            {
                Columns = members.ToArray(), WindX = (float)(windX / Math.Max(1, count)), WindZ = (float)(windZ / Math.Max(1, count)), Moment = moment,
            });
        }
        return clouds;
    }

    #endregion

    #region Following

    // Continues the earlier frame's blobs into this one's by the overlap of their footprints (the earlier moved on by its
    // wind and widened by a column); new ones start tracks of their own.
    private void Match<T>(List<T> previous, List<T> current, double dt, List<Track> tracks) where T : Blob
    {
        for (int b = 0; b < current.Count; b++)
        {
            foreach (int column in current[b].Columns) _owner[column] = b;
        }
        var overlaps = new Dictionary<(int Previous, int Current), int>();
        for (int p = 0; p < previous.Count; p++)
        {
            var blob = previous[p];
            int di = (int)Math.Round(blob.WindX * dt / _h), dk = (int)Math.Round(blob.WindZ * dt / _h);
            int stamp = ++_stamp;
            foreach (int column in blob.Columns)
            {
                int i = column % _nx + di, k = column / _nx + dk;
                for (int b = Math.Max(0, k - 1); b <= Math.Min(_nz - 1, k + 1); b++)
                {
                    for (int a = Math.Max(0, i - 1); a <= Math.Min(_nx - 1, i + 1); a++)
                    {
                        int n = b * _nx + a;
                        if (_columnStamp[n] == stamp) continue;
                        _columnStamp[n] = stamp;
                        if (_owner[n] >= 0) overlaps[(p, _owner[n])] = overlaps.GetValueOrDefault((p, _owner[n])) + 1;
                    }
                }
            }
        }
        var continued = new bool[previous.Count];
        // The earlier blob every new one overlaps most: the one it split off, when that one continued elsewhere.
        var parent = new int[current.Count];
        Array.Fill(parent, -1);
        foreach (var ((p, c), _) in overlaps.OrderByDescending(o => o.Value))
        {
            if (parent[c] < 0) parent[c] = p;
            if (continued[p] || current[c].Track != null) continue;
            continued[p] = true;
            current[c].Track = previous[p].Track;
        }
        for (int c = 0; c < current.Count; c++)
        {
            if (current[c].Track != null) continue;
            var track = new Track { Id = tracks.Count };
            track.Family = parent[c] >= 0 ? previous[parent[c]].Track!.Family : track;
            current[c].Track = track;
            tracks.Add(track);
        }
        foreach (var blob in current)
        {
            foreach (int column in blob.Columns) _owner[column] = -1;
        }
    }

    // Every cloud's columns at this frame, by their place around its center (its pieces' water-weighted), for its shape
    // over its life: when each was cloudy, its most water, its bottom and top.
    private void AddShapes(List<Cumulus> clouds, double time)
    {
        foreach (var family in clouds.GroupBy(c => c.Track!.Family))
        {
            double mass = 0, sumX = 0, sumZ = 0;
            int count = 0;
            foreach (var piece in family)
            {
                foreach (int column in piece.Columns)
                {
                    double m = _cloudPath[column];
                    mass += m;
                    sumX += (column % _nx) * m;
                    sumZ += (column / _nx) * m;
                    count++;
                }
            }
            if (count == 0) continue;
            double ci = mass > 0 ? sumX / mass : family.SelectMany(p => p.Columns).Average(c => c % _nx);
            double ck = mass > 0 ? sumZ / mass : family.SelectMany(p => p.Columns).Average(c => c / _nx);
            // The center column (a center half way between columns rounds one way for all of them).
            int centerI = (int)Math.Floor(ci + 0.5), centerK = (int)Math.Floor(ck + 0.5);
            var shape = family.Key.Shape ??= [];
            foreach (var piece in family)
            {
                foreach (int column in piece.Columns)
                {
                    var place = (column % _nx - centerI, column / _nx - centerK);
                    if (!shape.TryGetValue(place, out var cell)) shape[place] = cell = new ShapeColumn { First = time };
                    cell.Last = time;
                    cell.Path = Math.Max(cell.Path, _cloudPath[column]);
                    cell.Bottom = Math.Min(cell.Bottom, _cloudBase[column]);
                    cell.Top = Math.Max(cell.Top, _cloudTop[column]);
                }
            }
        }
    }

    // A cloud's shape for the file: its columns around its center from when to when in its life they were cloudy, their
    // bottom and top above its base and their most water.
    private CloudShape? Shape(Track family, double start, double duration, double interval)
    {
        if (family.Shape is not { Count: > 0 } columns) return null;
        int i0 = columns.Keys.Min(p => p.I), i1 = columns.Keys.Max(p => p.I), k0 = columns.Keys.Min(p => p.K), k1 = columns.Keys.Max(p => p.K);
        int width = i1 - i0 + 1, depth = k1 - k0 + 1, baseLevel = columns.Values.Min(c => c.Bottom);
        var appear = new byte[width * depth];
        var vanish = new byte[width * depth];
        var bottom = new byte[width * depth];
        var top = new byte[width * depth];
        var water = new byte[width * depth];
        byte Share(double time) => (byte)Math.Clamp(Math.Round((time - start) / duration * 255), 0, 255);
        byte Height(double meters) => (byte)Math.Clamp(Math.Round(meters / CloudShape.HeightStep), 0, 255);
        foreach (var ((i, k), cell) in columns)
        {
            int n = (k - k0) * width + (i - i0);
            appear[n] = Share(cell.First);
            vanish[n] = Math.Max(appear[n], Share(cell.Last + interval));
            bottom[n] = Height((cell.Bottom - baseLevel) * _dy);
            top[n] = Height((cell.Top + 1 - baseLevel) * _dy);
            water[n] = (byte)Math.Clamp(Math.Ceiling(cell.Path / CloudShape.WaterStep), 1, 255);
        }
        return new CloudShape(width, depth, (float)_h, (float)((i0 - 0.5) * _h), (float)((k0 - 0.5) * _h), appear, vanish, bottom, top, water);
    }

    // A cloud is fed by the thermals whose tops reach within two levels of its base, under it or beside it.
    private void Link(List<Updraft> updrafts, List<Cumulus> clouds)
    {
        foreach (var cloud in clouds)
        {
            int stamp = ++_stamp;
            foreach (int column in cloud.Columns)
            {
                int i = column % _nx, k = column / _nx;
                for (int b = Math.Max(0, k - 1); b <= Math.Min(_nz - 1, k + 1); b++)
                {
                    for (int a = Math.Max(0, i - 1); a <= Math.Min(_nx - 1, i + 1); a++) _columnStamp[b * _nx + a] = stamp;
                }
            }
            foreach (var updraft in updrafts)
            {
                if (!updraft.Moment.IsThermal || updraft.Moment.Top < cloud.Moment.Base - 2 * _dy) continue;
                if (!updraft.Columns.Any(c => _columnStamp[c] == stamp)) continue;
                var thermal = updraft.Track!;
                var cumulus = cloud.Track!;
                if (!cumulus.Links.Contains(thermal.Id)) cumulus.Links.Add(thermal.Id);
                if (!thermal.Links.Contains(cumulus.Id)) thermal.Links.Add(cumulus.Id);
            }
        }
    }

    #endregion

    #region Base flow

    // Adds the free cells' wind: those of no thermal (nor beside its core, rising) and no cloud.
    private void Accumulate(AirflowCells cells, List<Updraft> updrafts)
    {
        var air = _grid.Air;
        float[] w = cells.Y, water = cells.CloudWater;
        Array.Clear(_masked);
        foreach (var updraft in updrafts)
        {
            if (!updraft.Moment.IsThermal) continue;
            foreach (int c in updraft.Cells)
            {
                _masked[c] = true;
                int j = c % _ny, column = c / _ny, i = column % _nx, k = column / _nx;
                for (int b = Math.Max(0, k - FlankCells); b <= Math.Min(_nz - 1, k + FlankCells); b++)
                {
                    for (int a = Math.Max(0, i - FlankCells); a <= Math.Min(_nx - 1, i + FlankCells); a++)
                    {
                        int n = (b * _nx + a) * _ny + j;
                        if (w[n] > Flank) _masked[n] = true;
                    }
                }
            }
        }
        Parallel.For(0, _columns, column =>
        {
            int first = column * _ny;
            for (int j = _grid.FirstLevel[column]; j < _ny; j++)
            {
                int c = first + j;
                if (air[c] <= 0 || _masked[c] || water[c] > AirflowSetup.CloudThreshold || _count[c] == ushort.MaxValue) continue;
                _sumX[c] += cells.X[c];
                _sumY[c] += w[c];
                _sumZ[c] += cells.Z[c];
                _count[c]++;
            }
        });
    }

    /// <summary>
    /// Gets the base flow so far in the solver's cell layout (<see cref="AirflowGrid.Cell"/>): every cell's mean wind over
    /// the frames no thermal or cloud was in it; a cell they filled most of the time takes the base flow around it at its
    /// level, and where there is none around it, <paramref name="mean"/>'s wind. <c>null</c> before the first frame.
    /// </summary>
    public (float[] X, float[] Y, float[] Z)? BaseWind(AirflowCells mean)
    {
        if (Frames == 0) return null;
        var air = _grid.Air;
        int need = Math.Max(1, (int)Math.Ceiling(Frames * FreeShare));
        var x = new float[_cells];
        var y = new float[_cells];
        var z = new float[_cells];
        var known = new bool[_cells];
        Parallel.For(0, _cells, c =>
        {
            if (_count[c] < need) return;
            x[c] = _sumX[c] / _count[c];
            y[c] = _sumY[c] / _count[c];
            z[c] = _sumZ[c] / _count[c];
            known[c] = true;
        });

        // The others take their known neighbors' mean at their level, ring by ring outwards.
        var fillX = new float[_cells];
        var fillY = new float[_cells];
        var fillZ = new float[_cells];
        var filled = (bool[])known.Clone();
        for (int pass = 0; pass < 40; pass++)
        {
            var next = (bool[])filled.Clone();
            int added = 0;
            Parallel.For(0, _columns, () => 0, (column, _, count) =>
            {
                int i = column % _nx, k = column / _nx;
                for (int j = _grid.FirstLevel[column]; j < _ny; j++)
                {
                    int c = column * _ny + j;
                    if (filled[c] || air[c] <= 0) continue;
                    double sx = 0, sy = 0, sz = 0;
                    int n = 0;
                    for (int b = Math.Max(0, k - 1); b <= Math.Min(_nz - 1, k + 1); b++)
                    {
                        for (int a = Math.Max(0, i - 1); a <= Math.Min(_nx - 1, i + 1); a++)
                        {
                            int m = (b * _nx + a) * _ny + j;
                            if (!filled[m]) continue;
                            sx += known[m] ? x[m] : fillX[m];
                            sy += known[m] ? y[m] : fillY[m];
                            sz += known[m] ? z[m] : fillZ[m];
                            n++;
                        }
                    }
                    if (n == 0) continue;
                    fillX[c] = (float)(sx / n);
                    fillY[c] = (float)(sy / n);
                    fillZ[c] = (float)(sz / n);
                    next[c] = true;
                    count++;
                }
                return count;
            }, count => Interlocked.Add(ref added, count));
            filled = next;
            if (added == 0) break;
        }

        // Cells free some of the time weigh their own mean against the fill.
        Parallel.For(0, _cells, c =>
        {
            if (known[c] || air[c] <= 0) return;
            float fx = filled[c] ? fillX[c] : mean.X[c], fy = filled[c] ? fillY[c] : mean.Y[c], fz = filled[c] ? fillZ[c] : mean.Z[c];
            int own = _count[c];
            x[c] = (_sumX[c] + (need - own) * fx) / need;
            y[c] = (_sumY[c] + (need - own) * fy) / need;
            z[c] = (_sumZ[c] + (need - own) * fz) / need;
        });
        return (x, y, z);
    }

    #endregion

    #region Climate

    /// <summary>Gets the thermals' and clouds' lives so far, and the sites the thermals rise from.</summary>
    /// <remarks>
    /// A thermal and the pieces that split off it are one thermal, its strongest piece standing for it at every frame (a
    /// thermal pulses: its bubbles part and rise on their own); a cloud and its pieces one cloud, their water and area
    /// added up. Those seen in fewer than two frames are left out, and updrafts that were thermals less than half the time.
    /// </remarks>
    public ConvectionClimate Climate()
    {
        double interval = Frames > 1 ? (_last - _first) / (Frames - 1) : 30;
        bool Cut(double start, double end) => start <= _first + 1e-6 || end >= _last - 1e-6;

        var cloudOf = new Dictionary<Track, int>();
        var cloudLinks = new List<IEnumerable<int>>();
        var clouds = new List<CloudLife>();
        var cloudMoments = new List<List<CloudMoment>>();
        var thermalMoments = new List<List<ThermalMoment>>();
        foreach (var family in _clouds.GroupBy(t => t.Family))
        {
            var moments = family.SelectMany(t => t.Cloud).GroupBy(m => m.Time).OrderBy(g => g.Key).Select(Combine).ToList();
            if (moments.Count < 2) continue;
            cloudMoments.Add(moments);
            cloudOf[family.Key] = clouds.Count;
            cloudLinks.Add(family.SelectMany(t => t.Links));
            var largest = moments.MaxBy(m => m.Diameter);
            var wettest = moments.MaxBy(m => m.Water);
            double duration = moments[^1].Time - moments[0].Time + interval, span = moments[^1].Time - moments[0].Time;
            clouds.Add(new CloudLife(clouds.Count, moments[0].Time, duration, largest.X, largest.Z,
                moments.Min(m => m.Base), moments.Max(m => m.Top), largest.Diameter, wettest.Water, wettest.Time - moments[0].Time,
                -1, Cut(moments[0].Time, moments[^1].Time))
            {
                Shape = Shape(family.Key, moments[0].Time, duration, interval),
                DriftX = span > 0 ? (float)((moments[^1].X - moments[0].X) / span) : 0,
                DriftZ = span > 0 ? (float)((moments[^1].Z - moments[0].Z) / span) : 0,
            });
        }

        var thermalOf = new Dictionary<Track, int>();
        var thermals = new List<ThermalLife>();
        foreach (var family in _thermals.GroupBy(t => t.Family))
        {
            var all = family.SelectMany(t => t.Thermal).ToList();
            var moments = all.GroupBy(m => m.Time).OrderBy(g => g.Key).Select(g => g.MaxBy(m => m.Climb)).ToList();
            if (moments.Count < 2 || all.Count(m => m.IsThermal) * 2 < all.Count) continue;
            if (moments.Max(m => m.Top - m.Bottom) < UsefulDepth || moments.Max(m => m.Diameter) < UsefulWidth) continue;
            thermalMoments.Add(moments);
            thermalOf[family.Key] = thermals.Count;
            var first = moments[0];
            var last = moments[^1];
            double span = last.Time - first.Time;
            var deep = moments.Where(m => m.Top - m.Bottom >= 150).ToList();
            float leanX = deep.Count > 0 ? deep.Average(m => (m.TopX - m.RootX) / (m.Top - m.Bottom)) : 0;
            float leanZ = deep.Count > 0 ? deep.Average(m => (m.TopZ - m.RootZ) / (m.Top - m.Bottom)) : 0;
            int cloud = family.SelectMany(t => t.Links).Select(id => cloudOf.GetValueOrDefault(_clouds[id].Family, -1)).FirstOrDefault(id => id >= 0, -1);
            thermals.Add(new ThermalLife(thermals.Count, first.Time, span + interval, first.RootX, first.RootZ, first.Ground,
                moments.Average(m => m.Bottom - m.Ground), all.Max(m => m.Top), moments.Average(m => m.Climb), all.Max(m => m.Peak),
                moments.Average(m => m.Diameter), leanX, leanZ, span > 0 ? (float)((last.X - first.X) / span) : 0,
                span > 0 ? (float)((last.Z - first.Z) / span) : 0, cloud, Cut(first.Time, last.Time)));
        }
        // The clouds' first feeding thermal, now that both are numbered.
        for (int id = 0; id < clouds.Count; id++)
        {
            int thermal = cloudLinks[id].Select(t => thermalOf.GetValueOrDefault(_thermals[t].Family, -1)).FirstOrDefault(t => t >= 0, -1);
            if (thermal >= 0) clouds[id] = clouds[id] with { ThermalId = thermal };
        }

        double observed = Frames > 1 ? _last - _first : 0;
        return new ConvectionClimate
        {
            Start = _first, End = _last, Interval = interval, Area = _columns * _h * _h,
            Thermals = thermals, Clouds = clouds, Sites = Sites(thermals, observed),
            ThermalCycle = Cycle(thermalMoments.Where((_, i) => !thermals[i].Cut).Select(l => l.Select(m => (m.Time, (double)m.Climb)).ToList()), mean: true),
            CloudCycle = Cycle(cloudMoments.Where((_, i) => !clouds[i].Cut).Select(l => l.Select(m => (m.Time, (double)m.Water)).ToList()), mean: false),
        };
    }

    // How a quantity goes over a life, on average: in tenths of the lives (of four frames or more), against each life's
    // mean (or its most).
    private static float[] Cycle(IEnumerable<List<(double Time, double Value)>> lives, bool mean)
    {
        var sums = new double[ConvectionClimate.CycleSteps];
        var counts = new int[ConvectionClimate.CycleSteps];
        foreach (var life in lives)
        {
            if (life.Count < 4) continue;
            double scale = mean ? life.Average(m => m.Value) : life.Max(m => m.Value);
            if (scale <= 0) continue;
            double start = life[0].Time, span = life[^1].Time - start;
            foreach (var (time, value) in life)
            {
                int step = Math.Min(sums.Length - 1, (int)((time - start) / span * sums.Length));
                sums[step] += value / scale;
                counts[step]++;
            }
        }
        return sums.Select((s, i) => counts[i] > 0 ? (float)(s / counts[i]) : float.NaN).ToArray();
    }

    // A cloud's pieces at one frame as one: their water and area added up, centered by their water.
    private static CloudMoment Combine(IEnumerable<CloudMoment> pieces)
    {
        var list = pieces.ToList();
        if (list.Count == 1) return list[0];
        float water = list.Sum(m => m.Water);
        float x = water > 0 ? list.Sum(m => m.X * m.Water) / water : list.Average(m => m.X);
        float z = water > 0 ? list.Sum(m => m.Z * m.Water) / water : list.Average(m => m.Z);
        return new CloudMoment(list[0].Time, x, z, list.Min(m => m.Base), list.Max(m => m.Top), MathF.Sqrt(list.Sum(m => m.Diameter * m.Diameter)), water);
    }

    // The sites: where the thermals came from, by the peaks of their density (smoothed over 150 m); each thermal goes to
    // the nearest peak within 500 m.
    private List<ThermalSite> Sites(List<ThermalLife> own, double observed)
    {
        if (own.Count == 0 || observed <= 0) return [];
        var density = new float[_columns];
        foreach (var t in own) density[Column(t.X, t.Z)]++;
        int radius = Math.Max(1, (int)Math.Round(150 / _h));
        var smooth = Maps.Smooth(density, _nx, _nz, radius);
        int reach = Math.Max(1, (int)Math.Round(300 / _h));
        var peaks = new List<(int Column, float Density)>();
        for (int column = 0; column < _columns; column++)
        {
            if (smooth[column] <= 0) continue;
            int i = column % _nx, k = column / _nx;
            if (Maps.IsPeak(smooth, _nx, _nz, i, k, reach)) peaks.Add((column, smooth[column]));
        }
        var members = peaks.Select(_ => new List<ThermalLife>()).ToList();
        foreach (var t in own)
        {
            int best = -1;
            double nearest = 500 * 500;
            for (int p = 0; p < peaks.Count; p++)
            {
                double px = _grid.X0 + (peaks[p].Column % _nx + 0.5) * _h, pz = _grid.Z0 + (peaks[p].Column / _nx + 0.5) * _h;
                double d = (px - t.X) * (px - t.X) + (pz - t.Z) * (pz - t.Z);
                if (d >= nearest) continue;
                nearest = d;
                best = p;
            }
            if (best >= 0) members[best].Add(t);
        }
        var sites = new List<ThermalSite>();
        for (int p = 0; p < peaks.Count; p++)
        {
            var list = members[p];
            if (list.Count < 2) continue;
            var whole = list.Where(t => !t.Cut).ToList();
            int born = list.Count(t => t.Start > _first);
            sites.Add(new ThermalSite(list.Average(t => t.X), list.Average(t => t.Z), list.Average(t => t.Ground), born / observed * 3600,
                ConvectionClimate.Median((whole.Count > 0 ? whole : list).Select(t => t.Duration)), list.Average(t => t.Climb), list.Average(t => t.Peak),
                list.Average(t => t.Diameter), list.Average(t => t.Top), (float)list.Count(t => t.CloudId >= 0) / list.Count,
                list.Average(t => t.LeanX), list.Average(t => t.LeanZ), list.Count));
        }
        return sites.OrderByDescending(s => s.Rate).ThenByDescending(s => s.Count).ToList();
    }

    private int Column(double x, double z) =>
        Math.Clamp((int)((z - _grid.Z0) / _h), 0, _nz - 1) * _nx + Math.Clamp((int)((x - _grid.X0) / _h), 0, _nx - 1);

    #endregion

    // A frame's updraft or cloud, followed by its track.
    private abstract class Blob
    {
        public int[] Columns = [];
        public float WindX, WindZ;
        public Track? Track;
    }

    private sealed class Updraft : Blob
    {
        public int[] Cells = [];
        public ThermalMoment Moment;
    }

    private sealed class Cumulus : Blob
    {
        public CloudMoment Moment;
    }

    // A blob followed through the frames; pieces that split off belong to the family of the one they split off.
    private sealed class Track
    {
        public int Id;
        public Track Family = null!;
        public readonly List<ThermalMoment> Thermal = [];
        public readonly List<CloudMoment> Cloud = [];
        public readonly List<int> Links = [];
        // A cloud family's columns over its life, by their place around its center (cells east and south).
        public Dictionary<(int I, int K), ShapeColumn>? Shape;
    }

    // An updraft at one frame: its weighted center, its root (the lowest level's center) and the ground there, its bottom
    // and top (m above sea level) and the top level's center, its mean and strongest climb, diameter and warmth.
    private readonly record struct ThermalMoment(double Time, float X, float Z, float RootX, float RootZ, float Ground, float Bottom, float Top,
        float TopX, float TopZ, float Climb, float Peak, float Diameter, float Warmth, bool Cloudy, bool IsThermal);

    // A column of a cloud's shape: when it was first and last cloudy, its most water (kg/m²), its lowest and highest level.
    private sealed class ShapeColumn
    {
        public double First, Last;
        public float Path;
        public int Bottom = int.MaxValue, Top = -1;
    }

    // A cloud at one frame: its center (by water), base, top, diameter and water (t).
    private readonly record struct CloudMoment(double Time, float X, float Z, float Base, float Top, float Diameter, float Water);
}
