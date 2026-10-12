using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Algorithms.ScanReduceOperations;

namespace ParagliderToolbox.Airflow.Gpu;

/// <summary>
/// The airflow solver's kernels: the steps of ParagliderToolbox.Airflow's AirflowSolver (see it for the physics), one
/// thread per face, cell or column, in single precision.
/// </summary>
internal static class GpuKernels
{
    private const float Gravity = 9.80665f;
    private const float SpecificHeat = 1004.6f;
    private const float LatentHeat = 2.501e6f;
    private const float VaporConstant = 461.5f;
    private const float Epsilon = 287.05f / 461.5f;
    private const float ZeroCelsius = 273.15f;
    private const float WaterTransfer = 1.1e-3f;
    private const float LiftThreshold = 1f;
    private const float CloudThreshold = 1e-5f;
    private const int RelaxationCells = 5;
    private const float RelaxationTime = 90f;
    private const float OutflowRelaxationTime = 400f;
    private const float SpongeTime = 120f;
    private const float CloudExtinction = 150f;
    private const int NoiseBlock = 8;
    private const int NoiseBlocks = 2;

    #region Helpers

    private static float Clamp(float value, float min, float max) => XMath.Max(min, XMath.Min(max, value));

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    // Trilinear in an array of (ni, nj, nk) at a position in its own index space, with the bounds of the eight values.
    private static float Sample(ArrayView<float> d, int ni, int nj, int nk, float fi, float fj, float fk, out float low, out float high)
    {
        fi = Clamp(fi, 0f, ni - 1);
        fj = Clamp(fj, 0f, nj - 1);
        fk = Clamp(fk, 0f, nk - 1);
        int i = XMath.Min((int)fi, ni - 2), j = XMath.Min((int)fj, nj - 2), k = XMath.Min((int)fk, nk - 2);
        float tx = fi - i, ty = fj - j, tz = fk - k;
        int b = (k * ni + i) * nj + j, di = nj, dk = ni * nj;
        float v000 = d[b], v010 = d[b + 1], v100 = d[b + di], v110 = d[b + di + 1];
        float v001 = d[b + dk], v011 = d[b + dk + 1], v101 = d[b + dk + di], v111 = d[b + dk + di + 1];
        low = XMath.Min(XMath.Min(XMath.Min(v000, v010), XMath.Min(v100, v110)), XMath.Min(XMath.Min(v001, v011), XMath.Min(v101, v111)));
        high = XMath.Max(XMath.Max(XMath.Max(v000, v010), XMath.Max(v100, v110)), XMath.Max(XMath.Max(v001, v011), XMath.Max(v101, v111)));
        float a = v000 + ty * (v010 - v000), c = v100 + ty * (v110 - v100);
        float e = v001 + ty * (v011 - v001), g = v101 + ty * (v111 - v101);
        float front = a + tx * (c - a), back = e + tx * (g - e);
        return front + tz * (back - front);
    }

    private static float Sample(Staggered field, float x, float y, float z, out float low, out float high) =>
        Sample(field.Data, field.Ni, field.Nj, field.Nk, x - field.Ox, y - field.Oy, z - field.Oz, out low, out high);

    // The velocity in cells per second at a position in cell units.
    private static Float3 VelocityInCells(GridViews g, VelocityViews v, float x, float y, float z)
    {
        float u = Sample(v.X, g.Nx + 1, g.Ny, g.Nz, x, y - 0.5f, z - 0.5f, out _, out _);
        float w = Sample(v.Y, g.Nx, g.Ny + 1, g.Nz, x - 0.5f, y, z - 0.5f, out _, out _);
        float s = Sample(v.Z, g.Nx, g.Ny, g.Nz + 1, x - 0.5f, y - 0.5f, z, out _, out _);
        return new Float3(u / g.H, w / g.Dy, s / g.H);
    }

    // Where the air at a position (cell units) is after dt (negative: was before), with the midpoint rule.
    private static Float3 Trace(GridViews g, VelocityViews v, float x, float y, float z, float dt)
    {
        var a = VelocityInCells(g, v, x, y, z);
        var m = VelocityInCells(g, v, x + 0.5f * dt * a.X, y + 0.5f * dt * a.Y, z + 0.5f * dt * a.Z);
        return new Float3(x + dt * m.X, y + dt * m.Y, z + dt * m.Z);
    }

    // The sounding's potential temperature at a height in cell units, minus level j's.
    private static float BackgroundShift(GridViews g, float y, int j)
    {
        float f = Clamp(y - 0.5f, 0f, g.Ny - 1);
        int a = XMath.Min((int)f, g.Ny - 2);
        float t = f - a;
        return g.Theta0[a] + t * (g.Theta0[a + 1] - g.Theta0[a]) - g.Theta0[j];
    }

    private static float Correct(float hat, float original, float back, float low, float high)
    {
        float value = hat + 0.5f * (original - back);
        return value < low || value > high ? hat : value;
    }

    private static float SaturationMixingRatio(float temperature, float pressure)
    {
        float celsius = temperature - ZeroCelsius;
        float vapor = 611.2f * XMath.Exp(17.67f * celsius / (celsius + 243.5f));
        return Epsilon * vapor / XMath.Max(pressure - vapor, 1f);
    }

    #endregion

    #region Extrapolation

    public static void ExtrapolateX(Index1D index, GridViews g, ArrayView<float> ux)
    {
        int first = g.FirstX[index];
        if (first <= 0 || first >= g.Ny) return;
        int start = index * g.Ny;
        float value = ux[start + first];
        for (int j = 0; j < first; j++) ux[start + j] = value;
    }

