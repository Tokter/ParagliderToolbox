namespace ParagliderToolbox.Terrain.Features;

/// <summary>
/// A one-pixel skeleton (<see cref="Skeletons.Thin"/>) as a graph: nodes at its ends and junctions (touching junction
/// pixels are one node), edges along the pixels between them. Pruning removes the short spurs thinning leaves on wide
/// shapes; merging joins edges that meet at a node of two into one line.
/// </summary>
public sealed class SkeletonGraph
{
    /// <summary>An edge: the nodes it joins and its pixels from <see cref="A"/> to <see cref="B"/> (both nodes' pixels included).</summary>
    public sealed class Edge
    {
        /// <summary>Gets or sets the start node.</summary>
        public int A { get; set; }

        /// <summary>Gets or sets the end node.</summary>
        public int B { get; set; }

        /// <summary>Gets the pixels from the start to the end.</summary>
        public List<(int X, int Y)> Pixels { get; init; } = [];

        /// <summary>Gets the length along its pixels (pixels; diagonal steps count √2).</summary>
        public double Length
        {
            get
            {
                double length = 0;
                for (int k = 1; k < Pixels.Count; k++)
                {
                    int dx = Pixels[k].X - Pixels[k - 1].X, dy = Pixels[k].Y - Pixels[k - 1].Y;
                    length += dx != 0 && dy != 0 ? Math.Sqrt(2) : 1;
                }
                return length;
            }
        }
    }

    /// <summary>Gets the nodes' pixels.</summary>
    public List<(int X, int Y)> Nodes { get; } = [];

    /// <summary>Gets the edges.</summary>
    public List<Edge> Edges { get; private set; } = [];

    /// <summary>Gets the number of edge ends at each node (a loop counts twice).</summary>
    public int[] Degrees()
    {
        var degree = new int[Nodes.Count];
        foreach (var edge in Edges)
        {
            degree[edge.A]++;
            degree[edge.B]++;
        }
        return degree;
    }

