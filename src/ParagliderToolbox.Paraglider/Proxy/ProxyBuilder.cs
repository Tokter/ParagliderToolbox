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
/// <param name="LineSegmentLength">
/// The longest line segment (m): longer suspension lines get points along them, so slack lines sag and bow; 0 keeps
/// them straight. Brake lines always get at least two segments.
/// </param>
public readonly record struct ProxySettings(int SectionStride, int IntermediateSections, int ExtraStations, bool DoubleSurface, bool Cascades,
    float LineSegmentLength = 0)
{
    /// <summary>Gets the settings of a design.</summary>
    public static ProxySettings FromDesign(GliderDesign design) => design.ProxyComplexity switch
    {
        ProxyComplexity.Arcade => new(3, 0, 0, false, false, 0),
        ProxyComplexity.Low => new(2, 0, 0, true, false, 0),
        ProxyComplexity.Medium => new(1, 0, 1, true, true, 2.5f),
        ProxyComplexity.High => new(1, 1, 2, true, true, 1.5f),
        _ => new(Math.Max(1, design.ProxySectionStride), Math.Max(0, design.ProxyIntermediateSections),
            Math.Clamp(design.ProxyExtraChordStations, 0, 2), design.ProxyDoubleSurface, design.ProxyCascades,
            design.ProxyCascades ? 2.5f : 0),
    };
}

/// <summary>
/// The material of the proxy: XPBD compliances (m/N, 0 is rigid) of each constraint kind when stretched and when
/// compressed. Fabric and ribs are stiff in tension. Against compression and folding an inflated cell is stiff (its
/// pressure holds the shape) but an empty one is limp fabric: the solver blends between the inflated and the
/// <c>Deflated</c> compliance by the cells' pressure, so a wing holds its shape in flight and folds when it collapses.
/// </summary>
public sealed record ProxyMaterial
{
    /// <summary>Gets the compliance of the canopy fabric along the chord and the span when stretched.</summary>
    public float Fabric { get; init; } = 2e-7f;

    /// <summary>Gets the compliance of the fabric of an inflated cell when compressed.</summary>
    public float FabricCompression { get; init; } = 4e-4f;

    /// <summary>Gets the compliance of the surface diagonals (bias stretch of the fabric) when stretched.</summary>
    public float Shear { get; init; } = 2e-5f;

    /// <summary>Gets the compliance of the surface diagonals of an inflated cell when compressed.</summary>
    public float ShearCompression { get; init; } = 2e-5f;

    /// <summary>Gets the compliance of the ribs (profile height and diagonals) when stretched.</summary>
    public float Rib { get; init; } = 2e-7f;

    /// <summary>Gets the compliance of the ribs of an inflated cell when compressed.</summary>
    public float RibCompression { get; init; } = 1e-3f;

    /// <summary>Gets the compliance of the bending constraints of an inflated cell.</summary>
    public float Bend { get; init; } = 4e-3f;

    /// <summary>
    /// Gets the compliance against compression and bending in the trailing edge bay (behind the last line row and the rear
    /// 40 % of the chord), inflated or not. The profile is thin there, and pressure keeps a thin bay from wrinkling only
    /// against tiny moments: the brakes curl the trailing edge down (camber) against the airload instead of pitching a
    /// rigid profile.
    /// </summary>
    public float TrailingEdgeCompression { get; init; } = 0.005f;

    /// <summary>Gets the compliance of all canopy constraints against compression and folding when the cells are empty (fabric buckles).</summary>
    public float Deflated { get; init; } = 0.05f;

    /// <summary>Gets the bending compliance near the nose when the wing has leading edge rods (they keep the nose round, inflated or not).</summary>
    public float RodBend { get; init; } = 4e-3f;

    /// <summary>Gets the axial stiffness EA of a line (N).</summary>
    public float LineStiffness { get; init; } = 30000f;
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

    /// <summary>
    /// Gets the proxy nodes along each line of the line plan that is a proxy line, keyed by its upper and lower rigging
    /// point: from the upper end through the points along it to the lower end.
    /// </summary>
    public Dictionary<(int Upper, int Lower), int[]> LinePaths { get; init; } = [];