    public static void ExtrapolateZ(Index1D index, GridViews g, ArrayView<float> uz)
    {
        int first = g.FirstZ[index];
        if (first <= 0 || first >= g.Ny) return;
        int start = index * g.Ny;
        float value = uz[start + first];
        for (int j = 0; j < first; j++) uz[start + j] = value;
    }

    public static void ExtrapolateCells(Index1D column, GridViews g, ScalarViews s)
    {
        int first = g.FirstLevel[column];
        if (first <= 0 || first >= g.Ny) return;
        int start = column * g.Ny;
        for (int j = 0; j < first; j++)
        {
            s.Theta[start + j] = s.Theta[start + first];
            s.Vapor[start + j] = s.Vapor[start + first];
            s.Cloud[start + j] = s.Cloud[start + first];
        }
    }

    #endregion

    #region Advection

    // The first-order (semi-Lagrangian) step of a staggered velocity: the value where the air comes from.
    public static void AdvectFaces(Index1D f, GridViews g, VelocityViews v, Staggered field, ArrayView<float> open, ArrayView<float> hat,
        ArrayView<float> low, ArrayView<float> high, float dt)
    {
        if (open[f] <= 0)
        {
            hat[f] = field.Data[f];
            return;
        }
        int j = f % field.Nj, t = f / field.Nj, i = t % field.Ni, k = t / field.Ni;
        var back = Trace(g, v, i + field.Ox, j + field.Oy, k + field.Oz, -dt);
        hat[f] = Sample(field, back.X, back.Y, back.Z, out float lo, out float hi);
        low[f] = lo;
        high[f] = hi;
    }

    // MacCormack's correction: back from where the air goes with the first-order result; half the difference, within bounds.
    public static void CorrectFaces(Index1D f, GridViews g, VelocityViews v, Staggered field, Staggered hat, ArrayView<float> open,
        ArrayView<float> low, ArrayView<float> high, ArrayView<float> result, float dt)
    {
        if (open[f] <= 0)
        {
            result[f] = 0;
            return;
        }
        int j = f % field.Nj, t = f / field.Nj, i = t % field.Ni, k = t / field.Ni;
        var forward = Trace(g, v, i + field.Ox, j + field.Oy, k + field.Oz, dt);
        float back = Sample(hat, forward.X, forward.Y, forward.Z, out _, out _);
        result[f] = Correct(hat.Data[f], field.Data[f], back, low[f], high[f]);
    }

    public static void AdvectCells(Index1D c, GridViews g, VelocityViews v, ScalarViews s, ScalarViews hat, ScalarViews low, ScalarViews high, float dt)
    {
        if (g.IsAir[c] == 0)
        {
            hat.Theta[c] = s.Theta[c];
            hat.Vapor[c] = s.Vapor[c];
            hat.Cloud[c] = s.Cloud[c];
            return;
        }
        int j = c % g.Ny, t = c / g.Ny, i = t % g.Nx, k = t / g.Nx;
        var back = Trace(g, v, i + 0.5f, j + 0.5f, k + 0.5f, -dt);
        // The potential temperature is conserved, not its difference to the sounding.
        float shift = BackgroundShift(g, back.Y, j);
        hat.Theta[c] = Sample(s.Theta, g.Nx, g.Ny, g.Nz, back.X - 0.5f, back.Y - 0.5f, back.Z - 0.5f, out float lt, out float ht) + shift;
        low.Theta[c] = lt + shift;
        high.Theta[c] = ht + shift;
        hat.Vapor[c] = Sample(s.Vapor, g.Nx, g.Ny, g.Nz, back.X - 0.5f, back.Y - 0.5f, back.Z - 0.5f, out float lv, out float hv);
        low.Vapor[c] = lv;
        high.Vapor[c] = hv;
        hat.Cloud[c] = Sample(s.Cloud, g.Nx, g.Ny, g.Nz, back.X - 0.5f, back.Y - 0.5f, back.Z - 0.5f, out float lc, out float hc);
        low.Cloud[c] = lc;
        high.Cloud[c] = hc;
    }

    public static void CorrectCells(Index1D c, GridViews g, VelocityViews v, ScalarViews s, ScalarViews hat, ScalarViews low, ScalarViews high,
        ScalarViews result, float dt)
    {
        if (g.IsAir[c] == 0)
        {
            result.Theta[c] = s.Theta[c];
            result.Vapor[c] = s.Vapor[c];
            result.Cloud[c] = s.Cloud[c];
            return;
        }
        int j = c % g.Ny, t = c / g.Ny, i = t % g.Nx, k = t / g.Nx;
        var forward = Trace(g, v, i + 0.5f, j + 0.5f, k + 0.5f, dt);
        float x = forward.X - 0.5f, y = forward.Y - 0.5f, z = forward.Z - 0.5f;
        float theta = Sample(hat.Theta, g.Nx, g.Ny, g.Nz, x, y, z, out _, out _) + BackgroundShift(g, forward.Y, j);
        result.Theta[c] = Correct(hat.Theta[c], s.Theta[c], theta, low.Theta[c], high.Theta[c]);
        float vapor = Sample(hat.Vapor, g.Nx, g.Ny, g.Nz, x, y, z, out _, out _);
        result.Vapor[c] = XMath.Max(0f, Correct(hat.Vapor[c], s.Vapor[c], vapor, low.Vapor[c], high.Vapor[c]));
        float cloud = Sample(hat.Cloud, g.Nx, g.Ny, g.Nz, x, y, z, out _, out _);
        result.Cloud[c] = XMath.Max(0f, Correct(hat.Cloud[c], s.Cloud[c], cloud, low.Cloud[c], high.Cloud[c]));
    }

    #endregion

    #region Forces

