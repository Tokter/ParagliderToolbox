using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Rigging;
using ParagliderToolbox.Paraglider.Texturing;
using SkiaSharp;

namespace ParagliderToolbox.Paraglider.Export;

/// <summary>A recorded motion of the proxy: node positions over time, e.g. from the simulation.</summary>
/// <param name="Name">The animation's name.</param>
/// <param name="Times">The time of each frame (s).</param>
/// <param name="Frames">The node positions of each frame.</param>
public sealed record ProxyAnimation(string Name, float[] Times, Vector3[][] Frames);

/// <summary>Options of the glTF export.</summary>
public sealed record GlbOptions
{
    /// <summary>Gets whether the mesh is skinned to the proxy's joints (otherwise static).</summary>
    public bool Skinned { get; init; } = true;

    /// <summary>Gets whether a line mesh of the proxy (its fabric, ribs and lines) is included, skinned to its own joints.</summary>
    public bool IncludeProxyCage { get; init; } = true;

    /// <summary>Gets an animation to bake into the joints, or null.</summary>
    public ProxyAnimation? Animation { get; init; }

    /// <summary>Gets whether only the proxy (cage and joints) is written, without the high resolution mesh.</summary>
    public bool ProxyOnly { get; init; }
}

/// <summary>
/// Writes a generated glider for Blender, Godot and games:
/// <list type="bullet">
/// <item>glTF 2.0 binary (.glb): the high resolution parts with PBR materials and the embedded canopy textures, skinned
/// to a skeleton with one joint per proxy node (bind pose: the nodes' frames). Animate the joints (or bake a simulation
/// into them) and the canopy follows. Both Blender and Godot import it directly.</item>
/// <item>The proxy JSON (<see cref="ProxyModel.ToJson"/>): the simulation definition for a game; node names match the joint names.</item>
/// <item>Wavefront OBJ + MTL + PNG textures: the static mesh, for tools without glTF.</item>
/// <item>A line plan CSV: every line with its level, row, diameter and length.</item>
/// </list>
/// </summary>
public static class GliderExporter
{
    private const string Generator = "Paraglider Toolbox";

