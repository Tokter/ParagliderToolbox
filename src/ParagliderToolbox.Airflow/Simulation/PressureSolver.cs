namespace ParagliderToolbox.Airflow.Simulation;

/// <summary>
/// Solves the pressure equation that makes the flow mass conserving: for every cell, the weighted pressure differences
/// to its neighbors equal the flux out of it, the weights being the faces' open air shares (a variational, cut-cell
/// discretization: the terrain's slopes count by how much of each face they leave open). Open sides of the domain hold
/// the pressure at 0, so air can leave (and enter) there.
/// </summary>
/// <remarks>
/// Conjugate gradients, preconditioned with a multigrid V-cycle: symmetric red–black Gauss–Seidel smoothing, coarse
/// levels by aggregating 2 × 2 × 2 cells with the fine faces' weights summed (Galerkin coarse operators of piecewise
/// constant interpolation), so cut faces and closed ones carry over to the coarse levels. The previous solution is the
/// next start, so few iterations are needed once the flow has settled.
/// </remarks>
internal sealed class PressureSolver
{
    /// <summary>The Gauss–Seidel sweeps before and after the coarse correction.</summary>
    internal const int Smoothing = 2;

    /// <summary>The symmetric sweep pairs that solve the coarsest level.</summary>
    internal const int CoarsestSweeps = 24;

    private const int CoarsestCells = 512;

    private readonly Level[] _levels;
    private readonly float[] _r, _z, _p, _q;

    /// <summary>Initializes the solver for <paramref name="grid"/>.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="open">Whether the west, east, north and south sides are open (the pressure held at 0 outside).</param>
    public PressureSolver(AirflowGrid grid, (bool West, bool East, bool North, bool South) open)
    {
        var fine = new Level(grid.Nx, grid.Ny, grid.Nz);
        float h = (float)grid.CellSize, dy = (float)grid.LayerHeight;
        float side = dy, vertical = h * h / dy;
        Parallel.For(0, grid.Nz, k =>
        {
            for (int i = 0; i <= grid.Nx; i++)
            {
                bool boundary = i == 0 || i == grid.Nx;
                bool isOpen = !boundary || (i == 0 ? open.West : open.East);
                for (int j = 0; j < grid.Ny; j++)
                {
                    int f = grid.FaceX(i, j, k);
                    fine.Wx[f] = isOpen ? grid.OpenX[f] * side : 0;
                }
            }
            for (int i = 0; i < grid.Nx; i++)
            {
                for (int j = 0; j <= grid.Ny; j++)
                {
                    int f = grid.FaceY(i, j, k);
                    fine.Wy[f] = grid.OpenY[f] * vertical;
                }
            }
        });
        Parallel.For(0, grid.Nz + 1, k =>
        {
            bool boundary = k == 0 || k == grid.Nz;
            bool isOpen = !boundary || (k == 0 ? open.North : open.South);
            for (int i = 0; i < grid.Nx; i++)
            {
                for (int j = 0; j < grid.Ny; j++)
                {
                    int f = grid.FaceZ(i, j, k);
                    fine.Wz[f] = isOpen ? grid.OpenZ[f] * side : 0;
                }
            }
        });
        fine.Finish();

        var levels = new List<Level> { fine };
        while (levels[^1].CellCount > CoarsestCells && Math.Min(levels[^1].Nx, Math.Min(levels[^1].Ny, levels[^1].Nz)) > 2)
        {
            levels.Add(levels[^1].Coarsen());
        }
        _levels = [.. levels];
        int n = fine.CellCount;
        _r = new float[n];
        _z = new float[n];
        _p = new float[n];
        _q = new float[n];
        Pressure = new float[n];
    }

    /// <summary>Gets the pressure (scaled: the change of velocity it makes is its difference over the cell distance).</summary>
    public float[] Pressure { get; }

    /// <summary>Gets whether a cell takes part (has an open face).</summary>
    public bool IsActive(int cell) => _levels[0].Diag[cell] > 0;

    /// <summary>Gets the number of levels of the multigrid hierarchy.</summary>
    public int LevelCount => _levels.Length;