    // Every level's sums of warming, vapor excess, cloud water and air cells (for the buoyancy's horizontal means).
    public static void LevelSums(Index1D c, GridViews g, ScalarViews s, ArrayView<float> sums)
    {
        if (g.IsAir[c] == 0) return;
        int j = c % g.Ny;
        if (j < g.FirstLevel[c / g.Ny]) return;
        Atomic.Add(ref sums[j * 4], s.Theta[c]);
        Atomic.Add(ref sums[j * 4 + 1], s.Vapor[c] - g.Vapor0[j]);
        Atomic.Add(ref sums[j * 4 + 2], s.Cloud[c]);
        Atomic.Add(ref sums[j * 4 + 3], 1f);
    }

    public static void LevelMeans(Index1D j, ArrayView<float> sums, ArrayView<float> means)
    {
        float n = sums[j * 4 + 3];
        means[j * 3] = n > 0 ? sums[j * 4] / n : 0;
        means[j * 3 + 1] = n > 0 ? sums[j * 4 + 1] / n : 0;
        means[j * 3 + 2] = n > 0 ? sums[j * 4 + 2] / n : 0;
    }

    private static float Buoyancy(GridViews g, ScalarViews s, ArrayView<float> means, int c, int level, int face) =>
        (s.Theta[c] - means[level * 3]) / g.ThetaFace[face] + 0.608f * (s.Vapor[c] - g.Vapor0[level] - means[level * 3 + 1]) - (s.Cloud[c] - means[level * 3 + 2]);

    public static void ApplyBuoyancy(Index1D f, GridViews g, ArrayView<float> uy, ScalarViews s, ArrayView<float> means, float dt)
    {
        int j = f % (g.Ny + 1), column = f / (g.Ny + 1);
        if (j < 1 || j >= g.Ny || j < g.FirstLevel[column] || g.OpenY[f] <= 0) return;
        int below = column * g.Ny + j - 1, above = below + 1;
        float b = g.IsAir[below] != 0
            ? (Buoyancy(g, s, means, below, j - 1, j) + Buoyancy(g, s, means, above, j, j)) / 2
            : Buoyancy(g, s, means, above, j, j);
        uy[f] += dt * Gravity * b;
    }

    // Smagorinsky viscosity per cell, scaled by the local stability (gradient Richardson number).
    public static void Viscosity(Index1D c, GridViews g, VelocityViews u, VelocityViews center, ArrayView<float> theta, ArrayView<float> viscosity,
        float limit, float cs2)
    {
        int nx = g.Nx, ny = g.Ny, nz = g.Nz, slab = nx * ny;
        int j = c % ny, t = c / ny, i = t % nx, k = t / nx;
        int first = g.FirstLevel[t];
        if (j < first || g.IsAir[c] == 0)
        {
            viscosity[c] = 0;
            return;
        }
        float h = g.H, dy = g.Dy;
        float sxx = (u.X[g.FaceX(i + 1, j, k)] - u.X[g.FaceX(i, j, k)]) / h;
        float syy = (u.Y[g.FaceY(i, j + 1, k)] - u.Y[g.FaceY(i, j, k)]) / dy;
        float szz = (u.Z[g.FaceZ(i, j, k + 1)] - u.Z[g.FaceZ(i, j, k)]) / h;
        int cxm = i > 0 && g.IsAir[c - ny] != 0 ? c - ny : c, cxp = i < nx - 1 && g.IsAir[c + ny] != 0 ? c + ny : c;
        int cym = j > first && g.IsAir[c - 1] != 0 ? c - 1 : c, cyp = j < ny - 1 && g.IsAir[c + 1] != 0 ? c + 1 : c;
        int czm = k > 0 && g.IsAir[c - slab] != 0 ? c - slab : c, czp = k < nz - 1 && g.IsAir[c + slab] != 0 ? c + slab : c;
        float dX = XMath.Max(1, (cxp - cxm) / ny) * h, dY = XMath.Max(1, cyp - cym) * dy, dZ = XMath.Max(1, (czp - czm) / slab) * h;
        float duxdy = (center.X[cyp] - center.X[cym]) / dY, duydx = (center.Y[cxp] - center.Y[cxm]) / dX;
        float duxdz = (center.X[czp] - center.X[czm]) / dZ, duzdx = (center.Z[cxp] - center.Z[cxm]) / dX;
        float duydz = (center.Y[czp] - center.Y[czm]) / dZ, duzdy = (center.Z[cyp] - center.Z[cym]) / dY;
        float sxy = 0.5f * (duxdy + duydx), sxz = 0.5f * (duxdz + duzdx), syz = 0.5f * (duydz + duzdy);
        float s2 = 2 * (sxx * sxx + syy * syy + szz * szz) + 4 * (sxy * sxy + sxz * sxz + syz * syz);
        int up = XMath.Min(j + 1, ny - 1), down = XMath.Max(j - 1, 0);
        float dtheta = (g.Theta0[up] - g.Theta0[down]) / (up - down) / dy + (theta[cyp] - theta[cym]) / dY;
        float n2 = Gravity / g.Theta0[j] * dtheta;
        float ri = n2 / XMath.Max(s2, 1e-10f);
        float factor = ri >= 0.25f ? 0 : ri >= 0 ? XMath.Sqrt(1 - 4 * ri) : XMath.Min(3f, XMath.Sqrt(1 - 16 * ri));
        viscosity[c] = XMath.Min(limit, 0.2f + cs2 * XMath.Sqrt(s2) * factor);
    }

