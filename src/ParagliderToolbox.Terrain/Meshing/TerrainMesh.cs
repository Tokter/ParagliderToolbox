using System.Numerics;

namespace ParagliderToolbox.Terrain.Meshing;

/// <summary>
/// A tile's triangle mesh, relative to the tile's center (<see cref="Origin"/>; heights stay absolute): positions,
/// normals, texture coordinates (0,0 the texture's top left, north-west), colors for faceted meshes, and indices
/// (counter-clockwise seen from above).
/// </summary>
public sealed class TerrainMesh
{
    internal TerrainMesh(Vector3 origin, Vector3[] positions, Vector3[] normals, Vector2[] texCoords, Vector4[]? colors, uint[] indices)
    {
        Origin = origin;
        Positions = positions;
        Normals = normals;
        TexCoords = texCoords;
        Colors = colors;
        Indices = indices;
    }

    /// <summary>Gets where the mesh's origin is in the terrain (the tile's center at height 0).</summary>
    public Vector3 Origin { get; }

    /// <summary>Gets the positions, relative to <see cref="Origin"/>.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Gets the normals.</summary>
    public Vector3[] Normals { get; }

    /// <summary>Gets the texture coordinates.</summary>
    public Vector2[] TexCoords { get; }

    /// <summary>Gets the vertex colors (linear RGBA) of a faceted mesh, or <c>null</c>.</summary>
    public Vector4[]? Colors { get; }

    /// <summary>Gets the indices, three per triangle.</summary>
    public uint[] Indices { get; }

    /// <summary>Gets the number of triangles.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// Builds the mesh of <paramref name="tile"/>: a grid of its samples with skirts (a band hanging
    /// <paramref name="skirtDepth"/> down from every edge, hiding the cracks to neighbors of other levels).
    /// </summary>
    /// <param name="tile">The tile.</param>
    /// <param name="shading">Smooth (shared vertices, texture) or faceted (flat triangles, each in the texture's average color over it).</param>
    /// <param name="skirtDepth">How far the skirts hang down (m); 0 for none.</param>
    /// <param name="texture">The decoded texture for faceted colors (see <see cref="TerrainTile.DecodeTexture"/>), or <c>null</c> for gray.</param>
    public static TerrainMesh Build(TerrainTile tile, TerrainShading shading, double skirtDepth, uint[]? texture = null) =>
        shading == TerrainShading.Faceted ? BuildFaceted(tile, skirtDepth, texture) : BuildSmooth(tile, skirtDepth);

    private static Vector3 Normal(TerrainTile tile, int column, int row)
    {
        double s = tile.Rect.Spacing;
        double dx = (tile.Height(column + 1, row) - tile.Height(column - 1, row)) / (2 * s);
        double dz = (tile.Height(column, row + 1) - tile.Height(column, row - 1)) / (2 * s);
        return Vector3.Normalize(new Vector3((float)-dx, 1, (float)-dz));
    }