    /// <summary>Writes every export of <paramref name="model"/> into <paramref name="folder"/>, named after <paramref name="baseName"/>.</summary>
    /// <returns>The written files.</returns>
    public static List<string> ExportAll(GliderModel model, string folder, string baseName, ProxyAnimation? animation = null)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        string Write(string name, byte[] data)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, data);
            files.Add(path);
            return path;
        }
        Write($"{baseName}.glb", ToGlb(model, new GlbOptions { Animation = animation }));
        Write($"{baseName}_proxy.glb", ToGlb(model, new GlbOptions { ProxyOnly = true, Animation = animation }));
        Write($"{baseName}_proxy.json", Encoding.UTF8.GetBytes(model.Proxy.Model.ToJson()));
        Write($"{baseName}_lineplan.csv", Encoding.UTF8.GetBytes(ToLinePlanCsv(model.Rigging)));
        files.AddRange(WriteObj(model, folder, baseName));
        return files;
    }

    #region glTF

    /// <summary>Writes the model as glTF 2.0 binary.</summary>
    public static byte[] ToGlb(GliderModel model, GlbOptions? options = null)
    {
        options ??= new GlbOptions();
        var gltf = new GltfWriter();
        var proxy = model.Proxy.Model;
        var deformer = new ProxyDeformer(proxy);
        bool skinned = options.Skinned || options.ProxyOnly;


        // Skinned meshes and the armature are scene roots (glTF ignores parent transforms of skinned meshes).
        // The skeleton: one joint per proxy node, under an armature node.
        int skin = -1;
        var joints = new int[proxy.Nodes.Count];
        if (skinned)
        {
            int armature = gltf.AddNode(new JsonObject { ["name"] = "Armature", ["children"] = new JsonArray() }, root: true);
            var armatureChildren = (JsonArray)gltf.Nodes[armature]!["children"]!;
            for (int i = 0; i < proxy.Nodes.Count; i++)
            {
                Matrix4x4.Decompose(deformer.RestFrames[i], out _, out var rotation, out var translation);
                joints[i] = gltf.AddNode(new JsonObject
                {
                    ["name"] = proxy.Nodes[i].Name,
                    ["translation"] = new JsonArray(translation.X, translation.Y, translation.Z),
                    ["rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z, rotation.W),
                });
                armatureChildren.Add(joints[i]);
            }
            var inverseBind = deformer.RestFrames.Select(InverseBind).ToArray();
            skin = gltf.AddSkin(new JsonObject
            {
                ["name"] = "ProxySkin",
                ["joints"] = new JsonArray(joints.Select(j => (JsonNode)j).ToArray()),
                ["inverseBindMatrices"] = gltf.AddMatrices(inverseBind),
                ["skeleton"] = armature,
            });
        }

        if (!options.ProxyOnly)
        {
            var materials = AddMaterials(gltf, model);
            for (int p = 0; p < model.Parts.Count; p++)
            {
                var part = model.Parts[p];
                int mesh = AddPartMesh(gltf, part, materials[part.Material], skinned ? model.Skin[p] : null);
                var node = new JsonObject { ["name"] = part.Name, ["mesh"] = mesh };
                if (skinned) node["skin"] = skin;
                gltf.AddNode(node, root: true);
            }
        }

        if (options.IncludeProxyCage || options.ProxyOnly)
        {
            int mesh = AddProxyCage(gltf, proxy, skinned);
            var node = new JsonObject { ["name"] = "ProxyCage", ["mesh"] = mesh };
            if (skinned) node["skin"] = skin;
            gltf.AddNode(node, root: true);
        }

        if (skinned && options.Animation is { } animation) AddAnimation(gltf, deformer, joints, animation);
        return gltf.ToGlb(Generator);
    }

    // glTF requires the projective part of inverse bind matrices to be exactly (0, 0, 0, 1).
    private static Matrix4x4 InverseBind(Matrix4x4 frame)
    {
        if (!Matrix4x4.Invert(frame, out var inverse)) inverse = Matrix4x4.Identity;
        inverse.M14 = 0;
        inverse.M24 = 0;
        inverse.M34 = 0;
        inverse.M44 = 1;
        return inverse;
    }

    private static Dictionary<GliderMaterial, int> AddMaterials(GltfWriter gltf, GliderModel model)
    {
        var design = model.Design;
        JsonObject Pbr(string name, Vector4 color, double roughness, double metallic = 0, bool doubleSided = true) => new()
        {
            ["name"] = name,
            ["doubleSided"] = doubleSided,
            ["pbrMetallicRoughness"] = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(color.X, color.Y, color.Z, color.W),
                ["roughnessFactor"] = roughness,
                ["metallicFactor"] = metallic,
            },
        };

        var canopy = Pbr("Canopy", Vector4.One, 0.72);
        if (model.BaseColor is { } baseColor)
        {
            int texture = gltf.AddTexture(gltf.AddImage(baseColor.EncodePng(), "CanopyBaseColor"));
            ((JsonObject)canopy["pbrMetallicRoughness"]!)["baseColorTexture"] = new JsonObject { ["index"] = texture };
        }
        if (model.NormalMap is { } normal)
        {
            int texture = gltf.AddTexture(gltf.AddImage(normal.EncodePng(), "CanopyNormal"));
            canopy["normalTexture"] = new JsonObject { ["index"] = texture, ["scale"] = 1.0 };
        }

        return new Dictionary<GliderMaterial, int>
        {
            [GliderMaterial.Canopy] = gltf.AddMaterial(canopy),
            [GliderMaterial.Ribs] = gltf.AddMaterial(Pbr("Ribs", HexToLinear(design.RibColor), 0.8)),
            [GliderMaterial.Lines] = gltf.AddMaterial(Pbr("Lines", Vector4.One, 0.6)),
            [GliderMaterial.Risers] = gltf.AddMaterial(Pbr("Risers", HexToLinear("#263238"), 0.9)),
            [GliderMaterial.Metal] = gltf.AddMaterial(Pbr("Metal", new Vector4(0.8f, 0.8f, 0.82f, 1), 0.3, 1)),
            [GliderMaterial.Toggles] = gltf.AddMaterial(Pbr("Toggles", HexToLinear(design.AccentColor), 0.6)),
        };
    }

    private static int AddPartMesh(GltfWriter gltf, MeshPart part, int material, SkinWeights[]? weights)
    {
        var attributes = new JsonObject
        {
            ["POSITION"] = gltf.AddAccessor(part.Positions.ToArray(), minMax: true),
            ["NORMAL"] = gltf.AddAccessor(part.Normals.ToArray()),
            ["TEXCOORD_0"] = gltf.AddAccessor(part.TexCoords.ToArray()),
        };
        if (part.Colors.Count == part.VertexCount && part.VertexCount > 0) attributes["COLOR_0"] = gltf.AddAccessor(part.Colors.ToArray());
        if (weights != null) AddSkinAttributes(gltf, attributes, weights);
        return gltf.AddMesh(new JsonObject
        {
            ["name"] = part.Name,
            ["primitives"] = new JsonArray(new JsonObject
            {
                ["attributes"] = attributes,
                ["indices"] = gltf.AddIndices(part.Indices.ToArray()),
                ["material"] = material,
                ["mode"] = 4,
            }),
        });
    }

    private static void AddSkinAttributes(GltfWriter gltf, JsonObject attributes, SkinWeights[] weights)
    {
        var joints = new ushort[weights.Length * 4];
        var w = new Vector4[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            var s = weights[i];
            joints[i * 4] = (ushort)s.J0;
            joints[i * 4 + 1] = s.W1 > 0 ? (ushort)s.J1 : (ushort)0;
            joints[i * 4 + 2] = s.W2 > 0 ? (ushort)s.J2 : (ushort)0;
            joints[i * 4 + 3] = s.W3 > 0 ? (ushort)s.J3 : (ushort)0;
            w[i] = new Vector4(s.W0, s.W1, s.W2, s.W3);
        }
        attributes["JOINTS_0"] = gltf.AddJoints(joints);
        attributes["WEIGHTS_0"] = gltf.AddAccessor(w);
    }

    // The proxy as lines: fabric along chord and span, ribs, lines and risers; each vertex follows its own joint.
    private static int AddProxyCage(GltfWriter gltf, ProxyModel proxy, bool skinned)
    {
        var positions = proxy.Nodes.Select(n => n.Position).ToArray();
        var indices = new List<uint>();
        foreach (var c in proxy.Constraints)
        {
            if (c.Kind is ConstraintKind.Chordwise or ConstraintKind.Spanwise or ConstraintKind.Rib or ConstraintKind.Line or ConstraintKind.Riser or ConstraintKind.Harness)
            {
                indices.Add((uint)c.A);
                indices.Add((uint)c.B);
            }
        }
        var attributes = new JsonObject { ["POSITION"] = gltf.AddAccessor(positions, minMax: true) };
        if (skinned)
        {
            AddSkinAttributes(gltf, attributes, proxy.Nodes.Select(n => new SkinWeights(n.Id, 0, 0, 0, 1, 0, 0, 0)).ToArray());
        }
        int material = gltf.AddMaterial(new JsonObject
        {
            ["name"] = "ProxyCage",
            ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(1.0, 0.75, 0.0, 1.0), ["metallicFactor"] = 0.0 },
        });
        return gltf.AddMesh(new JsonObject
        {
            ["name"] = "ProxyCage",
            ["primitives"] = new JsonArray(new JsonObject
            {
                ["attributes"] = attributes,
                ["indices"] = gltf.AddIndices(indices.ToArray()),
                ["material"] = material,
                ["mode"] = 1,
            }),
        });
    }

    private static void AddAnimation(GltfWriter gltf, ProxyDeformer deformer, int[] joints, ProxyAnimation animation)
    {
        if (animation.Frames.Length == 0) return;
        int time = gltf.AddScalars(animation.Times, minMax: true);
        var samplers = new JsonArray();
        var channels = new JsonArray();
        int frames = animation.Frames.Length;
        for (int j = 0; j < joints.Length; j++)
        {
            var translations = new Vector3[frames];
            var rotations = new Quaternion[frames];
            for (int f = 0; f < frames; f++)
            {
                Matrix4x4.Decompose(deformer.Frame(j, animation.Frames[f]), out _, out var rotation, out var translation);
                // Keep consecutive quaternions in the same hemisphere so interpolation takes the short way.
                if (f > 0 && Quaternion.Dot(rotation, rotations[f - 1]) < 0) rotation = -rotation;
                translations[f] = translation;
                rotations[f] = rotation;
            }
            samplers.Add(new JsonObject { ["input"] = time, ["output"] = gltf.AddAccessor(translations, target: null), ["interpolation"] = "LINEAR" });
            channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = joints[j], ["path"] = "translation" } });
            samplers.Add(new JsonObject { ["input"] = time, ["output"] = gltf.AddAccessor(rotations), ["interpolation"] = "LINEAR" });
            channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = joints[j], ["path"] = "rotation" } });
        }
        gltf.AddAnimation(new JsonObject { ["name"] = animation.Name, ["samplers"] = samplers, ["channels"] = channels });
    }

    #endregion

    #region OBJ and line plan

    /// <summary>Writes the static mesh as OBJ with an MTL file and PNG textures.</summary>
    public static List<string> WriteObj(GliderModel model, string folder, string baseName)
    {
        var files = new List<string>();
        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        obj.AppendLine($"# {Generator}: {model.Design.FlatArea:0.#} m², {model.Design.CellCount} cells. Units: meters, +Y up, +Z forward.");
        obj.AppendLine($"mtllib {baseName}.mtl");

        string Texture(TextureImage? image, string suffix)
        {
            if (image is null) return string.Empty;
            string name = $"{baseName}_{suffix}.png";
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, image.EncodePng());
            files.Add(path);
            return name;
        }
        string baseColor = Texture(model.BaseColor, "basecolor");
        string normal = Texture(model.NormalMap, "normal");

        foreach (var material in model.Parts.Select(p => p.Material).Distinct())
        {
            var color = material switch
            {
                GliderMaterial.Ribs => HexToSrgb(model.Design.RibColor),
                GliderMaterial.Risers => HexToSrgb("#263238"),
                GliderMaterial.Metal => new Vector3(0.85f, 0.85f, 0.87f),
                GliderMaterial.Toggles => HexToSrgb(model.Design.AccentColor),
                _ => Vector3.One,
            };
            mtl.AppendLine($"newmtl {material}");
            mtl.AppendLine(string.Create(ci, $"Kd {color.X:0.###} {color.Y:0.###} {color.Z:0.###}"));
            mtl.AppendLine("Ks 0.05 0.05 0.05");
            if (material == GliderMaterial.Canopy && baseColor.Length > 0) mtl.AppendLine($"map_Kd {baseColor}");
            if (material == GliderMaterial.Canopy && normal.Length > 0) mtl.AppendLine($"norm {normal}");
            mtl.AppendLine();
        }

        int offset = 1;
        foreach (var part in model.Parts)
        {
            obj.AppendLine($"o {part.Name}");
            obj.AppendLine($"usemtl {part.Material}");
            bool colors = part.Colors.Count == part.VertexCount;
            for (int i = 0; i < part.VertexCount; i++)
            {
                var p = part.Positions[i];
                if (colors)
                {
                    var c = part.Colors[i];
                    obj.AppendLine(string.Create(ci, $"v {p.X:0.#####} {p.Y:0.#####} {p.Z:0.#####} {LinearToSrgb(c.X):0.###} {LinearToSrgb(c.Y):0.###} {LinearToSrgb(c.Z):0.###}"));
                }
                else
                {
                    obj.AppendLine(string.Create(ci, $"v {p.X:0.#####} {p.Y:0.#####} {p.Z:0.#####}"));
                }
            }
            foreach (var t in part.TexCoords) obj.AppendLine(string.Create(ci, $"vt {t.X:0.#####} {1 - t.Y:0.#####}"));
            foreach (var n in part.Normals) obj.AppendLine(string.Create(ci, $"vn {n.X:0.####} {n.Y:0.####} {n.Z:0.####}"));
            for (int i = 0; i < part.Indices.Count; i += 3)
            {
                long a = part.Indices[i] + offset, b = part.Indices[i + 1] + offset, c = part.Indices[i + 2] + offset;
                obj.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            offset += part.VertexCount;
        }

        string objPath = Path.Combine(folder, $"{baseName}.obj");
        string mtlPath = Path.Combine(folder, $"{baseName}.mtl");
        File.WriteAllText(objPath, obj.ToString());
        File.WriteAllText(mtlPath, mtl.ToString());
        files.Add(objPath);
        files.Add(mtlPath);
        return files;
    }

    /// <summary>Writes the line plan: every line with its level, row, side, diameter and length.</summary>
    public static string ToLinePlanCsv(RiggingLayout rigging)
    {
        var ci = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("Name,Level,Row,Side,DiameterMm,LengthM\n");
        foreach (var line in rigging.Lines.OrderBy(l => l.Row).ThenBy(l => l.Side).ThenBy(l => l.Level))
        {
            csv.AppendLine(string.Create(ci, $"{line.Name},{line.Level},{RiggingLayout.RowName(line.Row)},{(line.Side > 0 ? "L" : "R")},{line.Diameter:0.##},{line.Length:0.0000}"));
        }
        foreach (var level in Enum.GetValues<LineLevel>())
        {
            double total = rigging.TotalLength(level);
            if (total > 0) csv.AppendLine(string.Create(ci, $"Total {level},{level},,,,{total:0.000}"));
        }
        return csv.ToString();
    }

    #endregion

    private static Vector3 HexToSrgb(string hex)
    {
        var c = CanopyTextureGenerator.ParseColor(hex, SKColors.Gray);
        return new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
    }

    private static Vector4 HexToLinear(string hex)
    {
        var c = CanopyTextureGenerator.ParseColor(hex, SKColors.Gray);
        return RiggingBuilder.SrgbToLinear(c.Red, c.Green, c.Blue);
    }

    private static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
}