    // A velocity with its open neighbors of the same kind; the viscosity at the face is its two cells' mean along the axis.
    public static void DiffuseFaces(Index1D f, GridViews g, ArrayView<float> field, ArrayView<float> result, ArrayView<float> open, int ni, int nj, int nk,
        int axis, ArrayView<float> viscosity, float dt, float ih2, float idy2)
    {
        float value = field[f];
        if (open[f] <= 0)
        {
            result[f] = value;
            return;
        }
        int j = f % nj, t = f / nj, i = t % ni, k = t / ni, slab = ni * nj;
        int a, b;
        if (axis == 0)
        {
            a = g.Cell(Clamp(i - 1, 0, g.Nx - 1), j, k);
            b = g.Cell(Clamp(i, 0, g.Nx - 1), j, k);
        }
        else if (axis == 1)
        {
            a = g.Cell(i, Clamp(j - 1, 0, g.Ny - 1), k);
            b = g.Cell(i, Clamp(j, 0, g.Ny - 1), k);
        }
        else
        {
            a = g.Cell(i, j, Clamp(k - 1, 0, g.Nz - 1));
            b = g.Cell(i, j, Clamp(k, 0, g.Nz - 1));
        }
        float nu = (viscosity[a] + viscosity[b]) / 2, sum = 0;
        if (i > 0 && open[f - nj] > 0) sum += (field[f - nj] - value) * ih2;
        if (i < ni - 1 && open[f + nj] > 0) sum += (field[f + nj] - value) * ih2;
        if (j > 0 && open[f - 1] > 0) sum += (field[f - 1] - value) * idy2;
        if (j < nj - 1 && open[f + 1] > 0) sum += (field[f + 1] - value) * idy2;
        if (k > 0 && open[f - slab] > 0) sum += (field[f - slab] - value) * ih2;
        if (k < nk - 1 && open[f + slab] > 0) sum += (field[f + slab] - value) * ih2;
        result[f] = value + dt * nu * sum;
    }

    private static float Flux(ArrayView<float> field, ArrayView<float> viscosity, float nu, float value, int other, float open, float inverse, float offset) =>
        open * 3 * (nu + viscosity[other]) / 2 * (field[other] - value + offset) * inverse;

    // A scalar through the open faces; the potential temperature (background 1) mixes as a whole, with the sounding's.
    public static void DiffuseCells(Index1D c, GridViews g, ArrayView<float> field, ArrayView<float> result, ArrayView<float> viscosity, int background,
        float dt, float ih2, float idy2)
    {
        float value = field[c];
        if (g.IsAir[c] == 0)
        {
            result[c] = value;
            return;
        }
        int nx = g.Nx, ny = g.Ny, nz = g.Nz, slab = nx * ny;
        int j = c % ny, t = c / ny, i = t % nx, k = t / nx;
        float nu = viscosity[c], sum = 0;
        if (i > 0) sum += Flux(field, viscosity, nu, value, c - ny, g.OpenX[g.FaceX(i, j, k)], ih2, 0);
        if (i < nx - 1) sum += Flux(field, viscosity, nu, value, c + ny, g.OpenX[g.FaceX(i + 1, j, k)], ih2, 0);
        float below = background == 0 || j == 0 ? 0 : g.Theta0[j - 1] - g.Theta0[j];
        float above = background == 0 || j == ny - 1 ? 0 : g.Theta0[j + 1] - g.Theta0[j];
        if (j > 0) sum += Flux(field, viscosity, nu, value, c - 1, g.OpenY[g.FaceY(i, j, k)], idy2, below);
        if (j < ny - 1) sum += Flux(field, viscosity, nu, value, c + 1, g.OpenY[g.FaceY(i, j + 1, k)], idy2, above);
        if (k > 0) sum += Flux(field, viscosity, nu, value, c - slab, g.OpenZ[g.FaceZ(i, j, k)], ih2, 0);
        if (k < nz - 1) sum += Flux(field, viscosity, nu, value, c + slab, g.OpenZ[g.FaceZ(i, j, k + 1)], ih2, 0);
        result[c] = value + dt * sum / XMath.Max(g.Air[c], 0.25f);
    }

    // The clouds' shadows: the sunlight that gets through the cloud water along the sun's ray from a column's ground (a
    // level up per step, the nearest cell's, none beyond the sides), and the land's heat following it.
    public static void Shade(Index1D column, GridViews g, SurfaceViews sv, ArrayView<float> cloud, float stepX, float stepZ, float length, float follow)
    {
        float fi = column % g.Nx + 0.5f, fk = column / g.Nx + 0.5f, path = 0;
        for (int j = g.FirstLevel[column]; j < g.Ny; j++)
        {
            int i = (int)XMath.Floor(fi), k = (int)XMath.Floor(fk);
            if (i < 0 || i >= g.Nx || k < 0 || k >= g.Nz) break;
            path += cloud[g.Cell(i, j, k)] * g.Density0[j];
            fi += stepX;
            fk += stepZ;
        }
        float through = XMath.Exp(-CloudExtinction * path * length);
        float heat = sv.ShadedHeat[column] + through * (sv.FullHeat[column] - sv.ShadedHeat[column]);
        float evaporation = sv.ShadedEvaporation[column] + through * (sv.FullEvaporation[column] - sv.ShadedEvaporation[column]);
        sv.Heat[column] += (heat - sv.Heat[column]) * follow;
        sv.Evaporation[column] += (evaporation - sv.Evaporation[column]) * follow;
    }

