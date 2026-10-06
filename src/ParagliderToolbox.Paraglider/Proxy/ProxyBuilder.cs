using System.Numerics;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Rigging;

namespace ParagliderToolbox.Paraglider.Proxy;

/// <summary>The settings a proxy is built with (resolved from <see cref="GliderDesign.ProxyComplexity"/>).</summary>
/// <param name="SectionStride">Every n-th tab rib (counted from the tips) becomes a section.</param>
/// <param name="IntermediateSections">Sections between two chosen tab ribs.</param>
/// <param name="ExtraStations">Chord stations added between the line rows (and the nose and tail).</param>
/// <param name="DoubleSurface">Upper and lower surface, or a single camber surface.</param>
/// <param name="Cascades">Whether cascade knots are nodes.</param>
public readonly record struct ProxySettings(int SectionStride, int IntermediateSections, int ExtraStations, bool DoubleSurface, bool Cascades)
{
    /// <summary>Gets the settings of a design.</summary>
    public static ProxySettings FromDesign(GliderDesign design) => design.ProxyComplexity switch
    {
        ProxyComplexity.Arcade => new(3, 0, 0, false, false),
        ProxyComplexity.Low => new(2, 0, 0, true, false),
        ProxyComplexity.Medium => new(1, 0, 1, true, true),
        ProxyComplexity.High => new(1, 1, 2, true, true),
        _ => new(Math.Max(1, design.ProxySectionStride), Math.Max(0, design.ProxyIntermediateSections),
            Math.Clamp(design.ProxyExtraChordStations, 0, 2), design.ProxyDoubleSurface, design.ProxyCascades),
    };
}

/// <summary>The proxy and how the rigging maps onto it, for skinning.</summary>
public sealed class ProxyBuild
{
    /// <summary>Gets the proxy.</summary>
    public required ProxyModel Model { get; init; }

    /// <summary>Gets the settings it was built with.</summary>
    public required ProxySettings Settings { get; init; }

    /// <summary>Gets the proxy node of each rigging point that has one (tabs on sections, knots with cascades, risers, ...).</summary>
    public required Dictionary<int, int> RigPointNodes { get; init; }
}

/// <summary>
/// Builds the low resolution physics proxy of a glider: profile sections at chosen ribs with nodes on the upper and lower
/// surface (or the camber surface), fabric and rib constraints, the line plan as tension-only constraints, risers, the
/// pilot, aerodynamic strips with pressure cells, and the pilot's controls.
/// </summary>
/// <remarks>
/// Sections sit on line tab ribs, and the chord stations include the line rows, so the lines attach to nodes exactly.
/// The tips are always sections. Compliances are for an XPBD solver (m/N): fabric resists stretching strongly but folds
/// easily, ribs only hold while pressurized, lines are tension-only with the elasticity of aramid/Dyneema lines.
/// </remarks>
public static class ProxyBuilder
{
    private const float FabricCompliance = 2e-7f;
    private const float FabricCompression = 4e-4f;
    private const float ShearCompliance = 2e-5f;
    private const float RibCompliance = 2e-7f;
    private const float RibCompression = 1e-3f;
    private const float BendCompliance = 4e-3f;
    private const float LineStiffness = 30000f; // N, EA of a typical line