    /// <summary>Gets the hierarchy's levels (their sizes, face weights, diagonals and lowest active cells), for a solver that copies it.</summary>
    internal IReadOnlyList<LevelData> Hierarchy => _levels.Select(l => new LevelData(l.Nx, l.Ny, l.Nz, l.Wx, l.Wy, l.Wz, l.Diag, l.InvDiag, l.First)).ToList();

    /// <summary>One level of the hierarchy.</summary>
    internal sealed record LevelData(int Nx, int Ny, int Nz, float[] Wx, float[] Wy, float[] Wz, float[] Diag, float[] InvDiag, int[] First);

    /// <summary>Gets the iterations of the last solve.</summary>
    public int Iterations { get; private set; }

    /// <summary>Gets the largest remaining flux error of the last solve.</summary>
    public double Residual { get; private set; }

    /// <summary>
    /// Solves A·p = <paramref name="rhs"/> (the negative net outflux of every cell, in the weights' units), starting from
    /// the previous pressure, until no cell's error exceeds <paramref name="tolerance"/> or after
    /// <paramref name="maxIterations"/>.
    /// </summary>
    public void Solve(float[] rhs, double tolerance, int maxIterations)
    {
        var fine = _levels[0];
        var x = Pressure;
        fine.Multiply(x, _q);
        Parallel.For(0, fine.Nz, k =>
        {
            for (int c = k * fine.Nx * fine.Ny, end = c + fine.Nx * fine.Ny; c < end; c++) _r[c] = fine.Diag[c] > 0 ? rhs[c] - _q[c] : 0;
        });
        double residual = MaxAbs(_r);
        int iteration = 0;
        if (residual > tolerance)
        {
            Precondition(_r, _z);
            Array.Copy(_z, _p, _z.Length);
            double rz = Dot(_r, _z);
            for (iteration = 1; iteration <= maxIterations; iteration++)
            {
                fine.Multiply(_p, _q);
                double pq = Dot(_p, _q);
                if (pq <= 0) break;
                float alpha = (float)(rz / pq);
                Parallel.For(0, fine.Nz, k =>
                {
                    for (int c = k * fine.Nx * fine.Ny, end = c + fine.Nx * fine.Ny; c < end; c++)
                    {
                        x[c] += alpha * _p[c];
                        _r[c] -= alpha * _q[c];
                    }
                });
                residual = MaxAbs(_r);
                if (residual <= tolerance) break;
                Precondition(_r, _z);
                double next = Dot(_r, _z);
                float beta = (float)(next / rz);
                rz = next;
                Parallel.For(0, fine.Nz, k =>
                {
                    for (int c = k * fine.Nx * fine.Ny, end = c + fine.Nx * fine.Ny; c < end; c++) _p[c] = _z[c] + beta * _p[c];
                });
            }
        }
        Iterations = iteration;
        Residual = residual;
    }

    // z = V-cycle(r).
    private void Precondition(float[] r, float[] z)
    {
        var fine = _levels[0];
        Array.Copy(r, fine.B, r.Length);
        VCycle(0);
        Array.Copy(fine.X, z, z.Length);
    }

    private void VCycle(int index)
    {
        var level = _levels[index];
        Array.Clear(level.X);
        if (index == _levels.Length - 1)
        {
            for (int sweep = 0; sweep < CoarsestSweeps; sweep++)
            {
                level.Smooth(0);
                level.Smooth(1);
                level.Smooth(1);
                level.Smooth(0);
            }
            return;
        }
        for (int sweep = 0; sweep < Smoothing; sweep++)
        {
            level.Smooth(0);
            level.Smooth(1);
        }
        level.Residual();
        var coarse = _levels[index + 1];
        level.Restrict(level.R, coarse);
        VCycle(index + 1);
        level.Prolong(coarse, level.X);
        for (int sweep = 0; sweep < Smoothing; sweep++)
        {
            level.Smooth(1);
            level.Smooth(0);
        }
    }

    private double Dot(float[] a, float[] b)
    {
        var fine = _levels[0];
        var partial = new double[fine.Nz];
        Parallel.For(0, fine.Nz, k =>
        {
            double sum = 0;
            for (int c = k * fine.Nx * fine.Ny, end = c + fine.Nx * fine.Ny; c < end; c++) sum += (double)a[c] * b[c];
            partial[k] = sum;
        });
        return partial.Sum();
    }