    // A column's ground: its land's heat and vapor, plus its water's by how much warmer the water is than the air above;
    // the heat and vapor go into the cells the ground passes through, and the friction rate of every cell is noted.
    public static void Surface(Index1D column, GridViews g, SurfaceViews sv, VelocityViews center, ScalarViews s, ArrayView<float> rate, float dt)
    {
        int ny = g.Ny, i = column % g.Nx, k = column / g.Nx;
        float heat = sv.Heat[column], evaporation = sv.Evaporation[column], water = sv.Water[column];
        int first = g.FirstLevel[column];
        if (water > 0 && first < ny)
        {
            int j = first, c = column * ny + first;
            if (g.Air[c] < 0.5f && j + 1 < ny && g.IsAir[c + 1] != 0)
            {
                c++;
                j++;
            }
            float air = (g.Theta0[j] + s.Theta[c]) * g.Exner0[j];
            float speed = XMath.Sqrt(center.X[c] * center.X[c] + center.Y[c] * center.Y[c] + center.Z[c] * center.Z[c]);
            float exchange = g.Density0[j] * WaterTransfer * XMath.Max(speed, 1f);
            float sensible = exchange * SpecificHeat * (sv.WaterTemperature[column] - air);
            float vapor = XMath.Max(0f, exchange * (SaturationMixingRatio(sv.WaterTemperature[column], g.Pressure0[j]) - s.Vapor[c]));
            sv.WaterHeat[column] = sensible;
            heat += water * sensible;
            evaporation += water * vapor;
        }
        sv.SurfaceHeat[column] = heat;
        for (int j = 0; j < ny; j++)
        {
            int c = column * ny + j;
            float ground = g.Ground[c];
            if (ground <= 0 || g.IsAir[c] == 0)
            {
                rate[c] = 0;
                continue;
            }
            float depth = XMath.Max(g.Air[c], 0.25f) * g.Dy;
            float speed = XMath.Sqrt(center.X[c] * center.X[c] + center.Y[c] * center.Y[c] + center.Z[c] * center.Z[c]);
            rate[c] = sv.Drag[column] * ground / depth * speed;
            bool share = g.Air[c] < 0.5f && j + 1 < ny && g.IsAir[c + 1] != 0;
            float layer = share ? (g.Air[c] + g.Air[c + 1]) * g.Dy : depth;
            float warming = dt * heat * ground / (g.Density0[j] * SpecificHeat * g.Exner0[j] * layer);
            float moistening = dt * evaporation * ground / (g.Density0[j] * layer);
            s.Theta[c] += warming;
            s.Vapor[c] = XMath.Max(0f, s.Vapor[c] + moistening);
            if (share)
            {
                s.Theta[c + 1] += warming;
                s.Vapor[c + 1] = XMath.Max(0f, s.Vapor[c + 1] + moistening);
            }
        }
    }

    public static void DragX(Index1D f, GridViews g, ArrayView<float> ux, ArrayView<float> rate, float dt)
    {
        int j = f % g.Ny, t = f / g.Ny, i = t % (g.Nx + 1), k = t / (g.Nx + 1);
        float r = (rate[g.Cell(XMath.Max(i - 1, 0), j, k)] + rate[g.Cell(XMath.Min(i, g.Nx - 1), j, k)]) / 2;
        if (r > 0) ux[f] /= 1 + dt * r;
    }

    public static void DragY(Index1D f, GridViews g, ArrayView<float> uy, ArrayView<float> rate, float dt)
    {
        int j = f % (g.Ny + 1), column = f / (g.Ny + 1);
        if (j < 1 || j >= g.Ny) return;
        float r = (rate[column * g.Ny + j - 1] + rate[column * g.Ny + j]) / 2;
        if (r > 0) uy[f] /= 1 + dt * r;
    }

    public static void DragZ(Index1D f, GridViews g, ArrayView<float> uz, ArrayView<float> rate, float dt)
    {
        int j = f % g.Ny, t = f / g.Ny, i = t % g.Nx, k = t / g.Nx;
        float r = (rate[g.Cell(i, j, XMath.Max(k - 1, 0))] + rate[g.Cell(i, j, XMath.Min(k, g.Nz - 1))]) / 2;
        if (r > 0) uz[f] /= 1 + dt * r;
    }

    // Saturation adjustment with the latent heat; heavy cloud water rains out slowly (dt 0: no rain).
    public static void Condense(Index1D c, GridViews g, ScalarViews s, float dt)
    {
        int j = c % g.Ny;
        if (g.IsAir[c] == 0 || j < g.FirstLevel[c / g.Ny]) return;
        float temperature = (g.Theta0[j] + s.Theta[c]) * g.Exner0[j];
        float saturation = SaturationMixingRatio(temperature, g.Pressure0[j]);
        float factor = 1 + LatentHeat * LatentHeat * saturation / (SpecificHeat * VaporConstant * temperature * temperature);
        float change = (s.Vapor[c] - saturation) / factor;
        if (change < 0) change = XMath.Max(change, -s.Cloud[c]);
        if (change == 0) return;
        s.Vapor[c] -= change;
        float cloud = s.Cloud[c] + change;
        s.Theta[c] += LatentHeat * change / (SpecificHeat * g.Exner0[j]);
        if (dt > 0 && cloud > 1.5e-3f) cloud -= (cloud - 1.5e-3f) * XMath.Min(1f, dt / 600);
        s.Cloud[c] = cloud;
    }

    // The relaxation's share at a position in cell units: towards the inflow near the sides (firmly upwind), and the
    // sponge under the lid.
    private static float RelaxationShare(GridViews g, BoundaryParams b, float fi, float fj, float fk, float dt)
    {
        float band = XMath.Max(XMath.Max(Side(fi, b.West), Side(g.Nx - fi, b.East)), XMath.Max(Side(fk, b.North), Side(g.Nz - fk, b.South)));
        float sponge = 0;
        if (fj > b.SpongeStart)
        {
            float s = XMath.Sin(XMath.PIHalf * (fj - b.SpongeStart) / (g.Ny - b.SpongeStart));
            sponge = s * s / SpongeTime;
        }
        float rate = XMath.Max(band, sponge);
        return rate <= 0 ? 0 : 1 - XMath.Exp(-rate * dt);
    }