    /// <summary>
    /// Gets, for a proxy without cascades, the proxy nodes from each tab (rigging point) down its line to the riser or
    /// pulley.
    /// </summary>
    public Dictionary<int, int[]> TabPaths { get; init; } = [];
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
    // A line of the proxy: its constraints from the upper end down, and the nodes along it (both ends included).
    private sealed record LineChain(string Name, ConstraintKind Kind, List<int> Nodes, List<int> Constraints)
    {
        public int Upper => Nodes[0];
        public int Lower => Nodes[^1];
    }

    /// <summary>Builds the proxy of a glider.</summary>
    public static ProxyBuild Build(GliderShape shape, RiggingLayout rigging, ProxyMaterial? material = null)
    {
        var design = shape.Design;
        var settings = ProxySettings.FromDesign(design);
        material ??= new ProxyMaterial();
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
        // The trailing edge bay: behind the last line row, and at least the rear 40 % of the chord (a two-liner holds the middle
        // of its profile with rods and the inflated structure).
        BuildFabric(model, settings.DoubleSurface, material, design.LeadingEdgeRods, Math.Max(0.6f, (float)design.RowPositions.Max()));
        var (rigNodes, chains) = BuildRigging(model, shape, rigging, sectionRibs, settings, material);
        if (settings.DoubleSurface) BuildDiagonalRibs(model, material);
        BuildStrips(model, shape, sectionRibs, settings.DoubleSurface);
        SubdivideLines(model, chains, settings);
        DistributeMass(model, shape, design);
        BuildControls(model, shape, rigging, rigNodes, chains);

        // Which proxy nodes each line of the line plan runs through, for skinning the line tubes.
        var linePaths = new Dictionary<(int, int), int[]>();
        var tabPaths = new Dictionary<int, int[]>();
        var chainByEnds = chains.GroupBy(c => (c.Upper, c.Lower)).ToDictionary(g => g.Key, g => g.First());
        foreach (var line in rigging.Lines)
        {
            if (rigNodes.TryGetValue(line.Upper, out int a) && rigNodes.TryGetValue(line.Lower, out int b) && chainByEnds.TryGetValue((a, b), out var chain))
            {
                linePaths[(line.Upper, line.Lower)] = [.. chain.Nodes];
            }
        }
        if (!settings.Cascades)
        {
            foreach (var tab in rigging.Points.Where(p => p.Kind is RigPointKind.Tab or RigPointKind.BrakeTab))
            {
                if (!rigNodes.TryGetValue(tab.Id, out int node)) continue;
                if (chains.FirstOrDefault(c => c.Upper == node && c.Kind == ConstraintKind.Line) is { } chain) tabPaths[tab.Id] = [.. chain.Nodes];
            }
        }
        return new ProxyBuild { Model = model, Settings = settings, RigPointNodes = rigNodes, LinePaths = linePaths, TabPaths = tabPaths };
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

    private static void BuildFabric(ProxyModel model, bool doubleSurface, ProxyMaterial material, bool rods, float softFrom)
    {
        // In the trailing edge bay (from softFrom on) the fabric is limp against compression and bending.
        bool Aft(ProxySection section, int k, int count) => doubleSurface && StationOf(section, k, count) >= softFrom - 1e-4f;
        float softCompression = Math.Max(material.TrailingEdgeCompression, material.FabricCompression);
        float ribSoft = Math.Max(material.TrailingEdgeCompression, material.RibCompression);
        // A single camber surface has no cells to inflate: it stays a stiff, arcade-style sail.
        float fabricCompression = doubleSurface ? material.FabricCompression : 4e-4f;
        float shearCompression = doubleSurface ? material.ShearCompression : material.Shear;
        float bend = doubleSurface ? material.Bend : 4e-3f;
        float deflated = doubleSurface ? material.Deflated : 0;
        var surfaces = doubleSurface ? new[] { true, false } : new[] { false };
        for (int s = 0; s < model.Sections.Count; s++)
        {
            var section = model.Sections[s];
            foreach (bool upper in surfaces)
            {
                var chain = Chain(section, upper);
                for (int k = 0; k < chain.Count - 1; k++)
                    AddDistance(model, chain[k], chain[k + 1], ConstraintKind.Chordwise, material.Fabric, Aft(section, k, chain.Count) ? softCompression : fabricCompression, deflated);
                for (int k = 0; k < chain.Count - 2; k++)
                {
                    // Leading edge rods keep the nose round (up to about a fifth of the chord).
                    bool rod = rods && doubleSurface && StationOf(section, k + 2, chain.Count) <= 0.2f;
                    float c = rod ? material.RodBend : bend;
                    AddDistance(model, chain[k], chain[k + 2], ConstraintKind.Bend, c, Aft(section, k + 1, chain.Count) ? Math.Max(c, softCompression) : c, rod ? 0 : deflated);
                }
            }
            if (doubleSurface)
            {
                int count = section.Upper.Count + 2;
                for (int k = 0; k < section.Upper.Count; k++)
                {
                    // Station k + 1 of the chain; the rib at the last row itself holds that row's tabs.
                    bool behind = StationOf(section, k + 1, count) > softFrom + 1e-4f;
                    AddDistance(model, section.Upper[k], section.Lower[k], ConstraintKind.Rib, material.Rib, behind ? ribSoft : material.RibCompression, deflated);
                    if (k + 1 < section.Upper.Count)
                    {
                        float diagonal = Aft(section, k + 1, count) ? ribSoft : material.RibCompression;
                        AddDistance(model, section.Upper[k], section.Lower[k + 1], ConstraintKind.Rib, material.Rib, diagonal, deflated);
                        AddDistance(model, section.Upper[k + 1], section.Lower[k], ConstraintKind.Rib, material.Rib, diagonal, deflated);
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
                        bool behind = doubleSurface && StationOf(section, k, a.Count) > softFrom + 1e-4f;
                        AddDistance(model, a[k], b[k], ConstraintKind.Spanwise, material.Fabric, behind ? softCompression : fabricCompression, deflated);
                    }
                    if (k + 1 < a.Count)
                    {
                        // The skin of the trailing edge bay wrinkles as the brakes curl it.
                        float shear = Aft(section, k, a.Count) ? Math.Max(softCompression, shearCompression) : shearCompression;
                        AddDistance(model, a[k], b[k + 1], ConstraintKind.Shear, material.Shear, shear, deflated);
                        AddDistance(model, a[k + 1], b[k], ConstraintKind.Shear, material.Shear, shear, deflated);
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
                    for (int k = 0; k < a.Count; k++) AddDistance(model, a[k], c[k], ConstraintKind.Bend, bend, bend, deflated);
                }
            }
        }
    }

    // The chord fraction of the k-th node of a section's chain (0 at the nose, 1 at the tail).
    private static float StationOf(ProxySection section, int k, int count) => k == 0 ? 0 : k >= count - 1 ? 1 : section.Stations[k - 1];

    // Diagonal (V) ribs: a section without lines hangs from the line attachments of its neighbors, from their lower
    // surface up to its upper surface, as the diagonal ribs between tab ribs do in a real wing.
    private static void BuildDiagonalRibs(ProxyModel model, ProxyMaterial material)
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
                    if (withLines.Contains(neighbor.Lower[k])) AddDistance(model, neighbor.Lower[k], section.Upper[k], ConstraintKind.Rib, material.Rib, material.RibCompression, material.Deflated);
                }
            }
        }
    }

    private static (Dictionary<int, int> Map, List<LineChain> Chains) BuildRigging(ProxyModel model, GliderShape shape, RiggingLayout rigging,
        List<int> sectionRibs, ProxySettings settings, ProxyMaterial material)
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
        // Lines are tension-only: they go slack when unloaded.
        var added = new HashSet<(int, int)>();
        void AddLine(int a, int b, string name, ConstraintKind kind = ConstraintKind.Line)
        {
            if (a == b || !added.Add((Math.Min(a, b), Math.Max(a, b)))) return;
            float length = Vector3.Distance(model.Nodes[a].Position, model.Nodes[b].Position);
            float compliance = kind == ConstraintKind.Line ? length / material.LineStiffness : 2e-6f;
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
        foreach (var line in rigging.Lines.Where(l => l.Level is LineLevel.Riser))
        {
            AddLine(map[line.Upper], map[line.Lower], line.Name, ConstraintKind.Riser);
        }
        // Every line and riser so far is one constraint from its upper to its lower end (SubdivideLines adds points).
        var chains = model.Constraints.Select((c, i) => (c, i))
            .Where(p => p.c.Kind is ConstraintKind.Line or ConstraintKind.Riser)
            .Select(p => new LineChain(p.c.Name ?? string.Empty, p.c.Kind, [p.c.A, p.c.B], [p.i]))
            .ToList();

        // The pilot hangs from the carabiners.
        int left = map[rigging.Carabiner(1).Id], right = map[rigging.Carabiner(-1).Id];
        var pilotPosition = (model.Nodes[left].Position + model.Nodes[right].Position) / 2 - new Vector3(0, 0.38f, 0.05f);
        model.Pilot = AddNode(model, "Pilot", ProxyNodeKind.Pilot, pilotPosition, -1, 0, 0);
        AddDistance(model, model.Pilot, left, ConstraintKind.Harness, 0, 0);
        AddDistance(model, model.Pilot, right, ConstraintKind.Harness, 0, 0);
        AddDistance(model, left, right, ConstraintKind.Harness, 0, 0);

        foreach (int side in new[] { 1, -1 })
        {
            string s = side > 0 ? "L" : "R";
            int carabiner = map[rigging.Carabiner(side).Id];
            var tops = rigging.Points.Where(p => p.Kind == RigPointKind.RiserTop && p.Side == side).OrderBy(p => p.Row).Select(p => map[p.Id]).ToList();
            // The maillons of a side stay together (the riser webbing and its stitching); the speed bar pulls them apart.
            for (int i = 0; i < tops.Count - 1; i++)
            {
                AddDistance(model, tops[i], tops[i + 1], ConstraintKind.Harness, 1e-5f, 1e-5f);
                model.Constraints[^1].Name = $"Maillons {s} {RiggingLayout.RowName(i)}{RiggingLayout.RowName(i + 1)}";
            }
            int pulley = map[rigging.Points.First(p => p.Kind == RigPointKind.Pulley && p.Side == side).Id];
            AddDistance(model, pulley, tops[^1], ConstraintKind.Harness, 0, 0);
            AddDistance(model, pulley, carabiner, ConstraintKind.Harness, 0, 0);

            // The pilot's hand holds the toggle, at fixed distances from the pilot and both carabiners (so in the
            // harness's frame); the brakes change these distances so the toggle moves down to the hip.
            int toggle = map[rigging.Toggle(side).Id];
            foreach (int anchor in new[] { model.Pilot, carabiner, side > 0 ? right : left })
            {
                AddDistance(model, toggle, anchor, ConstraintKind.Hand, 0, 0);
                model.Constraints[^1].Name = $"Hand {s}";
            }
        }

        // Line drag: the proxy's lines together get the drag area of all the real lines (a simpler proxy has fewer,
        // so each stands for more line).
        double realArea = rigging.Lines.Where(l => l.Level is not (LineLevel.Riser or LineLevel.BrakeHandle)).Sum(l => l.Diameter / 1000 * l.Length);
        double proxyLength = model.Constraints.Where(c => c.Kind == ConstraintKind.Line).Sum(c => c.RestLength);
        foreach (var c in model.Constraints)
        {
            if (c.Kind == ConstraintKind.Line) c.DragDiameter = (float)(realArea / Math.Max(proxyLength, 1e-6));
            else if (c.Kind == ConstraintKind.Riser) c.DragDiameter = 0.025f;
        }
        return (map, chains);
    }

    // Long lines get points along them (evenly spaced), so a slack line sags and bows in the airflow instead of staying
    // straight. Brake lines always get at least two segments: they hang slack in trim and bow back.
    private static void SubdivideLines(ProxyModel model, List<LineChain> chains, ProxySettings settings)
    {
        foreach (var chain in chains)
        {
            if (chain.Kind != ConstraintKind.Line || chain.Constraints.Count != 1) continue;
            var c = model.Constraints[chain.Constraints[0]];
            bool brake = chain.Name.StartsWith("Brake", StringComparison.Ordinal);
            // Only the lines hanging on the risers and pulleys (main lines; tab lines without cascades).
            var lowerKind = model.Nodes[c.B].Kind;
            if (lowerKind is not (ProxyNodeKind.RiserTop or ProxyNodeKind.Pulley)) continue;
            int segments = settings.LineSegmentLength > 0 ? (int)Math.Ceiling(c.RestLength / settings.LineSegmentLength) : 1;
            if (brake) segments = Math.Max(segments, 2);
            if (segments < 2) continue;

            var a = model.Nodes[c.A].Position;
            var b = model.Nodes[c.B].Position;
            int index = chain.Constraints[0];
            int lower = c.B;
            float segmentRest = c.RestLength / segments;
            float compliance = c.Compliance / segments;
            int previous = c.A;
            chain.Nodes.Clear();
            chain.Nodes.Add(c.A);
            chain.Constraints.Clear();
            for (int i = 1; i <= segments; i++)
            {
                int next = i < segments
                    ? AddNode(model, $"{chain.Name} {i}", ProxyNodeKind.LinePoint, Vector3.Lerp(a, b, i / (float)segments), -1, 0, model.Nodes[lower].Side)
                    : lower;
                if (i == 1)
                {
                    c.B = next;
                    c.RestLength = segmentRest;
                    c.Compliance = compliance;
                    chain.Constraints.Add(index);
                }
                else
                {
                    model.Constraints.Add(new ProxyConstraint
                    {
                        A = previous, B = next, Kind = ConstraintKind.Line, RestLength = segmentRest, Compliance = compliance,
                        TensionOnly = true, Name = c.Name, DragDiameter = c.DragDiameter,
                    });
                    chain.Constraints.Add(model.Constraints.Count - 1);
                }
                chain.Nodes.Add(next);
                previous = next;
            }
        }
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

        // The lines' mass goes to the knots and points along them, by the length of line they carry.
        var lineShare = new double[model.Nodes.Count];
        var lines = model.Constraints.Where(c => c.Kind == ConstraintKind.Line).ToList();
        double lineLength = Math.Max(1e-6, lines.Sum(c => c.RestLength));
        foreach (var c in lines)
        {
            lineShare[c.A] += c.RestLength / 2;
            lineShare[c.B] += c.RestLength / 2;
        }
        foreach (var node in model.Nodes)
        {
            node.Mass = node.Kind switch
            {
                ProxyNodeKind.Pilot => (float)design.PilotMass,
                ProxyNodeKind.Carabiner => 0.4f,
                ProxyNodeKind.Pulley or ProxyNodeKind.Toggle => 0.05f,
                ProxyNodeKind.RiserTop => 0.05f + (float)(lineMass * lineShare[node.Id] / lineLength),
                ProxyNodeKind.Knot or ProxyNodeKind.LinePoint => (float)(lineMass * lineShare[node.Id] / lineLength),
                _ => (float)(canopyMass * weights[node.Id] / total),
            };
            node.Mass = Math.Max(node.Mass, 0.002f);
        }
    }

    private static void BuildControls(ProxyModel model, GliderShape shape, RiggingLayout rigging, Dictionary<int, int> map, List<LineChain> chains)
    {
        var design = shape.Design;
        float halfProjected = (float)shape.ProjectedSpan / 2;
        int rows = design.RowPositions.Length;
        float slack = (float)design.BrakeSlack, travel = (float)design.BrakeTravel;
        int leftCarabiner = map[rigging.Carabiner(1).Id], rightCarabiner = map[rigging.Carabiner(-1).Id];

        // Shortens a whole line by `amount` at full input, spread over its segments by their length.
        static void Pull(ProxyModel model, ProxyControl control, LineChain chain, float amount)
        {
            float length = chain.Constraints.Sum(i => model.Constraints[i].RestLength);
            foreach (int i in chain.Constraints)
            {
                control.Constraints.Add(i);
                control.Travel.Add(amount * model.Constraints[i].RestLength / length);
            }
        }

        foreach (int side in new[] { 1, -1 })
        {
            string s = side > 0 ? "Left" : "Right";
            int pulley = map[rigging.Points.First(p => p.Kind == RigPointKind.Pulley && p.Side == side).Id];
            var brake = new ProxyControl { Name = "Brake" + s };
            foreach (var chain in chains.Where(c => c.Kind == ConstraintKind.Line && c.Lower == pulley))
            {
                // Brake lines have some slack at rest: the first centimeters of travel do nothing.
                float length = chain.Constraints.Sum(i => model.Constraints[i].RestLength);
                foreach (int i in chain.Constraints) model.Constraints[i].RestLength *= 1 + slack / length;
                Pull(model, brake, chain, travel + slack);
            }

            // The hand: the toggle moves from below the pulley down along the brake line by the whole travel, toward the
            // hip, staying behind the plane of the pilot and the carabiners (where its three distances keep it).
            int toggle = map[rigging.Toggle(side).Id];
            int carabiner = side > 0 ? leftCarabiner : rightCarabiner;
            var rest = model.Nodes[toggle].Position;
            var pulleyPosition = model.Nodes[pulley].Position;
            var hip = model.Nodes[carabiner].Position + new Vector3(side * 0.12f, -0.45f, 0);
            var full = pulleyPosition + Vector3.Normalize(hip - pulleyPosition) * (Vector3.Distance(rest, pulleyPosition) + travel + slack);
            var pilot = model.Nodes[model.Pilot].Position;
            var normal = Vector3.Normalize(Vector3.Cross(model.Nodes[leftCarabiner].Position - pilot, model.Nodes[rightCarabiner].Position - pilot));
            float restSide = Vector3.Dot(rest - pilot, normal), fullSide = Vector3.Dot(full - pilot, normal);
            float wanted = MathF.Sign(restSide) * MathF.Max(0.04f, MathF.Sign(restSide) * fullSide);
            full += normal * (wanted - fullSide);
            for (int i = 0; i < model.Constraints.Count; i++)
            {
                var c = model.Constraints[i];
                if (c.Kind != ConstraintKind.Hand || c.A != toggle) continue;
                brake.Constraints.Add(i);
                brake.Travel.Add(c.RestLength - Vector3.Distance(full, model.Nodes[c.B].Position));
            }
            model.Controls.Add(brake);

            // Pulling A lines down folds the canopy they hold: an asymmetric collapse folds the outer half of the half
            // span, big ears the outer quarter. Each line is pulled by the share of the canopy it holds in that part.
            var collapse = new ProxyControl { Name = "Collapse" + s };
            var ears = new ProxyControl { Name = "BigEars" + s };
            int riserA = map[rigging.RiserTop(0, side).Id];
            foreach (var chain in chains.Where(c => c.Kind == ConstraintKind.Line && c.Lower == riserA && c.Name.StartsWith('A')))
            {
                float collapseShare = OuterShare(model, chain, CollapseSpan);
                float earsShare = OuterShare(model, chain, BigEarsSpan);
                if (collapseShare > 0) Pull(model, collapse, chain, 1.2f * collapseShare);
                if (earsShare > 0) Pull(model, ears, chain, 0.7f * earsShare);
            }
            CloseOuter(model, collapse, side, CollapseSpan);
            CloseOuter(model, ears, side, BigEarsSpan);
            model.Controls.Add(collapse);
            model.Controls.Add(ears);
        }

        // The speed bar shortens the front risers (A the most, the last row not at all), which pulls their maillons
        // apart; a frontal collapse pulls all the A lines.
        var speedBar = new ProxyControl { Name = "SpeedBar" };
        var frontal = new ProxyControl { Name = "Frontal" };
        float barTravel = (float)design.SpeedBarTravel;
        float RiserTravel(int row) => row < rows - 1 ? barTravel * (rows - 1 - row) / (rows - 1) : 0;
        for (int i = 0; i < model.Constraints.Count; i++)
        {
            var c = model.Constraints[i];
            if (c.Kind == ConstraintKind.Riser && c.Name is { Length: > 6 } name)
            {
                int row = name[^1] - 'A';
                if (row >= 0 && RiserTravel(row) > 0)
                {
                    speedBar.Constraints.Add(i);
                    speedBar.Travel.Add(RiserTravel(row));
                }
            }
            else if (c.Kind == ConstraintKind.Harness && c.Name is { } maillons && maillons.StartsWith("Maillons", StringComparison.Ordinal))
            {
                int row = maillons[^2] - 'A';
                float offset = RiserTravel(row) - RiserTravel(row + 1);
                speedBar.Constraints.Add(i);
                speedBar.Travel.Add(c.RestLength - MathF.Sqrt(c.RestLength * c.RestLength + offset * offset));
            }
        }
        foreach (int side in new[] { 1, -1 })
        {
            int riserA = map[rigging.RiserTop(0, side).Id];
            foreach (var chain in chains.Where(c => c.Kind == ConstraintKind.Line && c.Lower == riserA && c.Name.StartsWith('A')))
            {
                Pull(model, frontal, chain, 0.55f);
                Close(model, frontal, chain);
            }
        }
        model.Controls.Add(speedBar);
        FillGaps(frontal);
        model.Controls.Add(frontal);
    }


    // The canopy nodes a line holds up: walking up the cascade from its upper end.
    private static List<int> HeldNodes(ProxyModel model, LineChain chain)
    {
        var below = new HashSet<int>(chain.Nodes);
        var seen = new HashSet<int> { chain.Upper };
        var queue = new Queue<int>([chain.Upper]);
        var held = new List<int>();
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            if (model.Nodes[node].Section >= 0)
            {
                held.Add(node);
                continue;
            }
            foreach (var c in model.Constraints)
            {
                if (c.Kind != ConstraintKind.Line || (c.A != node && c.B != node)) continue;
                int other = c.A == node ? c.B : c.A;
                if (below.Contains(other) || !seen.Add(other)) continue;
                queue.Enqueue(other);
            }
        }
        return held;
    }


    // The outer part of the half span (fraction from the tip) an asymmetric collapse and big ears fold.
    private const float CollapseSpan = 0.5f, BigEarsSpan = 0.25f;

    // The share of the canopy nodes a line holds that lie in the outer `span` of the half span (by η).
    private static float OuterShare(ProxyModel model, LineChain chain, float span)
    {
        var held = HeldNodes(model, chain);
        if (held.Count == 0) return 0;
        return held.Count(n => Math.Abs(model.Sections[model.Nodes[n].Section].Eta) >= 1 - span - 1e-4f) / (float)held.Count;
    }

    // The control closes the inlets of the cells in the outer `span` of a side's half span.
    private static void CloseOuter(ProxyModel model, ProxyControl control, int side, float span)
    {
        for (int s = 0; s < model.Strips.Count; s++)
        {
            float a = model.Sections[model.Strips[s].SectionA].Eta, b = model.Sections[model.Strips[s].SectionB].Eta;
            float middle = (a + b) / 2;
            if (Math.Sign(middle) == side && Math.Abs(middle) >= 1 - span) control.Strips.Add(s);
        }
    }
    // The cells between those the control's lines hold close too (the folded leading edge spans them).
    private static void FillGaps(ProxyControl control)
    {
        if (control.Strips.Count == 0) return;
        int first = control.Strips.Min(), last = control.Strips.Max();
        control.Strips = Enumerable.Range(first, last - first + 1).ToList();
    }
    // The mean lateral distance (m) of the canopy nodes a line holds up.
    private static float TabSpan(ProxyModel model, LineChain chain)
    {
        var held = HeldNodes(model, chain);
        return held.Count > 0 ? held.Average(n => Math.Abs(model.Nodes[n].Position.X)) : Math.Abs(model.Nodes[chain.Upper].Position.X);
    }

    // The control closes the inlets of the strips between the outermost sections the line holds.
    private static void Close(ProxyModel model, ProxyControl control, LineChain chain)
    {
        var sections = HeldNodes(model, chain).Select(n => model.Nodes[n].Section).ToList();
        if (sections.Count == 0) return;
        int first = sections.Min(), last = sections.Max();
        for (int s = 0; s < model.Strips.Count; s++)
        {
            var strip = model.Strips[s];
            if (strip.SectionA >= first && strip.SectionB <= last && !control.Strips.Contains(s)) control.Strips.Add(s);
        }
    }
    private static void AddDistance(ProxyModel model, int a, int b, ConstraintKind kind, float compliance, float compression, float deflated = 0)
    {
        model.Constraints.Add(new ProxyConstraint
        {
            A = a,
            B = b,
            Kind = kind,
            RestLength = Vector3.Distance(model.Nodes[a].Position, model.Nodes[b].Position),
            Compliance = compliance,
            CompressionCompliance = compression,
            DeflatedCompliance = deflated,
        });
    }
}
