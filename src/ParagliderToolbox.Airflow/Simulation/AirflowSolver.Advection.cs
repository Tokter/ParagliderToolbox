namespace ParagliderToolbox.Airflow.Simulation;

public sealed partial class AirflowSolver
{
    // MacCormack's work arrays: the first-order result and the bounds of what it interpolated, per quantity.
    private float[]? _hat0, _hat1, _hat2, _low0, _low1, _low2, _high0, _high1, _high2;

    // Fills the faces and cells inside the terrain from the air above them, so the advection's samples near the ground
    // see the flow (free slip) instead of standing air.
    private void Extrapolate()
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i <= nx; i++)
            {
                int first = _firstX[k * (nx + 1) + i];
                if (first <= 0 || first >= ny) continue;
                int start = grid.FaceX(i, 0, k);
                float value = _ux[start + first];
                for (int j = 0; j < first; j++) _ux[start + j] = value;
            }
            for (int i = 0; i < nx; i++)
            {
                int first = grid.FirstLevel[k * nx + i];
                if (first <= 0 || first >= ny) continue;
                int start = grid.Cell(i, 0, k);
                for (int j = 0; j < first; j++)
                {
                    _theta[start + j] = _theta[start + first];
                    _vapor[start + j] = _vapor[start + first];
                    _cloud[start + j] = _cloud[start + first];
                }
            }
        });
        Parallel.For(0, nz + 1, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                int first = _firstZ[k * nx + i];
                if (first <= 0 || first >= ny) continue;
                int start = grid.FaceZ(i, 0, k);
                float value = _uz[start + first];
                for (int j = 0; j < first; j++) _uz[start + j] = value;
            }
        });
    }

    private void Advect(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        int size = Math.Max(Math.Max(_ux.Length, _uy.Length), Math.Max(_uz.Length, grid.CellCount));
        if (_hat0 is null || _hat0.Length < size)
        {
            _hat0 = new float[size];
            _hat1 = new float[size];
            _hat2 = new float[size];
            _low0 = new float[size];
            _low1 = new float[size];
            _low2 = new float[size];
            _high0 = new float[size];
            _high1 = new float[size];
            _high2 = new float[size];
        }

        // Each quantity at its own positions (in cell units): X faces at (i, j + ½, k + ½) and so on.
        var x = new Staggered(_ux, nx + 1, ny, nz, 0, 0.5, 0.5);
        var y = new Staggered(_uy, nx, ny + 1, nz, 0.5, 0, 0.5);
        var z = new Staggered(_uz, nx, ny, nz + 1, 0.5, 0.5, 0);
        AdvectOne(x, grid.OpenX, _nextX, dt);
        AdvectOne(y, grid.OpenY, _nextY, dt);
        AdvectOne(z, grid.OpenZ, _nextZ, dt);
        AdvectScalars(dt);
        (_ux, _nextX) = (_nextX, _ux);
        (_uy, _nextY) = (_nextY, _uy);
        (_uz, _nextZ) = (_nextZ, _uz);
        (_theta, _nextTheta) = (_nextTheta, _theta);
        (_vapor, _nextVapor) = (_nextVapor, _vapor);
        (_cloud, _nextCloud) = (_nextCloud, _cloud);
    }

    private void AdvectOne(Staggered field, float[] open, float[] result, double dt)
    {
        var hat = _hat0!;
        var low = _low0!;
        var high = _high0!;
        int ni = field.Ni, nj = field.Nj, nk = field.Nk;
        // The first-order (semi-Lagrangian) step: the value where the air comes from.
        Parallel.For(0, nk, k =>
        {
            for (int i = 0; i < ni; i++)
            {
                for (int j = 0; j < nj; j++)
                {
                    int f = (k * ni + i) * nj + j;
                    if (open[f] <= 0)
                    {
                        hat[f] = field.Data[f];
                        continue;
                    }
                    var (bx, by, bz) = Trace(i + field.Ox, j + field.Oy, k + field.Oz, -dt);
                    hat[f] = field.Sample(bx, by, bz, out low[f], out high[f]);
                }
            }
        });
        // The error estimate: back from there with the first-order result; half of the difference corrects it, within
        // the bounds of what was interpolated.
        var corrected = new Staggered(hat, ni, nj, nk, field.Ox, field.Oy, field.Oz);
        Parallel.For(0, nk, k =>
        {
            for (int i = 0; i < ni; i++)
            {
                for (int j = 0; j < nj; j++)
                {
                    int f = (k * ni + i) * nj + j;
                    if (open[f] <= 0)
                    {
                        result[f] = 0;
                        continue;
                    }
                    var (fx, fy, fz) = Trace(i + field.Ox, j + field.Oy, k + field.Oz, dt);
                    float back = corrected.Sample(fx, fy, fz, out _, out _);
                    float value = hat[f] + 0.5f * (field.Data[f] - back);
                    result[f] = value < low[f] || value > high[f] ? hat[f] : value;
                }
            }
        });
    }

    private void AdvectScalars(double dt)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        var theta = new Staggered(_theta, nx, ny, nz, 0.5, 0.5, 0.5);
        var vapor = new Staggered(_vapor, nx, ny, nz, 0.5, 0.5, 0.5);
        var cloud = new Staggered(_cloud, nx, ny, nz, 0.5, 0.5, 0.5);
        float[] hat0 = _hat0!, hat1 = _hat1!, hat2 = _hat2!, low0 = _low0!, low1 = _low1!, low2 = _low2!, high0 = _high0!, high1 = _high1!, high2 = _high2!;
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_isAir[c])
                    {
                        hat0[c] = _theta[c];
                        hat1[c] = _vapor[c];
                        hat2[c] = _cloud[c];
                        continue;
                    }
                    var (bx, by, bz) = Trace(i + 0.5, j + 0.5, k + 0.5, -dt);
                    // The potential temperature is conserved, not its difference to the sounding: air that rises into
                    // warmer layers ends up colder than they are (that is what stable air does).
                    float shift = BackgroundShift(by, j);
                    hat0[c] = theta.Sample(bx, by, bz, out low0[c], out high0[c]) + shift;
                    low0[c] += shift;
                    high0[c] += shift;
                    hat1[c] = vapor.Sample(bx, by, bz, out low1[c], out high1[c]);
                    hat2[c] = cloud.Sample(bx, by, bz, out low2[c], out high2[c]);
                }
            }
        });
        var thetaHat = new Staggered(hat0, nx, ny, nz, 0.5, 0.5, 0.5);
        var vaporHat = new Staggered(hat1, nx, ny, nz, 0.5, 0.5, 0.5);
        var cloudHat = new Staggered(hat2, nx, ny, nz, 0.5, 0.5, 0.5);
        Parallel.For(0, nz, k =>
        {
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    int c = grid.Cell(i, j, k);
                    if (!_isAir[c])
                    {
                        _nextTheta[c] = _theta[c];
                        _nextVapor[c] = _vapor[c];
                        _nextCloud[c] = _cloud[c];
                        continue;
                    }
                    var (fx, fy, fz) = Trace(i + 0.5, j + 0.5, k + 0.5, dt);
                    float back = thetaHat.Sample(fx, fy, fz, out _, out _) + BackgroundShift(fy, j);
                    _nextTheta[c] = Correct(hat0[c], _theta[c], back, low0[c], high0[c]);
                    _nextVapor[c] = Math.Max(0, Correct(hat1[c], _vapor[c], vaporHat.Sample(fx, fy, fz, out _, out _), low1[c], high1[c]));
                    _nextCloud[c] = Math.Max(0, Correct(hat2[c], _cloud[c], cloudHat.Sample(fx, fy, fz, out _, out _), low2[c], high2[c]));
                }
            }
        });
    }

    // The sounding's potential temperature at a height in cell units, minus level j's.
    private float BackgroundShift(double y, int j)
    {
        double f = Math.Clamp(y - 0.5, 0, Grid.Ny - 1);
        int a = Math.Min((int)f, Grid.Ny - 2);
        double t = f - a;
        return (float)(_theta0[a] + t * (_theta0[a + 1] - _theta0[a]) - _theta0[j]);
    }

    private static float Correct(float hat, float original, float back, float low, float high)
    {
        float value = hat + 0.5f * (original - back);
        return value < low || value > high ? hat : value;
    }

    // Where the air at a position (cell units) is after dt (negative: was before), with the midpoint rule.
    private (double X, double Y, double Z) Trace(double x, double y, double z, double dt)
    {
        var (u, v, w) = VelocityInCells(x, y, z);
        double mx = x + 0.5 * dt * u, my = y + 0.5 * dt * v, mz = z + 0.5 * dt * w;
        (u, v, w) = VelocityInCells(mx, my, mz);
        return (x + dt * u, y + dt * v, z + dt * w);
    }

    // The velocity in cells per second at a position in cell units.
    private (double U, double V, double W) VelocityInCells(double x, double y, double z)
    {
        var grid = Grid;
        int nx = grid.Nx, ny = grid.Ny, nz = grid.Nz;
        double u = Staggered.Sample(_ux, nx + 1, ny, nz, x, y - 0.5, z - 0.5);
        double v = Staggered.Sample(_uy, nx, ny + 1, nz, x - 0.5, y, z - 0.5);
        double w = Staggered.Sample(_uz, nx, ny, nz + 1, x - 0.5, y - 0.5, z);
        return (u / grid.CellSize, v / grid.LayerHeight, w / grid.CellSize);
    }

    /// <summary>A field on the grid with its own staggering: index 0 sits at (Ox, Oy, Oz) in cell units.</summary>
    private readonly struct Staggered(float[] data, int ni, int nj, int nk, double ox, double oy, double oz)
    {
        public float[] Data { get; } = data;
        public int Ni { get; } = ni;
        public int Nj { get; } = nj;
        public int Nk { get; } = nk;
        public double Ox { get; } = ox;
        public double Oy { get; } = oy;
        public double Oz { get; } = oz;

        // Trilinear at a position in cell units, with the bounds of the eight values.
        public float Sample(double x, double y, double z, out float low, out float high)
        {
            double fi = Math.Clamp(x - Ox, 0, Ni - 1), fj = Math.Clamp(y - Oy, 0, Nj - 1), fk = Math.Clamp(z - Oz, 0, Nk - 1);
            int i = Math.Min((int)fi, Ni - 2), j = Math.Min((int)fj, Nj - 2), k = Math.Min((int)fk, Nk - 2);
            float tx = (float)(fi - i), ty = (float)(fj - j), tz = (float)(fk - k);
            int b = (k * Ni + i) * Nj + j, di = Nj, dk = Ni * Nj;
            var d = Data;
            float v000 = d[b], v010 = d[b + 1], v100 = d[b + di], v110 = d[b + di + 1];
            float v001 = d[b + dk], v011 = d[b + dk + 1], v101 = d[b + dk + di], v111 = d[b + dk + di + 1];
            low = Math.Min(Math.Min(Math.Min(v000, v010), Math.Min(v100, v110)), Math.Min(Math.Min(v001, v011), Math.Min(v101, v111)));
            high = Math.Max(Math.Max(Math.Max(v000, v010), Math.Max(v100, v110)), Math.Max(Math.Max(v001, v011), Math.Max(v101, v111)));
            float a = v000 + ty * (v010 - v000), c = v100 + ty * (v110 - v100);
            float e = v001 + ty * (v011 - v001), g = v101 + ty * (v111 - v101);
            float front = a + tx * (c - a), back = e + tx * (g - e);
            return front + tz * (back - front);
        }

        // Trilinear at a position given in the array's own index space.
        public static float Sample(float[] d, int ni, int nj, int nk, double fi, double fj, double fk)
        {
            fi = Math.Clamp(fi, 0, ni - 1);
            fj = Math.Clamp(fj, 0, nj - 1);
            fk = Math.Clamp(fk, 0, nk - 1);
            int i = Math.Min((int)fi, ni - 2), j = Math.Min((int)fj, nj - 2), k = Math.Min((int)fk, nk - 2);
            float tx = (float)(fi - i), ty = (float)(fj - j), tz = (float)(fk - k);
            int b = (k * ni + i) * nj + j, di = nj, dk = ni * nj;
            float a = d[b] + ty * (d[b + 1] - d[b]), c = d[b + di] + ty * (d[b + di + 1] - d[b + di]);
            float e = d[b + dk] + ty * (d[b + dk + 1] - d[b + dk]), g = d[b + dk + di] + ty * (d[b + dk + di + 1] - d[b + dk + di]);
            float front = a + tx * (c - a), back = e + tx * (g - e);
            return front + tz * (back - front);
        }
    }
}