    private static float Side(float d, int inflow)
    {
        if (d >= RelaxationCells) return 0;
        float x = (RelaxationCells - d) / RelaxationCells;
        return x * x / (inflow != 0 ? RelaxationTime : OutflowRelaxationTime);
    }

    public static void RelaxX(Index1D f, GridViews g, ArrayView<float> ux, ArrayView<float> target, BoundaryParams b, float dt)
    {
        if (g.OpenX[f] <= 0) return;
        int j = f % g.Ny, t = f / g.Ny, i = t % (g.Nx + 1), k = t / (g.Nx + 1);
        float a = RelaxationShare(g, b, i, j + 0.5f, k + 0.5f, dt);
        if (a > 0) ux[f] += (target[f] - ux[f]) * a;
    }

    public static void RelaxY(Index1D f, GridViews g, ArrayView<float> uy, ArrayView<float> target, BoundaryParams b, float dt)
    {
        if (g.OpenY[f] <= 0) return;
        int j = f % (g.Ny + 1), t = f / (g.Ny + 1), i = t % g.Nx, k = t / g.Nx;
        float a = RelaxationShare(g, b, i + 0.5f, j, k + 0.5f, dt);
        if (a > 0) uy[f] += (target[f] - uy[f]) * a;
    }

    public static void RelaxZ(Index1D f, GridViews g, ArrayView<float> uz, ArrayView<float> target, BoundaryParams b, float dt)
    {
        if (g.OpenZ[f] <= 0) return;
        int j = f % g.Ny, t = f / g.Ny, i = t % g.Nx, k = t / g.Nx;
        float a = RelaxationShare(g, b, i + 0.5f, j + 0.5f, k, dt);
        if (a > 0) uz[f] += (target[f] - uz[f]) * a;
    }

    public static void RelaxCells(Index1D c, GridViews g, ScalarViews s, ArrayView<float> initialTheta, ArrayView<float> initialVapor, BoundaryParams b, float dt)
    {
        if (g.IsAir[c] == 0) return;
        int j = c % g.Ny, t = c / g.Ny, i = t % g.Nx, k = t / g.Nx;
        float a = RelaxationShare(g, b, i + 0.5f, j + 0.5f, k + 0.5f, dt);
        if (a <= 0) return;
        s.Theta[c] += (initialTheta[c] - s.Theta[c]) * a;
        s.Vapor[c] += (initialVapor[c] - s.Vapor[c]) * a;
        s.Cloud[c] -= s.Cloud[c] * a;
    }

    // The inflow noise: random warmth in blocks just inside the upwind sides' relaxation band (see AirflowSetup.Noise).
    public static void PerturbInflow(Index1D column, GridViews g, ArrayView<float> theta, BoundaryParams b, int epoch, float amplitude, int levels)
    {
        int i = column % g.Nx, k = column / g.Nx, d = int.MaxValue;
        if (b.West != 0) d = XMath.Min(d, i);
        if (b.East != 0) d = XMath.Min(d, g.Nx - 1 - i);
        if (b.North != 0) d = XMath.Min(d, k);
        if (b.South != 0) d = XMath.Min(d, g.Nz - 1 - k);
        if (d < RelaxationCells || d >= RelaxationCells + NoiseBlock * NoiseBlocks) return;
        int first = g.FirstLevel[column], end = XMath.Min(g.Ny, first + levels);
        for (int j = first; j < end; j++)
        {
            int c = column * g.Ny + j;
            if (g.IsAir[c] == 0) continue;
            uint h = (uint)(i / NoiseBlock) * 73856093u ^ (uint)(j / NoiseBlock) * 19349663u ^ (uint)(k / NoiseBlock) * 83492791u ^ (uint)epoch * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            theta[c] += amplitude * ((h & 0xFFFF) / 32767.5f - 1);
        }
    }
    #endregion

    #region Boundaries and projection

    // An open side lets the flow out as it comes; air coming in takes the inflow's.
    private static float Open(float inside, float outward, float target) => inside * outward >= 0 ? inside : target;

    public static void BoundaryX(Index1D index, GridViews g, ArrayView<float> ux, ArrayView<float> target, BoundaryParams b)
    {
        int j = index % g.Ny, k = index / g.Ny;
        int west = g.FaceX(0, j, k), east = g.FaceX(g.Nx, j, k);
        ux[west] = g.OpenX[west] <= 0 ? 0 : b.West != 0 ? target[west] : Open(ux[g.FaceX(1, j, k)], -1, target[west]);
        ux[east] = g.OpenX[east] <= 0 ? 0 : b.East != 0 ? target[east] : Open(ux[g.FaceX(g.Nx - 1, j, k)], 1, target[east]);
    }

    public static void BoundaryZ(Index1D index, GridViews g, ArrayView<float> uz, ArrayView<float> target, BoundaryParams b)
    {
        int j = index % g.Ny, i = index / g.Ny;
        int north = g.FaceZ(i, j, 0), south = g.FaceZ(i, j, g.Nz);
        uz[north] = g.OpenZ[north] <= 0 ? 0 : b.North != 0 ? target[north] : Open(uz[g.FaceZ(i, j, 1)], -1, target[north]);
        uz[south] = g.OpenZ[south] <= 0 ? 0 : b.South != 0 ? target[south] : Open(uz[g.FaceZ(i, j, g.Nz - 1)], 1, target[south]);
    }

