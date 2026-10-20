using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ParagliderToolbox.Terrain.Export;
using ParagliderToolbox.Terrain.Geodesy;
using SkiaSharp;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>
/// Writes and reads a <see cref="FeatureSet"/>: <c>features.json</c> (the format of docs/FeaturesFormat.md), the ground
/// cover map as an 8-bit PNG of class numbers (and a colored one to look at), GeoJSON in latitude and longitude for map
/// tools, and the compressed bytes a project keeps.
/// </summary>
public static class FeatureFiles
{
    /// <summary>The format's name in the file.</summary>
    public const string Format = "paraglider-toolbox-features";

    /// <summary>The format's version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The file names of an export.</summary>
    public const string JsonFile = "features.json", CoverFile = "landcover.png", CoverColorsFile = "landcover_colors.png", GeoJsonFile = "features.geojson",
        AttributionFile = "ATTRIBUTION.txt";

    /// <summary>Writes the features into <paramref name="folder"/>; returns the files written.</summary>
    public static List<string> Export(FeatureSet features, string folder, bool geoJson = true)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        void Write(string name, byte[] bytes)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            written.Add(path);
        }
        Write(JsonFile, ToJson(features, embedCovers: false));
        if (features.Covers is { } covers)
        {
            Write(CoverFile, Heightmaps.ToPng(covers.Cells, covers.Columns, covers.Rows));
            Write(CoverColorsFile, CoverColors(covers));
        }
        if (geoJson) Write(GeoJsonFile, ToGeoJson(features));
        Write(AttributionFile, Encoding.UTF8.GetBytes(features.Attribution + "\n"));
        return written;
    }

    /// <summary>Gets the features as compressed bytes (the JSON with the cover map in it, gzipped), as a project keeps them.</summary>
    public static byte[] ToBytes(FeatureSet features)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(ToJson(features, embedCovers: true));
        return output.ToArray();
    }

    /// <summary>Reads features from <see cref="ToBytes"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes aren't features.</exception>
    public static FeatureSet FromBytes(byte[] bytes)
    {
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var json = new MemoryStream();
        input.CopyTo(json);
        return FromJson(json.ToArray());
    }

    /// <summary>Writes <c>features.json</c>: the cover map's cells in it (zlib, base64) when <paramref name="embedCovers"/>, else the name of its PNG.</summary>
    public static byte[] ToJson(FeatureSet features, bool embedCovers)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", FormatVersion);
            w.WriteString("generator", "Paraglider Toolbox");
            w.WriteStartObject("origin");
            w.WriteNumber("latitude", features.Origin.Latitude);
            w.WriteNumber("longitude", features.Origin.Longitude);
            w.WriteEndObject();
            w.WriteStartObject("frame");
            w.WriteString("projection", "transverse Mercator on WGS84, central meridian through the origin, scale 1");
            w.WriteString("axes", "x east, y up, z south; meters; y is the height above sea level");
            w.WriteEndObject();
            w.WriteStartObject("area");
            Number(w, "x0", features.X0);
            Number(w, "z0", features.Z0);
            Number(w, "width", features.Width);
            Number(w, "depth", features.Depth);
            w.WriteEndObject();
            Number(w, "spacing", features.Spacing);
            Number(w, "surfaceShare", features.SurfaceShare, 3);
            Number(w, "elapsed", features.Elapsed.TotalSeconds, 1);
            w.WriteString("attribution", features.Attribution);
            w.WriteStartArray("notes");
            foreach (string note in features.Notes) w.WriteStringValue(note);
            w.WriteEndArray();

            w.WriteStartArray("trees");
            foreach (var tree in features.Trees)
            {
                w.WriteStartObject();
                Point(w, tree.Position);
                Number(w, "height", tree.Height, 1);
                Number(w, "crownRadius", tree.CrownRadius, 1);
                w.WriteString("kind", Name(tree.Kind));
                if (tree.Estimated) w.WriteBoolean("estimated", true);
                if (tree.Origin != FeatureOrigin.Image) w.WriteString("origin", Name(tree.Origin));
                TagsOf(w, tree.Tags);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("roads");
            foreach (var road in features.Roads)
            {
                w.WriteStartObject();
                w.WriteString("kind", Name(road.Kind));
                w.WriteString("surface", Name(road.Surface));
                Number(w, "width", road.Width, 1);
                Number(w, "length", road.Length, 1);
                w.WriteNumber("start", road.Start);
                w.WriteNumber("end", road.End);
                w.WriteString("origin", Name(road.Origin));
                if (road.Structure != RoadStructure.Ground) w.WriteString("structure", Name(road.Structure));
                if (road.Class is { } roadClass) w.WriteString("class", roadClass);
                TagsOf(w, road.Tags);
                w.WriteStartArray("points");
                foreach (var p in road.Points) Triple(w, p);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("junctions");
            foreach (var junction in features.Junctions) Triple(w, junction);
            w.WriteEndArray();

            w.WriteStartArray("buildings");
            foreach (var building in features.Buildings)
            {
                w.WriteStartObject();
                Point(w, building.Position);
                Number(w, "length", building.Length, 1);
                Number(w, "width", building.Width, 1);
                Number(w, "angle", building.Angle, 1);
                Number(w, "height", building.Height, 1);
                Number(w, "eaves", building.Eaves, 1);
                w.WriteString("roof", Name(building.Roof));
                w.WriteString("origin", Name(building.Origin));
                if (!building.Measured) w.WriteBoolean("measured", false);
                if (building.Class is { } buildingClass) w.WriteString("class", buildingClass);
                TagsOf(w, building.Tags);
                w.WriteStartArray("footprint");
                foreach (var p in building.Footprint) Pair(w, p);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("rocks");
            foreach (var rock in features.Rocks)
            {
                w.WriteStartObject();
                Point(w, rock.Position);
                Number(w, "radius", rock.Radius, 2);
                Number(w, "height", rock.Height, 2);
                if (rock.Origin != FeatureOrigin.Image) w.WriteString("origin", Name(rock.Origin));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("areas");
            foreach (var area in features.Areas)
            {
                w.WriteStartObject();
                w.WriteString("kind", Name(area.Kind));
                Number(w, "size", area.Size, 0);
                w.WriteString("origin", Name(area.Origin));
                if (area.Class is { } areaClass) w.WriteString("class", areaClass);
                TagsOf(w, area.Tags);
                w.WriteStartArray("outline");
                foreach (var p in area.Outline) Pair(w, p);
                w.WriteEndArray();
                w.WriteStartArray("holes");
                foreach (var hole in area.Holes)
                {
                    w.WriteStartArray();
                    foreach (var p in hole) Pair(w, p);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("lines");
            foreach (var line in features.Lines)
            {
                w.WriteStartObject();
                w.WriteString("kind", Name(line.Kind));
                Number(w, "width", line.Width, 1);
                Number(w, "height", line.Height, 1);
                Number(w, "length", line.Length, 1);
                if (line.Structure != RoadStructure.Ground) w.WriteString("structure", Name(line.Structure));
                if (line.Class is { } lineClass) w.WriteString("class", lineClass);
                TagsOf(w, line.Tags);
                w.WriteStartArray("points");
                foreach (var p in line.Points) Triple(w, p);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("landmarks");
            foreach (var landmark in features.Landmarks)
            {
                w.WriteStartObject();
                Point(w, landmark.Position);
                w.WriteString("kind", Name(landmark.Kind));
                if (landmark.Name is { } name) w.WriteString("name", name);
                if (landmark.Elevation is { } elevation) Number(w, "elevation", elevation, 1);
                if (landmark.Height is { } height) Number(w, "height", height, 1);
                if (landmark.Class is { } landmarkClass) w.WriteString("class", landmarkClass);
                TagsOf(w, landmark.Tags);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (features.Covers is { } covers)
            {
                w.WriteStartObject("landCover");
                Number(w, "x0", covers.X0);
                Number(w, "z0", covers.Z0);
                Number(w, "spacing", covers.Spacing);
                w.WriteNumber("columns", covers.Columns);
                w.WriteNumber("rows", covers.Rows);
                w.WriteStartArray("classes");
                foreach (var cover in Enum.GetValues<GroundCover>()) w.WriteStringValue(Name(cover));
                w.WriteEndArray();
                if (embedCovers)
                {
                    using var zipped = new MemoryStream();
                    using (var zlib = new ZLibStream(zipped, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(covers.Cells);
                    w.WriteBase64String("cells", zipped.ToArray());
                }
                else
                {
                    w.WriteString("file", CoverFile);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Reads <c>features.json</c> (with the cover map in it; one in a PNG beside it isn't read).</summary>
    /// <exception cref="InvalidDataException">The JSON isn't features.</exception>
    public static FeatureSet FromJson(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != Format) throw new InvalidDataException("Not a feature file.");
            if (root.GetProperty("version").GetInt32() > FormatVersion) throw new InvalidDataException("The feature file is of a newer version.");
            var origin = root.GetProperty("origin");
            var area = root.GetProperty("area");

            var trees = root.GetProperty("trees").EnumerateArray().Select(t => new TreeFeature(Vector(t), Float(t, "height"), Float(t, "crownRadius"),
                Parse<TreeKind>(t.GetProperty("kind")), t.TryGetProperty("estimated", out var e) && e.GetBoolean())
            {
                Origin = OriginOf(t),
                Tags = ReadTags(t),
            }).ToList();
            var roads = root.GetProperty("roads").EnumerateArray().Select(r => new RoadFeature(
                r.GetProperty("points").EnumerateArray().Select(Triple).ToList(), Float(r, "width"), Parse<RoadSurface>(r.GetProperty("surface")),
                Parse<RoadKind>(r.GetProperty("kind")), r.GetProperty("start").GetInt32(), r.GetProperty("end").GetInt32())
            {
                Origin = OriginOf(r),
                Structure = StructureOf(r),
                Class = ClassOf(r),
                Tags = ReadTags(r),
            }).ToList();
            var buildings = root.GetProperty("buildings").EnumerateArray().Select(b => new BuildingFeature(Vector(b), Float(b, "length"), Float(b, "width"),
                Float(b, "angle"), Float(b, "height"), Float(b, "eaves"), Parse<RoofShape>(b.GetProperty("roof")),
                b.GetProperty("footprint").EnumerateArray().Select(Pair).ToList())
            {
                Origin = OriginOf(b),
                Measured = !b.TryGetProperty("measured", out var measured) || measured.GetBoolean(),
                Class = ClassOf(b),
                Tags = ReadTags(b),
            }).ToList();
            var rocks = root.GetProperty("rocks").EnumerateArray().Select(r => new RockFeature(Vector(r), Float(r, "radius"), Float(r, "height")) { Origin = OriginOf(r) }).ToList();
            var areas = root.GetProperty("areas").EnumerateArray().Select(a => new AreaFeature(Parse<AreaKind>(a.GetProperty("kind")),
                a.GetProperty("outline").EnumerateArray().Select(Pair).ToList(),
                a.GetProperty("holes").EnumerateArray().Select(h => (IReadOnlyList<Vector2>)h.EnumerateArray().Select(Pair).ToList()).ToList())
            {
                Origin = OriginOf(a),
                Class = ClassOf(a),
                Tags = ReadTags(a),
            }).ToList();
            var lines = !root.TryGetProperty("lines", out var lineArray) ? [] : lineArray.EnumerateArray().Select(l => new LineFeature(
                l.GetProperty("points").EnumerateArray().Select(Triple).ToList(), Parse<LineKind>(l.GetProperty("kind")), Float(l, "width"), Float(l, "height"))
            {
                Structure = StructureOf(l),
                Class = ClassOf(l),
                Tags = ReadTags(l),
            }).ToList();
            var landmarks = !root.TryGetProperty("landmarks", out var landmarkArray) ? [] : landmarkArray.EnumerateArray().Select(l =>
                new LandmarkFeature(Vector(l), Parse<LandmarkKind>(l.GetProperty("kind")))
                {
                    Name = l.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Elevation = l.TryGetProperty("elevation", out var elevation) ? elevation.GetSingle() : null,
                    Height = l.TryGetProperty("height", out var height) ? height.GetSingle() : null,
                    Class = ClassOf(l),
                    Tags = ReadTags(l),
                }).ToList();

            CoverMap? covers = null;
            if (root.TryGetProperty("landCover", out var land) && land.TryGetProperty("cells", out var cells))
            {
                int columns = land.GetProperty("columns").GetInt32(), rows = land.GetProperty("rows").GetInt32();
                using var input = new ZLibStream(new MemoryStream(cells.GetBytesFromBase64()), CompressionMode.Decompress);
                var bytes = new byte[columns * rows];
                input.ReadExactly(bytes);
                covers = new CoverMap(land.GetProperty("x0").GetDouble(), land.GetProperty("z0").GetDouble(), land.GetProperty("spacing").GetDouble(),
                    columns, rows, bytes);
            }

            return new FeatureSet
            {
                Origin = new GeoPoint(origin.GetProperty("latitude").GetDouble(), origin.GetProperty("longitude").GetDouble()),
                X0 = area.GetProperty("x0").GetDouble(),
                Z0 = area.GetProperty("z0").GetDouble(),
                Width = area.GetProperty("width").GetDouble(),
                Depth = area.GetProperty("depth").GetDouble(),
                Spacing = root.GetProperty("spacing").GetDouble(),
                SurfaceShare = root.GetProperty("surfaceShare").GetDouble(),
                Elapsed = TimeSpan.FromSeconds(root.GetProperty("elapsed").GetDouble()),
                Attribution = root.GetProperty("attribution").GetString() ?? "",
                Notes = root.GetProperty("notes").EnumerateArray().Select(n => n.GetString() ?? "").ToList(),
                Trees = trees,
                Roads = roads,
                Junctions = root.GetProperty("junctions").EnumerateArray().Select(Triple).ToList(),
                Buildings = buildings,
                Rocks = rocks,
                Areas = areas,
                Lines = lines,
                Landmarks = landmarks,
                Covers = covers,
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or EndOfStreamException)
        {
            throw new InvalidDataException($"The feature file can't be read: {e.Message}", e);
        }
    }

    /// <summary>
    /// Gets the features as GeoJSON (WGS84 longitude, latitude; heights as a third coordinate): trees, rocks and
    /// junctions as points, roads as lines, buildings and areas as polygons, each with its properties.
    /// </summary>
    public static byte[] ToGeoJson(FeatureSet features)
    {
        var frame = new LocalFrame(features.Origin);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            void Position(double x, double z, double? y = null)
            {
                var geo = frame.ToGeo(x, z);
                w.WriteStartArray();
                w.WriteRawValue(Round(geo.Longitude, 7));
                w.WriteRawValue(Round(geo.Latitude, 7));
                if (y is { } height) w.WriteRawValue(Round(height, 2));
                w.WriteEndArray();
            }
            void Ring(IReadOnlyList<Vector2> ring)
            {
                w.WriteStartArray();
                // GeoJSON's rings are closed, outlines counterclockwise.
                foreach (var p in ring) Position(p.X, p.Y);
                Position(ring[0].X, ring[0].Y);
                w.WriteEndArray();
            }
            // The map's tags beside the toolbox's own properties (theirs keep their names).
            void Properties(IReadOnlyDictionary<string, string>? tags)
            {
                foreach (var (key, value) in tags ?? new Dictionary<string, string>())
                {
                    if (key is not ("feature" or "kind" or "class" or "width" or "height" or "surface" or "size" or "roof" or "eaves" or "radius" or "origin")) w.WriteString(key, value);
                }
            }
            void Feature(string type, Action geometry, Action properties)
            {
                w.WriteStartObject();
                w.WriteString("type", "Feature");
                w.WriteStartObject("geometry");
                w.WriteString("type", type);
                w.WritePropertyName("coordinates");
                geometry();
                w.WriteEndObject();
                w.WriteStartObject("properties");
                properties();
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteStartObject();
            w.WriteString("type", "FeatureCollection");
            w.WriteStartArray("features");
            foreach (var area in features.Areas)
            {
                Feature("Polygon", () =>
                {
                    w.WriteStartArray();
                    Ring(area.Outline);
                    foreach (var hole in area.Holes) Ring(hole);
                    w.WriteEndArray();
                }, () =>
                {
                    w.WriteString("feature", "area");
                    w.WriteString("kind", Name(area.Kind));
                    Number(w, "size", area.Size, 0);
                    if (area.Class is { } areaClass) w.WriteString("class", areaClass);
                    Properties(area.Tags);
                });
            }
            foreach (var building in features.Buildings)
            {
                Feature("Polygon", () =>
                {
                    w.WriteStartArray();
                    Ring(building.Footprint);
                    w.WriteEndArray();
                }, () =>
                {
                    w.WriteString("feature", "building");
                    Number(w, "height", building.Height, 1);
                    Number(w, "eaves", building.Eaves, 1);
                    w.WriteString("roof", Name(building.Roof));
                    Properties(building.Tags);
                });
            }
            foreach (var road in features.Roads)
            {
                Feature("LineString", () =>
                {
                    w.WriteStartArray();
                    foreach (var p in road.Points) Position(p.X, p.Z, p.Y);
                    w.WriteEndArray();
                }, () =>
                {
                    w.WriteString("feature", "road");
                    w.WriteString("kind", Name(road.Kind));
                    w.WriteString("surface", Name(road.Surface));
                    Number(w, "width", road.Width, 1);
                    w.WriteString("origin", Name(road.Origin));
                    if (road.Structure != RoadStructure.Ground) w.WriteString("structure", Name(road.Structure));
                    if (road.Class is { } roadClass) w.WriteString("class", roadClass);
                    Properties(road.Tags);
                });
            }
            foreach (var tree in features.Trees)
            {
                Feature("Point", () => Position(tree.Position.X, tree.Position.Z, tree.Position.Y), () =>
                {
                    w.WriteString("feature", "tree");
                    w.WriteString("kind", Name(tree.Kind));
                    Number(w, "height", tree.Height, 1);
                    Number(w, "crownRadius", tree.CrownRadius, 1);
                    if (tree.Estimated) w.WriteBoolean("estimated", true);
                });
            }
            foreach (var rock in features.Rocks)
            {
                Feature("Point", () => Position(rock.Position.X, rock.Position.Z, rock.Position.Y), () =>
                {
                    w.WriteString("feature", "rock");
                    Number(w, "radius", rock.Radius, 2);
                    Number(w, "height", rock.Height, 2);
                });
            }
            foreach (var line in features.Lines)
            {
                Feature("LineString", () =>
                {
                    w.WriteStartArray();
                    foreach (var p in line.Points) Position(p.X, p.Z, p.Y);
                    w.WriteEndArray();
                }, () =>
                {
                    w.WriteString("feature", "line");
                    w.WriteString("kind", Name(line.Kind));
                    Number(w, "width", line.Width, 1);
                    Number(w, "height", line.Height, 1);
                    if (line.Class is { } lineClass) w.WriteString("class", lineClass);
                    Properties(line.Tags);
                });
            }
            foreach (var landmark in features.Landmarks)
            {
                Feature("Point", () => Position(landmark.Position.X, landmark.Position.Z, landmark.Position.Y), () =>
                {
                    w.WriteString("feature", "landmark");
                    w.WriteString("kind", Name(landmark.Kind));
                    if (landmark.Class is { } landmarkClass) w.WriteString("class", landmarkClass);
                    Properties(landmark.Tags);
                });
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Gets the cover map as a PNG in the classes' colors (<see cref="GroundCovers.MapColors"/>), to look at.</summary>
    public static byte[] CoverColors(CoverMap covers)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(covers.Columns, covers.Rows, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = new uint[covers.Cells.Length];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = GroundCovers.MapColors[Math.Min(covers.Cells[i], GroundCovers.MapColors.Count - 1)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bitmap.GetPixelSpan());
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Gets the name of a value in the files: camelCase ("mainRoad").</summary>
    public static string Name<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void TagsOf(Utf8JsonWriter w, IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is not { Count: > 0 }) return;
        w.WriteStartObject("tags");
        foreach (var (key, value) in tags) w.WriteString(key, value);
        w.WriteEndObject();
    }

    private static IReadOnlyDictionary<string, string>? ReadTags(JsonElement element) =>
        element.TryGetProperty("tags", out var tags) ? tags.EnumerateObject().ToDictionary(t => t.Name, t => t.Value.GetString() ?? "") : null;

    private static FeatureOrigin OriginOf(JsonElement element) =>
        element.TryGetProperty("origin", out var origin) ? Parse<FeatureOrigin>(origin) : FeatureOrigin.Image;

    private static RoadStructure StructureOf(JsonElement element) =>
        element.TryGetProperty("structure", out var structure) ? Parse<RoadStructure>(structure) : RoadStructure.Ground;

    private static string? ClassOf(JsonElement element) => element.TryGetProperty("class", out var value) ? value.GetString() : null;

    private static T Parse<T>(JsonElement element) where T : struct, Enum =>
        Enum.TryParse<T>(element.GetString(), ignoreCase: true, out var value) ? value : default;

    private static float Float(JsonElement element, string name) => element.GetProperty(name).GetSingle();

    private static Vector3 Vector(JsonElement element) => new(Float(element, "x"), Float(element, "y"), Float(element, "z"));

    private static Vector3 Triple(JsonElement element) => new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());

    private static Vector2 Pair(JsonElement element) => new(element[0].GetSingle(), element[1].GetSingle());

    private static string Round(double value, int digits) => Math.Round(value, digits).ToString("R", CultureInfo.InvariantCulture);

    private static void Number(Utf8JsonWriter w, string name, double value, int digits = 2)
    {
        w.WritePropertyName(name);
        w.WriteRawValue(Round(value, digits));
    }

    private static void Point(Utf8JsonWriter w, Vector3 p)
    {
        Number(w, "x", p.X);
        Number(w, "y", p.Y);
        Number(w, "z", p.Z);
    }

    private static void Triple(Utf8JsonWriter w, Vector3 p)
    {
        w.WriteStartArray();
        w.WriteRawValue(Round(p.X, 2));
        w.WriteRawValue(Round(p.Y, 2));
        w.WriteRawValue(Round(p.Z, 2));
        w.WriteEndArray();
    }

    private static void Pair(Utf8JsonWriter w, Vector2 p)
    {
        w.WriteStartArray();
        w.WriteRawValue(Round(p.X, 2));
        w.WriteRawValue(Round(p.Y, 2));
        w.WriteEndArray();
    }
}