    private static TerrainMesh BuildSmooth(TerrainTile tile, double skirtDepth)
    {
        var rect = tile.Rect;
        int columns = rect.Columns, rows = rect.Rows;
        var origin = new Vector3((float)rect.CenterX, 0, (float)rect.CenterZ);
        bool skirts = skirtDepth > 0;
        int edge = 2 * (columns + rows) - 4;
        int count = columns * rows + (skirts ? edge : 0);
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        float halfW = (float)(rect.Width / 2), halfD = (float)(rect.Depth / 2);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int i = row * columns + column;
                float u = columns > 1 ? (float)column / (columns - 1) : 0, v = rows > 1 ? (float)row / (rows - 1) : 0;
                positions[i] = new Vector3(-halfW + u * 2 * halfW, tile.Height(column, row), -halfD + v * 2 * halfD);
                normals[i] = Normal(tile, column, row);
                uvs[i] = new Vector2(u, v);
            }
        }

        var indices = new List<uint>((columns - 1) * (rows - 1) * 6 + (skirts ? edge * 6 : 0));
        for (int row = 0; row + 1 < rows; row++)
        {
            for (int column = 0; column + 1 < columns; column++)
            {
                uint a = (uint)(row * columns + column), b = a + 1, c = a + (uint)columns, d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        }

        if (skirts)
        {
            // The edge ring clockwise seen from above (north edge west to east, east edge north to south, ...), each
            // vertex with a copy hanging below it; the band faces outwards.
            var ring = EdgeRing(columns, rows);
            int first = columns * rows;
            for (int k = 0; k < ring.Count; k++)
            {
                int top = ring[k];
                positions[first + k] = positions[top] - new Vector3(0, (float)skirtDepth, 0);
                normals[first + k] = normals[top];
                uvs[first + k] = uvs[top];
            }
            for (int k = 0; k < ring.Count; k++)
            {
                int k1 = (k + 1) % ring.Count;
                uint topA = (uint)ring[k], topB = (uint)ring[k1], bottomA = (uint)(first + k), bottomB = (uint)(first + k1);
                indices.AddRange([topA, topB, bottomA, topB, bottomB, bottomA]);
            }
        }
        return new TerrainMesh(origin, positions, normals, uvs, null, indices.ToArray());
    }

    private static TerrainMesh BuildFaceted(TerrainTile tile, double skirtDepth, uint[]? texture)
    {
        var rect = tile.Rect;
        int columns = rect.Columns, rows = rect.Rows;
        var origin = new Vector3((float)rect.CenterX, 0, (float)rect.CenterZ);
        float halfW = (float)(rect.Width / 2), halfD = (float)(rect.Depth / 2);
        Vector3 Point(int column, int row) => new(
            -halfW + (columns > 1 ? (float)column / (columns - 1) : 0) * 2 * halfW, tile.Height(column, row),
            -halfD + (rows > 1 ? (float)row / (rows - 1) : 0) * 2 * halfD);
        Vector2 Uv(int column, int row) => new(columns > 1 ? (float)column / (columns - 1) : 0, rows > 1 ? (float)row / (rows - 1) : 0);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var colors = new List<Vector4>();
        void Triangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 t0, Vector2 t1, Vector2 t2)
        {
            var normal = Vector3.Cross(p1 - p0, p2 - p0);
            normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY;
            var color = ColorAt(tile, texture, t0, t1, t2);
            positions.AddRange([p0, p1, p2]);
            normals.AddRange([normal, normal, normal]);
            uvs.AddRange([t0, t1, t2]);
            colors.AddRange([color, color, color]);
        }

        for (int row = 0; row + 1 < rows; row++)
        {
            for (int column = 0; column + 1 < columns; column++)
            {
                Vector3 a = Point(column, row), b = Point(column + 1, row), c = Point(column, row + 1), d = Point(column + 1, row + 1);
                Vector2 ta = Uv(column, row), tb = Uv(column + 1, row), tc = Uv(column, row + 1), td = Uv(column + 1, row + 1);
                Triangle(a, c, b, ta, tc, tb);
                Triangle(b, c, d, tb, tc, td);
            }
        }
        if (skirtDepth > 0)
        {
            var ring = EdgeRing(columns, rows);
            var down = new Vector3(0, (float)skirtDepth, 0);
            for (int k = 0; k < ring.Count; k++)
            {
                int ia = ring[k], ib = ring[(k + 1) % ring.Count];
                var pa = Point(ia % columns, ia / columns);
                var pb = Point(ib % columns, ib / columns);
                var ua = Uv(ia % columns, ia / columns);
                var ub = Uv(ib % columns, ib / columns);
                Triangle(pa, pb, pa - down, ua, ub, ua);
                Triangle(pb, pb - down, pa - down, ub, ub, ua);
            }
        }
        var indices = new uint[positions.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = (uint)i;
        return new TerrainMesh(origin, positions.ToArray(), normals.ToArray(), uvs.ToArray(), colors.ToArray(), indices);
    }

    // The face color: the texture averaged over the triangle (its center, corners and edge midpoints), in linear light,
    // so neighboring faces don't flicker between the colors of single pixels.
    private static Vector4 ColorAt(TerrainTile tile, uint[]? texture, Vector2 t0, Vector2 t1, Vector2 t2)
    {
        if (texture is null || tile.TextureWidth == 0) return new Vector4(0.45f, 0.45f, 0.45f, 1);
        var sum = Vector3.Zero;
        var center = (t0 + t1 + t2) / 3;
        ReadOnlySpan<Vector2> points = [center, t0, t1, t2, (t0 + t1) / 2, (t1 + t2) / 2, (t2 + t0) / 2];
        foreach (var uv in points)
        {
            int x = Math.Clamp((int)(uv.X * tile.TextureWidth), 0, tile.TextureWidth - 1);
            int y = Math.Clamp((int)(uv.Y * tile.TextureHeight), 0, tile.TextureHeight - 1);
            uint c = texture[y * tile.TextureWidth + x];
            sum += new Vector3(SrgbToLinear(c & 0xFF), SrgbToLinear((c >> 8) & 0xFF), SrgbToLinear((c >> 16) & 0xFF));
        }
        return new Vector4(sum / points.Length, 1);
    }

    private static float SrgbToLinear(uint value)
    {
        float c = value / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    // The indices of the edge vertices clockwise seen from above (+Y), starting at the north-west corner.
    private static List<int> EdgeRing(int columns, int rows)
    {
        var ring = new List<int>(2 * (columns + rows));
        for (int c = 0; c < columns - 1; c++) ring.Add(c);
        for (int r = 0; r < rows - 1; r++) ring.Add(r * columns + columns - 1);
        for (int c = columns - 1; c > 0; c--) ring.Add((rows - 1) * columns + c);
        for (int r = rows - 1; r > 0; r--) ring.Add(r * columns);
        return ring;
    }
}