    public static void CloseFaces(Index1D f, ArrayView<float> open, ArrayView<float> u)
    {
        if (open[f] <= 0) u[f] = 0;
    }

    // The negative net volume flux out of every active cell.
    public static void Divergence(Index1D c, GridViews g, VelocityViews u, ArrayView<float> diag, ArrayView<float> rhs)
    {
        if (diag[c] <= 0)
        {
            rhs[c] = 0;
            return;
        }
        int j = c % g.Ny, t = c / g.Ny, i = t % g.Nx, k = t / g.Nx;
        int x0 = g.FaceX(i, j, k), x1 = x0 + g.Ny, y0 = g.FaceY(i, j, k), y1 = y0 + 1, z0 = c, z1 = c + g.Nx * g.Ny;
        float outflux = g.H * g.Dy * (g.OpenX[x1] * u.X[x1] - g.OpenX[x0] * u.X[x0] + g.OpenZ[z1] * u.Z[z1] - g.OpenZ[z0] * u.Z[z0])
                        + g.H * g.H * (g.OpenY[y1] * u.Y[y1] - g.OpenY[y0] * u.Y[y0]);
        rhs[c] = -outflux;
    }

    public static void ProjectX(Index1D f, GridViews g, ArrayView<float> ux, ArrayView<float> p, BoundaryParams b)
    {
        if (g.OpenX[f] <= 0) return;
        int j = f % g.Ny, t = f / g.Ny, i = t % (g.Nx + 1), k = t / (g.Nx + 1);
        if ((i == 0 && b.West != 0) || (i == g.Nx && b.East != 0)) return;
        float west = i > 0 ? p[g.Cell(i - 1, j, k)] : 0, east = i < g.Nx ? p[g.Cell(i, j, k)] : 0;
        ux[f] -= (east - west) / g.H;
    }

    public static void ProjectY(Index1D f, GridViews g, ArrayView<float> uy, ArrayView<float> p)
    {
        int j = f % (g.Ny + 1), column = f / (g.Ny + 1);
        if (j < 1 || j >= g.Ny || g.OpenY[f] <= 0) return;
        uy[f] -= (p[column * g.Ny + j] - p[column * g.Ny + j - 1]) / g.Dy;
    }

    public static void ProjectZ(Index1D f, GridViews g, ArrayView<float> uz, ArrayView<float> p, BoundaryParams b)
    {
        if (g.OpenZ[f] <= 0) return;
        int j = f % g.Ny, t = f / g.Ny, i = t % g.Nx, k = t / g.Nx;
        if ((k == 0 && b.North != 0) || (k == g.Nz && b.South != 0)) return;
        float north = k > 0 ? p[g.Cell(i, j, k - 1)] : 0, south = k < g.Nz ? p[g.Cell(i, j, k)] : 0;
        uz[f] -= (south - north) / g.H;
    }

    // The velocity at the cells' centers: the open-weighted mean of their faces.
    public static void Centers(Index1D c, GridViews g, VelocityViews u, VelocityViews center)
    {
        if (g.IsAir[c] == 0)
        {
            center.X[c] = 0;
            center.Y[c] = 0;
            center.Z[c] = 0;
            return;
        }
        int j = c % g.Ny, t = c / g.Ny, i = t % g.Nx, k = t / g.Nx;
        center.X[c] = Mean(g.OpenX, u.X, g.FaceX(i, j, k), g.FaceX(i + 1, j, k));
        center.Y[c] = Mean(g.OpenY, u.Y, g.FaceY(i, j, k), g.FaceY(i, j + 1, k));
        center.Z[c] = Mean(g.OpenZ, u.Z, g.FaceZ(i, j, k), g.FaceZ(i, j, k + 1));
    }

    private static float Mean(ArrayView<float> open, ArrayView<float> velocity, int a, int b)
    {
        float wa = open[a], wb = open[b];
        return wa + wb > 0 ? (wa * velocity[a] + wb * velocity[b]) / (wa + wb) : 0;
    }

    #endregion

    #region Pressure (multigrid-preconditioned conjugate gradients)

    // Σ weight × neighbor over the six faces (boundary faces have no neighbor: the pressure outside is 0).
    private static float Neighbors(LevelViews l, ArrayView<float> x, int i, int j, int k, int c)
    {
        int slab = l.Nx * l.Ny, fx = (k * (l.Nx + 1) + i) * l.Ny + j, fy = (k * l.Nx + i) * (l.Ny + 1) + j;
        float sum = 0;
        if (i > 0) sum += l.Wx[fx] * x[c - l.Ny];
        if (i < l.Nx - 1) sum += l.Wx[fx + l.Ny] * x[c + l.Ny];
        if (j > 0) sum += l.Wy[fy] * x[c - 1];
        if (j < l.Ny - 1) sum += l.Wy[fy + 1] * x[c + 1];
        if (k > 0) sum += l.Wz[c] * x[c - slab];
        if (k < l.Nz - 1) sum += l.Wz[c + slab] * x[c + slab];
        return sum;
    }

    public static void Multiply(Index1D c, LevelViews l, ArrayView<float> x, ArrayView<float> result)
    {
        if (l.Diag[c] <= 0)
        {
            result[c] = 0;
            return;
        }
        int j = c % l.Ny, t = c / l.Ny, i = t % l.Nx, k = t / l.Nx;
        result[c] = l.Diag[c] * x[c] - Neighbors(l, x, i, j, k, c);
    }

    public static void Residual(Index1D c, LevelViews l, ArrayView<float> x, ArrayView<float> b, ArrayView<float> r)
    {
        if (l.Diag[c] <= 0)
        {
            r[c] = 0;
            return;
        }
        int j = c % l.Ny, t = c / l.Ny, i = t % l.Nx, k = t / l.Nx;
        r[c] = b[c] - (l.Diag[c] * x[c] - Neighbors(l, x, i, j, k, c));
    }