    /// <summary>Builds the proxy of a glider.</summary>
    public static ProxyBuild Build(GliderShape shape, RiggingLayout rigging)
    {
        var design = shape.Design;
        var settings = ProxySettings.FromDesign(design);
        var model = new ProxyModel
        {
            Complexity = design.ProxyComplexity.ToString(),
            FlatArea = (float)design.FlatArea,
            ProjectedAspectRatio = (float)shape.ProjectedAspectRatio,
            // Arched wings induce less drag than a flat wing of their projected span (nonplanar lifting line).
            InducedAspectRatio = (float)Math.Sqrt(design.FlatAspectRatio * shape.ProjectedAspectRatio),
            SpanEfficiency = (float)design.SpanEfficiency,
            PilotDragArea = (float)design.PilotDragArea,
            LineDragCoefficient = (float)design.LineDragCoefficient,
            Polar = new SectionPolar(design).Sample(),
        };

        // Trim airspeed from the lift needed at the trim angle of attack (lifting-line lift slope of the projected wing).
        var polar = new SectionPolar(design);
        double aspect = shape.ProjectedAspectRatio;
        double slope3D = polar.LiftSlope / (1 + polar.LiftSlope / (Math.PI * aspect * 0.9));
        double trimLift = Math.Max(0.2, slope3D * (design.TrimAngleOfAttack - polar.ZeroLiftAlpha) * Math.PI / 180);
        double weight = (design.PilotMass + design.CanopyMass) * 9.81;
        model.TrimAirspeed = (float)Math.Sqrt(2 * weight / (1.225 * shape.ProjectedArea * trimLift));
        model.TrimFlightPathAngle = (float)(Math.Atan(1 / Math.Max(1, design.TrimGlideRatio)) * 180 / Math.PI);

        var sectionRibs = ChooseSectionRibs(shape, rigging, settings);
        var stations = ChooseStations(design.RowPositions, settings.ExtraStations);
        BuildSections(model, shape, sectionRibs, stations, settings.DoubleSurface);
        BuildFabric(model, settings.DoubleSurface);
        var rigNodes = BuildRigging(model, shape, rigging, sectionRibs, settings);
        if (settings.DoubleSurface) BuildDiagonalRibs(model);
        BuildStrips(model, shape, sectionRibs, settings.DoubleSurface);
        DistributeMass(model, shape, design);
        BuildControls(model, shape, rigging, rigNodes);
        return new ProxyBuild { Model = model, Settings = settings, RigPointNodes = rigNodes };
    }

    private static List<int> ChooseSectionRibs(GliderShape shape, RiggingLayout rigging, ProxySettings settings)
    {
        var ribs = shape.RibPositions;
        var chosen = new SortedSet<int> { 0, shape.CellCount };
        foreach (int side in new[] { 1, -1 })
        {
            var sideRibs = rigging.TabRibs.Where(r => Math.Sign(ribs[r]) == side).OrderByDescending(r => Math.Abs(ribs[r])).ToList();
            for (int k = 0; k < sideRibs.Count; k++)
            {
                if (k % settings.SectionStride == 0 || k == sideRibs.Count - 1) chosen.Add(sideRibs[k]);
            }
        }
        if (settings.IntermediateSections > 0)
        {
            var list = chosen.ToList();
            for (int i = 0; i < list.Count - 1; i++)
            {
                int a = list[i], b = list[i + 1];
                for (int m = 1; m <= settings.IntermediateSections; m++)
                {
                    int r = a + (int)Math.Round((b - a) * m / (double)(settings.IntermediateSections + 1));
                    if (r > a && r < b) chosen.Add(r);
                }
            }
        }
        return chosen.ToList();
    }