    /// <summary>Traces a skeleton.</summary>
    public static SkeletonGraph Build(bool[] skeleton, int width, int height)
    {
        var graph = new SkeletonGraph();
        int Count(int i)
        {
            int x = i % width, y = i / width, count = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && yy >= 0 && xx < width && yy < height && skeleton[yy * width + xx]) count++;
            }
            return count;
        }
        IEnumerable<int> Around(int i)
        {
            int x = i % width, y = i / width;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && yy >= 0 && xx < width && yy < height && skeleton[yy * width + xx]) yield return yy * width + xx;
            }
        }

        var count = new int[skeleton.Length];
        var nodeOf = new int[skeleton.Length];
        Array.Fill(nodeOf, -1);
        for (int i = 0; i < skeleton.Length; i++) if (skeleton[i]) count[i] = Count(i);
        var members = new List<List<int>>();
        int AddNode(int i)
        {
            graph.Nodes.Add((i % width, i / width));
            members.Add([i]);
            nodeOf[i] = graph.Nodes.Count - 1;
            return graph.Nodes.Count - 1;
        }

        // Nodes: ends alone, junction pixels joined with the junction pixels they touch.
        var stack = new Stack<int>();
        for (int i = 0; i < skeleton.Length; i++)
        {
            if (!skeleton[i] || count[i] == 2 || nodeOf[i] >= 0) continue;
            int node = AddNode(i);
            if (count[i] < 3) continue;
            stack.Push(i);
            while (stack.Count > 0)
            {
                foreach (int j in Around(stack.Pop()))
                {
                    if (count[j] < 3 || nodeOf[j] >= 0) continue;
                    nodeOf[j] = node;
                    members[node].Add(j);
                    stack.Push(j);
                }
            }
        }

        var walked = new HashSet<long>();
        var onEdge = new bool[skeleton.Length];
        long Step(int from, int to) => (long)from * skeleton.Length + to;
        void Walk(int from, int to)
        {
            if (!walked.Add(Step(from, to))) return;
            var pixels = new List<(int, int)> { (from % width, from / width) };
            int previous = from, current = to;
            while (true)
            {
                pixels.Add((current % width, current / width));
                onEdge[current] = true;
                if (nodeOf[current] >= 0) break;
                int next = -1;
                foreach (int j in Around(current))
                {
                    if (j == previous || (nodeOf[j] >= 0 && nodeOf[j] == nodeOf[from] && pixels.Count < 3)) continue;
                    next = j;
                    if (nodeOf[j] < 0) break;
                }
                if (next < 0)
                {
                    // A dead end that isn't a node (shouldn't happen after thinning): make it one.
                    AddNode(current);
                    break;
                }
                previous = current;
                current = next;
            }
            walked.Add(Step(current, previous));
            graph.Edges.Add(new Edge { A = nodeOf[from], B = nodeOf[current], Pixels = pixels });
        }

        for (int node = 0; node < members.Count; node++)
        {
            foreach (int pixel in members[node].ToArray())
            {
                foreach (int j in Around(pixel))
                {
                    if (nodeOf[j] == node) continue;
                    if (nodeOf[j] >= 0)
                    {
                        // Two nodes side by side: one edge between them.
                        if (nodeOf[j] > node && walked.Add(Step(pixel, j)))
                        {
                            graph.Edges.Add(new Edge { A = node, B = nodeOf[j], Pixels = [(pixel % width, pixel / width), (j % width, j / width)] });
                        }
                        continue;
                    }
                    Walk(pixel, j);
                }
            }
        }
        // Loops without nodes: a node anywhere on them.
        for (int i = 0; i < skeleton.Length; i++)
        {
            if (!skeleton[i] || nodeOf[i] >= 0 || onEdge[i]) continue;
            AddNode(i);
            foreach (int j in Around(i))
            {
                Walk(i, j);
                break;
            }
        }
        return graph;
    }

    /// <summary>
    /// Removes spurs (edges from a junction to a free end) that <paramref name="isSpur"/> picks, given the edge and its
    /// junction node, and joins the edges left meeting in twos; repeats until nothing changes.
    /// </summary>
    public void Prune(Func<Edge, int, bool> isSpur)
    {
        for (int round = 0; round < 20; round++)
        {
            var degree = Degrees();
            var removed = new HashSet<Edge>();
            foreach (var edge in Edges)
            {
                if (edge.A == edge.B) continue;
                bool aFree = degree[edge.A] == 1, bFree = degree[edge.B] == 1;
                if (aFree == bFree) continue;
                int junction = aFree ? edge.B : edge.A;
                if (degree[junction] < 3 || !isSpur(edge, junction)) continue;
                removed.Add(edge);
                // One spur per junction per round: two short ones at a node could both be the only way on.
                degree[junction] = 0;
            }
            if (removed.Count == 0) break;
            Edges = Edges.Where(e => !removed.Contains(e)).ToList();
            MergeChains();
        }
        MergeChains();
    }

    /// <summary>Removes edges that <paramref name="drop"/> picks.</summary>
    public void Remove(Func<Edge, bool> drop) => Edges = Edges.Where(e => !drop(e)).ToList();

    /// <summary>Joins edges that meet at nodes of degree two into single edges.</summary>
    public void MergeChains()
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            var incident = new List<Edge>[Nodes.Count];
            foreach (var edge in Edges)
            {
                (incident[edge.A] ??= []).Add(edge);
                if (edge.B != edge.A) (incident[edge.B] ??= []).Add(edge);
            }
            var gone = new HashSet<Edge>();
            var added = new List<Edge>();
            for (int node = 0; node < Nodes.Count; node++)
            {
                if (incident[node] is not { Count: 2 } pair) continue;
                var (first, second) = (pair[0], pair[1]);
                if (first == second || first.A == first.B || second.A == second.B || gone.Contains(first) || gone.Contains(second)) continue;
                // first runs into the node, second out of it.
                var into = first.B == node ? first.Pixels : Enumerable.Reverse(first.Pixels).ToList();
                var outOf = second.A == node ? second.Pixels : Enumerable.Reverse(second.Pixels).ToList();
                int start = first.B == node ? first.A : first.B, end = second.A == node ? second.B : second.A;
                var pixels = new List<(int X, int Y)>(into);
                pixels.AddRange(outOf.Skip(1));
                gone.Add(first);
                gone.Add(second);
                var joined = new Edge { A = start, B = end, Pixels = pixels };
                added.Add(joined);
                // The joined edge's ends may be degree-two nodes too: pick them up in the next pass.
                merged = true;
            }
            if (!merged) break;
            Edges = Edges.Where(e => !gone.Contains(e)).Concat(added).ToList();
        }
    }

    /// <summary>Groups the edges into connected parts.</summary>
    public List<List<Edge>> Components()
    {
        var parent = Enumerable.Range(0, Nodes.Count).ToArray();
        int Find(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }
        foreach (var edge in Edges) parent[Find(edge.A)] = Find(edge.B);
        return Edges.GroupBy(e => Find(e.A)).Select(g => g.ToList()).ToList();
    }
}