    // One red (0) or black (1) Gauss–Seidel half sweep.
    public static void Smooth(Index1D c, LevelViews l, ArrayView<float> x, ArrayView<float> b, int color)
    {
        if (l.Diag[c] <= 0) return;
        int j = c % l.Ny, t = c / l.Ny, i = t % l.Nx, k = t / l.Nx;
        if (((i + j + k + color) & 1) != 0) return;
        x[c] = (b[c] + Neighbors(l, x, i, j, k, c)) * l.InvDiag[c];
    }

    // The coarsest level, solved by symmetric sweeps in one group of threads (one thread per cell).
    public static void SolveCoarsest(LevelViews l, ArrayView<float> x, ArrayView<float> b, int sweeps)
    {
        int count = l.Nx * l.Ny * l.Nz;
        for (int c = Group.IdxX; c < count; c += Group.DimX) x[c] = 0;
        Group.Barrier();
        for (int sweep = 0; sweep < sweeps * 4; sweep++)
        {
            // Red, black, black, red: symmetric.
            int phase = sweep & 3, color = phase == 0 || phase == 3 ? 0 : 1;
            for (int c = Group.IdxX; c < count; c += Group.DimX)
            {
                if (l.Diag[c] <= 0) continue;
                int j = c % l.Ny, t = c / l.Ny, i = t % l.Nx, k = t / l.Nx;
                if (((i + j + k + color) & 1) != 0) continue;
                x[c] = (b[c] + Neighbors(l, x, i, j, k, c)) * l.InvDiag[c];
            }
            Group.Barrier();
        }
    }

    // The coarse level's right-hand side: the sum of its 2 × 2 × 2 children's residuals.
    public static void Restrict(Index1D cc, LevelViews fine, LevelViews coarse, ArrayView<float> r, ArrayView<float> b)
    {
        int jc = cc % coarse.Ny, t = cc / coarse.Ny, ic = t % coarse.Nx, kc = t / coarse.Nx;
        float sum = 0;
        for (int k = 2 * kc; k < XMath.Min(2 * kc + 2, fine.Nz); k++)
        {
            for (int i = 2 * ic; i < XMath.Min(2 * ic + 2, fine.Nx); i++)
            {
                for (int j = 2 * jc; j < XMath.Min(2 * jc + 2, fine.Ny); j++) sum += r[(k * fine.Nx + i) * fine.Ny + j];
            }
        }
        b[cc] = sum;
    }

    public static void Prolong(Index1D c, LevelViews fine, LevelViews coarse, ArrayView<float> coarseX, ArrayView<float> x)
    {
        if (fine.Diag[c] <= 0) return;
        int j = c % fine.Ny, t = c / fine.Ny, i = t % fine.Nx, k = t / fine.Nx;
        x[c] += coarseX[((k >> 1) * coarse.Nx + (i >> 1)) * coarse.Ny + (j >> 1)];
    }

    public static void UpdateSolution(Index1D c, ArrayView<float> x, ArrayView<float> r, ArrayView<float> p, ArrayView<float> q, float alpha)
    {
        x[c] += alpha * p[c];
        r[c] -= alpha * q[c];
    }

    public static void UpdateDirection(Index1D c, ArrayView<float> p, ArrayView<float> z, float beta) => p[c] = z[c] + beta * p[c];

    #endregion

    #region Reductions

    // Σ a·b into output[slot] (grid-stride, a group sum, one atomic per group).
    public static void Dot(ArrayView<float> a, ArrayView<float> b, ArrayView<float> output, int slot)
    {
        float local = 0;
        for (int n = Grid.GlobalIndex.X; n < a.IntLength; n += Grid.DimX * Group.DimX) local += a[n] * b[n];
        float sum = GroupExtensions.Reduce<float, AddFloat>(local);
        if (Group.IsFirstThread) Atomic.Add(ref output[slot], sum);
    }

    // max |a| into output[slot] (non-negative floats compare as their bits do).
    public static void MaxAbs(ArrayView<float> a, ArrayView<int> output, int slot)
    {
        float local = 0;
        for (int n = Grid.GlobalIndex.X; n < a.IntLength; n += Grid.DimX * Group.DimX) local = XMath.Max(local, XMath.Abs(a[n]));
        float max = GroupExtensions.Reduce<float, MaxFloat>(local);
        if (Group.IsFirstThread) Atomic.Max(ref output[slot], (int)Interop.FloatAsInt(max));
    }

    #endregion

    #region Averages

    public static void Accumulate(Index1D c, GridViews g, AverageViews avg, VelocityViews center, ArrayView<float> viscosity, ScalarViews s, float w,
        float subgridVariance)
    {
        if (g.IsAir[c] == 0) return;
        float x = center.X[c], y = center.Y[c], z = center.Z[c], nu = viscosity[c];
        avg.X[c] += w * x;
        avg.Y[c] += w * y;
        avg.Z[c] += w * z;
        avg.XX[c] += w * x * x;
        avg.YY[c] += w * y * y;
        avg.ZZ[c] += w * z * z;
        avg.Subgrid[c] += w * nu * nu * subgridVariance;
        if (y >= LiftThreshold)
        {
            avg.LiftTime[c] += w;
            avg.LiftSum[c] += w * y;
        }
        if (s.Cloud[c] > CloudThreshold) avg.CloudTime[c] += w;
        avg.Theta[c] += w * s.Theta[c];
        avg.Vapor[c] += w * s.Vapor[c];
    }

    #endregion
}