    private static List<float> ChooseStations(double[] rows, int extra)
    {
        var points = new List<double> { 0 };
        points.AddRange(rows.OrderBy(r => r));
        points.Add(1);
        var stations = new List<float>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            if (i > 0) stations.Add((float)points[i]);
            for (int e = 1; e <= extra; e++) stations.Add((float)(points[i] + (points[i + 1] - points[i]) * e / (extra + 1)));
        }
        return stations;
    }

    private static void BuildSections(ProxyModel model, GliderShape shape, List<int> ribs, List<float> stations, bool doubleSurface)
    {
        for (int s = 0; s < ribs.Count; s++)
        {
            double eta = shape.RibPositions[ribs[s]];
            int side = Math.Abs(eta) < 1e-9 ? 0 : Math.Sign(eta);
            var section = new ProxySection
            {
                Eta = (float)eta,
                Chord = (float)shape.Chord(eta),
                Stations = [.. stations],
            };
            section.LeadingEdge = AddNode(model, $"S{s:00}_LE", ProxyNodeKind.Camber, shape.SurfacePoint(eta, 0), s, 0, side);
            for (int k = 0; k < stations.Count; k++)
            {
                double x = stations[k];
                if (doubleSurface)
                {
                    section.Upper.Add(AddNode(model, $"S{s:00}_U{k}", ProxyNodeKind.Upper, shape.SurfacePoint(eta, GliderShape.ProfileParameter(x, true)), s, x, side));
                    section.Lower.Add(AddNode(model, $"S{s:00}_L{k}", ProxyNodeKind.Lower, shape.SurfacePoint(eta, GliderShape.ProfileParameter(x, false)), s, x, side));
                }
                else
                {
                    section.Lower.Add(AddNode(model, $"S{s:00}_C{k}", ProxyNodeKind.Camber, shape.CamberPoint(eta, x), s, x, side));
                }
            }
            section.TrailingEdge = AddNode(model, $"S{s:00}_TE", ProxyNodeKind.Camber, shape.SurfacePoint(eta, 1), s, 1, side);
            model.Sections.Add(section);
        }
    }

    private static int AddNode(ProxyModel model, string name, ProxyNodeKind kind, Vector3 position, int section, double chord, int side)
    {
        var node = new ProxyNode { Id = model.Nodes.Count, Name = name, Kind = kind, Position = position, Section = section, Chord = (float)chord, Side = side };
        model.Nodes.Add(node);
        return node.Id;
    }

    // The chain of a surface from nose to tail.
    /// <summary>Gets the nodes of a section's upper or lower (camber) surface from the nose to the tail, nose and tail included.</summary>
    public static List<int> Chain(ProxySection section, bool upper)
    {
        var chain = new List<int> { section.LeadingEdge };
        chain.AddRange(upper ? section.Upper : section.Lower);
        chain.Add(section.TrailingEdge);
        return chain;
    }

    private static void BuildFabric(ProxyModel model, bool doubleSurface)
    {
        var surfaces = doubleSurface ? new[] { true, false } : new[] { false };
        for (int s = 0; s < model.Sections.Count; s++)
        {
            var section = model.Sections[s];
            foreach (bool upper in surfaces)
            {
                var chain = Chain(section, upper);
                for (int k = 0; k < chain.Count - 1; k++) AddDistance(model, chain[k], chain[k + 1], ConstraintKind.Chordwise, FabricCompliance, FabricCompression);
                for (int k = 0; k < chain.Count - 2; k++) AddDistance(model, chain[k], chain[k + 2], ConstraintKind.Bend, BendCompliance, BendCompliance);
            }
            if (doubleSurface)
            {
                for (int k = 0; k < section.Upper.Count; k++)
                {
                    AddDistance(model, section.Upper[k], section.Lower[k], ConstraintKind.Rib, RibCompliance, RibCompression);
                    if (k + 1 < section.Upper.Count)
                    {
                        AddDistance(model, section.Upper[k], section.Lower[k + 1], ConstraintKind.Rib, RibCompliance, RibCompression);
                        AddDistance(model, section.Upper[k + 1], section.Lower[k], ConstraintKind.Rib, RibCompliance, RibCompression);
                    }
                }
            }

            if (s + 1 >= model.Sections.Count) continue;
            var next = model.Sections[s + 1];
            foreach (bool upper in surfaces)
            {
                var a = Chain(section, upper);
                var b = Chain(next, upper);
                for (int k = 0; k < a.Count; k++)
                {
                    if (!upper || (k > 0 && k < a.Count - 1)) // nose and tail are shared by both surfaces
                    {
                        AddDistance(model, a[k], b[k], ConstraintKind.Spanwise, FabricCompliance, FabricCompression);
                    }
                    if (k + 1 < a.Count)
                    {
                        AddDistance(model, a[k], b[k + 1], ConstraintKind.Shear, ShearCompliance, ShearCompliance);
                        AddDistance(model, a[k + 1], b[k], ConstraintKind.Shear, ShearCompliance, ShearCompliance);
                    }
                }
            }
            if (s + 2 < model.Sections.Count)
            {
                var after = model.Sections[s + 2];
                foreach (bool upper in surfaces)
                {
                    var a = Chain(section, upper);
                    var c = Chain(after, upper);
                    for (int k = 0; k < a.Count; k++) AddDistance(model, a[k], c[k], ConstraintKind.Bend, BendCompliance, BendCompliance);
                }
            }
        }
    }

    // Diagonal (V) ribs: a section without lines hangs from the line attachments of its neighbors, from their lower
    // surface up to its upper surface, as the diagonal ribs between tab ribs do in a real wing.
    private static void BuildDiagonalRibs(ProxyModel model)
    {
        var withLines = new HashSet<int>();
        foreach (var c in model.Constraints.Where(c => c.Kind == ConstraintKind.Line))
        {
            foreach (int n in new[] { c.A, c.B })
            {
                if (model.Nodes[n].Kind == ProxyNodeKind.Lower) withLines.Add(n);
            }
        }
        for (int s = 0; s < model.Sections.Count; s++)
        {
            var section = model.Sections[s];
            if (section.Lower.Any(withLines.Contains)) continue;
            foreach (int t in new[] { s - 1, s + 1 })
            {
                if (t < 0 || t >= model.Sections.Count) continue;
                var neighbor = model.Sections[t];
                for (int k = 0; k < neighbor.Lower.Count && k < section.Upper.Count; k++)
                {
                    if (withLines.Contains(neighbor.Lower[k])) AddDistance(model, neighbor.Lower[k], section.Upper[k], ConstraintKind.Rib, RibCompliance, RibCompression);
                }
            }
        }
    }

    private static Dictionary<int, int> BuildRigging(ProxyModel model, GliderShape shape, RiggingLayout rigging, List<int> sectionRibs, ProxySettings settings)
    {
        var map = new Dictionary<int, int>();
        var sectionOfRib = sectionRibs.Select((rib, s) => (rib, s)).ToDictionary(p => p.rib, p => p.s);

        // Tabs on sections.
        foreach (var tab in rigging.Points.Where(p => p.Kind == RigPointKind.Tab))
        {
            if (!sectionOfRib.TryGetValue(tab.Rib, out int s)) continue;
            var section = model.Sections[s];
            int k = section.Stations.FindIndex(x => Math.Abs(x - tab.Chord) < 1e-4);
            if (k >= 0) map[tab.Id] = section.Lower[k];
        }
        // Brake tabs onto the nearest section's trailing edge on the same side.
        foreach (var tab in rigging.Points.Where(p => p.Kind == RigPointKind.BrakeTab))
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int s = 0; s < model.Sections.Count; s++)
            {
                double eta = model.Sections[s].Eta;
                if (Math.Sign(eta) != tab.Side) continue;
                double d = Math.Abs(eta - tab.Eta);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = s;
                }
            }
            if (best >= 0) map[tab.Id] = model.Sections[best].TrailingEdge;
        }

        // Risers, carabiners, pulley, toggle, knots.
        foreach (var point in rigging.Points)
        {
            var kind = point.Kind switch
            {
                RigPointKind.RiserTop => ProxyNodeKind.RiserTop,
                RigPointKind.Carabiner => ProxyNodeKind.Carabiner,
                RigPointKind.Pulley => ProxyNodeKind.Pulley,
                RigPointKind.Toggle => ProxyNodeKind.Toggle,
                RigPointKind.Knot when settings.Cascades => ProxyNodeKind.Knot,
                _ => (ProxyNodeKind?)null,
            };
            if (kind is not { } k) continue;
            string name = $"{k}_{(point.Side > 0 ? "L" : "R")}_{RiggingLayout.RowName(point.Row == -1 ? 0 : point.Row)}{point.Id}";
            if (k is ProxyNodeKind.Carabiner or ProxyNodeKind.Pulley or ProxyNodeKind.Toggle) name = $"{k}_{(point.Side > 0 ? "L" : "R")}";
            if (k == ProxyNodeKind.RiserTop) name = $"Riser_{(point.Side > 0 ? "L" : "R")}_{RiggingLayout.RowName(point.Row)}";
            map[point.Id] = AddNode(model, name, k, point.Position, -1, 0, point.Side);
        }

        // Lines between mapped points (merging duplicates), or straight from each tab to its riser without cascades.
        var added = new HashSet<(int, int)>();
        void AddLine(int a, int b, string name, ConstraintKind kind = ConstraintKind.Line)
        {
            if (a == b || !added.Add((Math.Min(a, b), Math.Max(a, b)))) return;
            float length = Vector3.Distance(model.Nodes[a].Position, model.Nodes[b].Position);
            float compliance = kind == ConstraintKind.Line ? length / LineStiffness : 2e-6f;
            model.Constraints.Add(new ProxyConstraint
            {
                A = a, B = b, Kind = kind, RestLength = length, Compliance = compliance, CompressionCompliance = 0,
                TensionOnly = true, Name = name,
            });
        }

        if (settings.Cascades)
        {
            foreach (var line in rigging.Lines.Where(l => l.Level is not (LineLevel.Riser or LineLevel.BrakeHandle)))
            {
                if (map.TryGetValue(line.Upper, out int a) && map.TryGetValue(line.Lower, out int b)) AddLine(a, b, line.Name);
            }
            var remap = PruneDanglingKnots(model);
            foreach (int key in map.Keys.ToList())
            {
                if (remap[map[key]] < 0) map.Remove(key);
                else map[key] = remap[map[key]];
            }
        }
        else
        {
            foreach (var tab in rigging.Points.Where(p => p.Kind is RigPointKind.Tab or RigPointKind.BrakeTab))
            {
                if (!map.TryGetValue(tab.Id, out int node)) continue;
                var anchor = tab.Kind == RigPointKind.BrakeTab
                    ? rigging.Points.First(p => p.Kind == RigPointKind.Pulley && p.Side == tab.Side)
                    : rigging.RiserTop(tab.Row, tab.Side);
                string name = tab.Kind == RigPointKind.BrakeTab ? $"Brake {(tab.Side > 0 ? "L" : "R")}" : $"{RiggingLayout.RowName(tab.Row)} {(tab.Side > 0 ? "L" : "R")}";
                AddLine(node, map[anchor.Id], name);
            }
        }

        // Risers, the brake handle and the pilot.
        foreach (var line in rigging.Lines.Where(l => l.Level is LineLevel.Riser))
        {
            AddLine(map[line.Upper], map[line.Lower], line.Name, ConstraintKind.Riser);
        }
        foreach (int side in new[] { 1, -1 })
        {
            int carabiner = map[rigging.Carabiner(side).Id];
            var tops = rigging.Points.Where(p => p.Kind == RigPointKind.RiserTop && p.Side == side).OrderBy(p => p.Row).Select(p => map[p.Id]).ToList();
            // The maillons of a side stay together (the riser webbing and its stitching).
            for (int i = 0; i < tops.Count - 1; i++) AddDistance(model, tops[i], tops[i + 1], ConstraintKind.Harness, 1e-5f, 1e-5f);
            int pulley = map[rigging.Points.First(p => p.Kind == RigPointKind.Pulley && p.Side == side).Id];
            int toggle = map[rigging.Toggle(side).Id];
            AddDistance(model, pulley, tops[^1], ConstraintKind.Harness, 0, 0);
            AddDistance(model, pulley, carabiner, ConstraintKind.Harness, 0, 0);
            AddDistance(model, toggle, pulley, ConstraintKind.Harness, 0, 0);
            AddDistance(model, toggle, carabiner, ConstraintKind.Harness, 0, 0);
        }

        int left = map[rigging.Carabiner(1).Id], right = map[rigging.Carabiner(-1).Id];
        var pilotPosition = (model.Nodes[left].Position + model.Nodes[right].Position) / 2 - new Vector3(0, 0.38f, 0.05f);
        model.Pilot = AddNode(model, "Pilot", ProxyNodeKind.Pilot, pilotPosition, -1, 0, 0);
        AddDistance(model, model.Pilot, left, ConstraintKind.Harness, 0, 0);
        AddDistance(model, model.Pilot, right, ConstraintKind.Harness, 0, 0);
        AddDistance(model, left, right, ConstraintKind.Harness, 0, 0);

        // Line drag: the proxy's lines together get the drag area of all the real lines (a simpler proxy has fewer,
        // so each stands for more line).
        double realArea = rigging.Lines.Where(l => l.Level != LineLevel.Riser).Sum(l => l.Diameter / 1000 * l.Length);
        double proxyLength = model.Constraints.Where(c => c.Kind == ConstraintKind.Line).Sum(c => c.RestLength);
        foreach (var c in model.Constraints)
        {
            if (c.Kind == ConstraintKind.Line) c.DragDiameter = (float)(realArea / Math.Max(proxyLength, 1e-6));
            else if (c.Kind == ConstraintKind.Riser) c.DragDiameter = 0.025f;
        }
        return map;
    }

    // Knots whose upper lines all went (their tabs aren't on sections) are removed, with the lines below them.
    private static int[] PruneDanglingKnots(ProxyModel model)
    {
        bool removed;
        do
        {
            removed = false;
            foreach (var knot in model.Nodes.Where(n => n.Kind == ProxyNodeKind.Knot && n.Mass >= 0).ToList())
            {
                // A knot needs a line above it (to a node higher up, i.e. the line's other end is not below it).
                bool hasUpper = model.Constraints.Any(c => c.Kind == ConstraintKind.Line && (c.A == knot.Id || c.B == knot.Id)
                    && model.Nodes[c.A == knot.Id ? c.B : c.A].Position.Y > knot.Position.Y);
                if (hasUpper) continue;
                model.Constraints.RemoveAll(c => c.A == knot.Id || c.B == knot.Id);
                knot.Mass = -1; // marked for removal
                removed = true;
            }
        } while (removed);
        return RemoveMarkedNodes(model);
    }

    // Drops the nodes marked with a negative mass; returns the new index of every old node (−1 for removed ones).
    private static int[] RemoveMarkedNodes(ProxyModel model)
    {
        var remap = new int[model.Nodes.Count];
        var kept = new List<ProxyNode>();
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            if (model.Nodes[i].Mass < 0)
            {
                remap[i] = -1;
                continue;
            }
            remap[i] = kept.Count;
            model.Nodes[i].Id = kept.Count;
            kept.Add(model.Nodes[i]);
        }
        model.Nodes = kept;
        foreach (var c in model.Constraints)
        {
            c.A = remap[c.A];
            c.B = remap[c.B];
        }
        foreach (var s in model.Sections)
        {
            s.LeadingEdge = remap[s.LeadingEdge];
            s.TrailingEdge = remap[s.TrailingEdge];
            s.Upper = s.Upper.Select(i => remap[i]).ToList();
            s.Lower = s.Lower.Select(i => remap[i]).ToList();
        }
        return remap;
    }

    private static void BuildStrips(ProxyModel model, GliderShape shape, List<int> sectionRibs, bool doubleSurface)
    {
        int cells = shape.CellCount;
        for (int s = 0; s < model.Sections.Count - 1; s++)
        {
            var a = model.Sections[s];
            var b = model.Sections[s + 1];
            int ribA = sectionRibs[s], ribB = sectionRibs[s + 1];
            var strip = new ProxyStrip
            {
                SectionA = s,
                SectionB = s + 1,
                Area = (float)((b.Eta - a.Eta) * shape.HalfSpan * (a.Chord + b.Chord) / 2),
                HasInlet = ribA >= CanopyBuilder.ClosedTipCells && ribB <= cells - CanopyBuilder.ClosedTipCells,
            };
            if (doubleSurface)
            {
                // Each triangle is oriented against a reference: the profile's up axis for the surfaces, the span
                // direction for the end caps. (Facing away from the cell's center fails for thin, curved cells.)
                var up = Vector3.Zero;
                foreach (var section in new[] { a, b })
                {
                    for (int k = 0; k < section.Upper.Count; k++) up += model.Nodes[section.Upper[k]].Position - model.Nodes[section.Lower[k]].Position;
                }
                foreach (bool upper in new[] { true, false })
                {
                    var ca = Chain(a, upper);
                    var cb = Chain(b, upper);
                    for (int k = 0; k < ca.Count - 1; k++)
                    {
                        AddTriangle(model, strip.Surface, ca[k], ca[k + 1], cb[k + 1], upper ? up : -up);
                        AddTriangle(model, strip.Surface, ca[k], cb[k + 1], cb[k], upper ? up : -up);
                    }
                }
                var centerA = Center(model, a);
                var centerB = Center(model, b);
                if (s == 0) AddCap(model, strip.Surface, a, centerA - centerB);
                if (s == model.Sections.Count - 2) AddCap(model, strip.Surface, b, centerB - centerA);
            }
            model.Strips.Add(strip);
        }
    }

    private static Vector3 Center(ProxyModel model, ProxySection section)
    {
        var chain = Chain(section, true).Concat(Chain(section, false)).ToList();
        var center = Vector3.Zero;
        foreach (int i in chain) center += model.Nodes[i].Position;
        return center / chain.Count;
    }

    private static void AddTriangle(ProxyModel model, List<int> surface, int a, int b, int c, Vector3 outward)
    {
        var n = Vector3.Cross(model.Nodes[b].Position - model.Nodes[a].Position, model.Nodes[c].Position - model.Nodes[a].Position);
        if (Vector3.Dot(n, outward) < 0) surface.AddRange([a, c, b]);
        else surface.AddRange([a, b, c]);
    }

    private static void AddCap(ProxyModel model, List<int> surface, ProxySection section, Vector3 outward)
    {
        // A zigzag between the upper and lower chain, so every node carries a similar share of the cap's pressure (a fan
        // from the nose would load the light nose node with a third of every triangle).
        var upper = Chain(section, true);
        var lower = Chain(section, false);
        for (int k = 0; k < upper.Count - 1; k++)
        {
            int a = upper[k], b = upper[k + 1], c = lower[k + 1], d = lower[k];
            if (a == d) AddTriangle(model, surface, a, b, c, outward);      // at the shared nose
            else if (b == c) AddTriangle(model, surface, a, b, d, outward); // at the shared tail
            else
            {
                AddTriangle(model, surface, a, b, d, outward);
                AddTriangle(model, surface, d, b, c, outward);
            }
        }
    }

    private static void DistributeMass(ProxyModel model, GliderShape shape, GliderDesign design)
    {
        float canopyMass = (float)(design.CanopyMass * 0.8);
        float lineMass = (float)(design.CanopyMass * 0.2);
        var weights = new double[model.Nodes.Count];
        for (int s = 0; s < model.Sections.Count; s++)
        {
            var section = model.Sections[s];
            double etaPrev = s > 0 ? model.Sections[s - 1].Eta : section.Eta;
            double etaNext = s + 1 < model.Sections.Count ? model.Sections[s + 1].Eta : section.Eta;
            double width = (etaNext - etaPrev) / 2 * shape.HalfSpan;
            foreach (bool upper in section.Upper.Count > 0 ? new[] { true, false } : new[] { false })
            {
                var chain = Chain(section, upper);
                var x = new List<double> { 0 };
                x.AddRange(section.Stations.Select(v => (double)v));
                x.Add(1);
                for (int k = 0; k < chain.Count; k++)
                {
                    double dx = ((k + 1 < x.Count ? x[k + 1] : 1) - (k > 0 ? x[k - 1] : 0)) / 2;
                    weights[chain[k]] += width * dx * section.Chord;
                }
            }
        }
        double total = weights.Sum();
        var lineNodes = model.Nodes.Where(n => n.Kind is ProxyNodeKind.Knot or ProxyNodeKind.RiserTop).ToList();
        foreach (var node in model.Nodes)
        {
            node.Mass = node.Kind switch
            {
                ProxyNodeKind.Pilot => (float)design.PilotMass,
                ProxyNodeKind.Carabiner => 0.4f,
                ProxyNodeKind.Pulley or ProxyNodeKind.Toggle => 0.05f,
                ProxyNodeKind.Knot or ProxyNodeKind.RiserTop => lineMass / Math.Max(1, lineNodes.Count),
                _ => (float)(canopyMass * weights[node.Id] / total),
            };
            node.Mass = Math.Max(node.Mass, 0.002f);
        }
    }

    private static void BuildControls(ProxyModel model, GliderShape shape, RiggingLayout rigging, Dictionary<int, int> map)
    {
        float halfProjected = (float)shape.ProjectedSpan / 2;
        int rows = shape.Design.RowPositions.Length;
        foreach (int side in new[] { 1, -1 })
        {
            string s = side > 0 ? "Left" : "Right";
            int pulley = map[rigging.Points.First(p => p.Kind == RigPointKind.Pulley && p.Side == side).Id];
            var brake = new ProxyControl { Name = "Brake" + s };
            foreach (var (c, i) in model.Constraints.Select((c, i) => (c, i)))
            {
                if (c.Kind == ConstraintKind.Line && (c.A == pulley || c.B == pulley))
                {
                    brake.Constraints.Add(i);
                    // Brake lines have some slack at rest: the first centimeters of travel do nothing.
                    model.Constraints[i].RestLength += (float)shape.Design.BrakeSlack;
                    brake.Travel.Add((float)(shape.Design.BrakeTravel + shape.Design.BrakeSlack));
                }
            }
            model.Controls.Add(brake);

            // Pulling the outer A lines down folds the outer canopy: an asymmetric collapse.
            var collapse = new ProxyControl { Name = "Collapse" + s };
            var ears = new ProxyControl { Name = "BigEars" + s };
            int riserA = map[rigging.RiserTop(0, side).Id];
            foreach (var (c, i) in model.Constraints.Select((c, i) => (c, i)))
            {
                if (c.Kind != ConstraintKind.Line || c.Name is null || !c.Name.StartsWith('A')) continue;
                if (!IsOnSide(model, c, side)) continue;
                float outer = Math.Max(Math.Abs(model.Nodes[c.A].Position.X), Math.Abs(model.Nodes[c.B].Position.X)) / halfProjected;
                // The lines hanging on the A riser: main lines with cascades, otherwise the tab lines themselves.
                if (c.A != riserA && c.B != riserA) continue;
                if (outer > 0.35f)
                {
                    collapse.Constraints.Add(i);
                    collapse.Travel.Add(1.2f);
                }
                if (outer > 0.6f)
                {
                    ears.Constraints.Add(i);
                    ears.Travel.Add(0.7f);
                }
            }
            model.Controls.Add(collapse);
            model.Controls.Add(ears);
        }

        var speedBar = new ProxyControl { Name = "SpeedBar" };
        var frontal = new ProxyControl { Name = "Frontal" };
        foreach (var (c, i) in model.Constraints.Select((c, i) => (c, i)))
        {
            if (c.Kind == ConstraintKind.Riser && c.Name is { } name && name.Length > 6)
            {
                int row = name[^1] - 'A';
                if (row >= 0 && row < rows - 1)
                {
                    speedBar.Constraints.Add(i);
                    speedBar.Travel.Add(0.15f * (rows - 1 - row) / (rows - 1));
                }
            }
            bool onRiser = model.Nodes[c.A].Kind == ProxyNodeKind.RiserTop || model.Nodes[c.B].Kind == ProxyNodeKind.RiserTop;
            if (c.Kind == ConstraintKind.Line && c.Name is { } line && line.StartsWith('A') && onRiser)
            {
                frontal.Constraints.Add(i);
                frontal.Travel.Add(0.55f);
            }
        }
        model.Controls.Add(speedBar);
        model.Controls.Add(frontal);
    }

    private static bool IsOnSide(ProxyModel model, ProxyConstraint c, int side) =>
        model.Nodes[c.A].Side == side || model.Nodes[c.B].Side == side;

    private static void AddDistance(ProxyModel model, int a, int b, ConstraintKind kind, float compliance, float compression)
    {
        model.Constraints.Add(new ProxyConstraint
        {
            A = a,
            B = b,
            Kind = kind,
            RestLength = Vector3.Distance(model.Nodes[a].Position, model.Nodes[b].Position),
            Compliance = compliance,
            CompressionCompliance = compression,
        });
    }
}