    private double MaxAbs(float[] a)
    {
        var fine = _levels[0];
        var partial = new double[fine.Nz];
        Parallel.For(0, fine.Nz, k =>
        {
            float max = 0;
            for (int c = k * fine.Nx * fine.Ny, end = c + fine.Nx * fine.Ny; c < end; c++) max = Math.Max(max, Math.Abs(a[c]));
            partial[k] = max;
        });
        return partial.Max();
    }

    /// <summary>One level of the hierarchy: its face weights, diagonal and work vectors.</summary>
    private sealed class Level
    {
        public Level(int nx, int ny, int nz)
        {
            Nx = nx;
            Ny = ny;
            Nz = nz;
            Wx = new float[(nx + 1) * ny * nz];
            Wy = new float[nx * (ny + 1) * nz];
            Wz = new float[nx * ny * (nz + 1)];
            Diag = new float[nx * ny * nz];
            InvDiag = new float[nx * ny * nz];
            X = new float[nx * ny * nz];
            B = new float[nx * ny * nz];
            R = new float[nx * ny * nz];
            First = new int[nx * nz];
        }

        public int Nx { get; }
        public int Ny { get; }
        public int Nz { get; }
        public int CellCount => Nx * Ny * Nz;
        public float[] Wx { get; }
        public float[] Wy { get; }
        public float[] Wz { get; }
        public float[] Diag { get; }
        public float[] InvDiag { get; }
        public float[] X { get; }
        public float[] B { get; }
        public float[] R { get; }

        // The lowest active level of every column: the cells below are inside the terrain.
        public int[] First { get; }

        public void Finish()
        {
            int slab = Nx * Ny;
            Parallel.For(0, Nz, k =>
            {
                for (int i = 0; i < Nx; i++)
                {
                    int first = Ny;
                    for (int j = 0; j < Ny; j++)
                    {
                        int c = (k * Nx + i) * Ny + j;
                        int fx = (k * (Nx + 1) + i) * Ny + j, fy = (k * Nx + i) * (Ny + 1) + j;
                        float d = Wx[fx] + Wx[fx + Ny] + Wy[fy] + Wy[fy + 1] + Wz[c] + Wz[c + slab];
                        Diag[c] = d;
                        InvDiag[c] = d > 0 ? 1 / d : 0;
                        if (d > 0 && first == Ny) first = j;
                    }
                    First[k * Nx + i] = first;
                }
            });
        }

        public void Multiply(float[] x, float[] result)
        {
            int slab = Nx * Ny;
            Parallel.For(0, Nz, k =>
            {
                for (int i = 0; i < Nx; i++)
                {
                    int column = (k * Nx + i) * Ny;
                    int first = First[k * Nx + i];
                    for (int j = 0; j < first; j++) result[column + j] = 0;
                    for (int j = first; j < Ny; j++)
                    {
                        int c = column + j;
                        result[c] = Diag[c] > 0 ? Diag[c] * x[c] - Neighbors(x, i, j, k, c, slab) : 0;
                    }
                }
            });
        }

        // Σ weight × neighbor value over the six faces (boundary faces have no neighbor: the pressure outside is 0).
        private float Neighbors(float[] x, int i, int j, int k, int c, int slab)
        {
            int fx = (k * (Nx + 1) + i) * Ny + j, fy = (k * Nx + i) * (Ny + 1) + j;
            float sum = 0;
            if (i > 0) sum += Wx[fx] * x[c - Ny];
            if (i < Nx - 1) sum += Wx[fx + Ny] * x[c + Ny];
            if (j > 0) sum += Wy[fy] * x[c - 1];
            if (j < Ny - 1) sum += Wy[fy + 1] * x[c + 1];
            if (k > 0) sum += Wz[c] * x[c - slab];
            if (k < Nz - 1) sum += Wz[c + slab] * x[c + slab];
            return sum;
        }

        // One red (0) or black (1) Gauss–Seidel half sweep on X for B.
        public void Smooth(int color)
        {
            int slab = Nx * Ny;
            Parallel.For(0, Nz, k =>
            {
                for (int i = 0; i < Nx; i++)
                {
                    int column = (k * Nx + i) * Ny;
                    int first = First[k * Nx + i];
                    int start = first + ((i + first + k + color) & 1);
                    for (int j = start; j < Ny; j += 2)
                    {
                        int c = column + j;
                        if (Diag[c] > 0) X[c] = (B[c] + Neighbors(X, i, j, k, c, slab)) * InvDiag[c];
                    }
                }
            });
        }

        // The coarse level: 2 × 2 × 2 cells into one, the faces between them summed.
        public Level Coarsen()
        {
            int cx = (Nx + 1) / 2, cy = (Ny + 1) / 2, cz = (Nz + 1) / 2;
            var coarse = new Level(cx, cy, cz);
            Parallel.For(0, cz, kc =>
            {
                for (int ic = 0; ic <= cx; ic++)
                {
                    int i = ic == cx ? Nx : 2 * ic;
                    for (int jc = 0; jc < cy; jc++)
                    {
                        float sum = 0;
                        for (int k = 2 * kc; k < Math.Min(2 * kc + 2, Nz); k++)
                        {
                            for (int j = 2 * jc; j < Math.Min(2 * jc + 2, Ny); j++) sum += Wx[(k * (Nx + 1) + i) * Ny + j];
                        }
                        coarse.Wx[(kc * (cx + 1) + ic) * cy + jc] = sum;
                    }
                }
                for (int ic = 0; ic < cx; ic++)
                {
                    for (int jc = 0; jc <= cy; jc++)
                    {
                        int j = jc == cy ? Ny : 2 * jc;
                        float sum = 0;
                        for (int k = 2 * kc; k < Math.Min(2 * kc + 2, Nz); k++)
                        {
                            for (int i = 2 * ic; i < Math.Min(2 * ic + 2, Nx); i++) sum += Wy[(k * Nx + i) * (Ny + 1) + j];
                        }
                        coarse.Wy[(kc * cx + ic) * (cy + 1) + jc] = sum;
                    }
                }
            });
            Parallel.For(0, cz + 1, kc =>
            {
                int k = kc == cz ? Nz : 2 * kc;
                for (int ic = 0; ic < cx; ic++)
                {
                    for (int jc = 0; jc < cy; jc++)
                    {
                        float sum = 0;
                        for (int i = 2 * ic; i < Math.Min(2 * ic + 2, Nx); i++)
                        {
                            for (int j = 2 * jc; j < Math.Min(2 * jc + 2, Ny); j++) sum += Wz[(k * Nx + i) * Ny + j];
                        }
                        coarse.Wz[(kc * cx + ic) * cy + jc] = sum;
                    }
                }
            });
            coarse.Finish();
            return coarse;
        }

        // The residual R = B − A·X.
        public void Residual()
        {
            Multiply(X, R);
            Parallel.For(0, Nz, k =>
            {
                for (int c = k * Nx * Ny, end = c + Nx * Ny; c < end; c++) R[c] = Diag[c] > 0 ? B[c] - R[c] : 0;
            });
        }

        // The coarse level's B: the sum of its children's values (each coarse slab from its own two fine ones).
        public void Restrict(float[] fine, Level coarse)
        {
            Array.Clear(coarse.B);
            int cx = coarse.Nx, cy = coarse.Ny;
            Parallel.For(0, coarse.Nz, kc =>
            {
                for (int k = 2 * kc; k < Math.Min(2 * kc + 2, Nz); k++)
                {
                    for (int i = 0; i < Nx; i++)
                    {
                        int column = (k * Nx + i) * Ny;
                        int target = (kc * cx + (i >> 1)) * cy;
                        for (int j = First[k * Nx + i]; j < Ny; j++) coarse.B[target + (j >> 1)] += fine[column + j];
                    }
                }
            });
        }

        public void Prolong(Level coarse, float[] fine)
        {
            int cx = coarse.Nx, cy = coarse.Ny;
            Parallel.For(0, Nz, k =>
            {
                for (int i = 0; i < Nx; i++)
                {
                    int column = (k * Nx + i) * Ny;
                    int source = ((k >> 1) * cx + (i >> 1)) * cy;
                    for (int j = First[k * Nx + i]; j < Ny; j++)
                    {
                        if (Diag[column + j] > 0) fine[column + j] += coarse.X[source + (j >> 1)];
                    }
                }
            });
        }
    }
}
