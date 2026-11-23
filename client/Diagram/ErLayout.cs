namespace GranitDb.Client.Diagram;

/// <summary>
///     Placement automatique d'un diagramme entité-relation, en couches (méthode de Sugiyama) :
///     <list type="number">
///         <item>composantes connexes placées chacune pour soi, puis rangées en étagères ;</item>
///         <item>suppression des cycles (heuristique d'Eades, Lin et Smyth : le moins de liens inversés) ;</item>
///         <item>choix des couches par le chemin le plus long, puis resserrage : une table se rapproche de ses voisines
///         selon que ses liens entrants ou sortants dominent, ce qui raccourcit les liens ;</item>
///         <item>un point de passage par couche traversée pour les liens longs, de sorte que l'ordre et la hauteur
///         comptent aussi les liens qui contournent les tables ;</item>
///         <item>ordre dans chaque couche : barycentre en balayages alternés, échange de voisines, plusieurs points de
///         départ, on garde l'ordre qui a le moins de croisements (décompte exact en O(n log n)) ;</item>
///         <item>hauteurs : chaque table se place face aux colonnes qui la relient (et non au milieu de la table), les
///         contraintes de non-chevauchement étant résolues exactement (régression isotone) ;</item>
///         <item>les liens longs sont tracés par leurs points de passage (voir <see cref="ErDiagram.Route" />).</item>
///     </list>
///     Calcul pur et déterministe : mêmes tables, mêmes liens, même dessin.
/// </summary>
internal static class ErLayout
{
    private const double DummyHeight = 10;
    private const double DummyGap = 10;
    private const double MixedGap = 16;
    private const double ComponentGap = 60;

    /// <summary>Au-delà, un seul point de départ et pas d'échange de voisines : le coût reste raisonnable.</summary>
    private const int LargeGraph = 900;

    internal sealed class Result
    {
        /// <summary>Points de passage (de gauche à droite) des relations qui traversent au moins une couche.</summary>
        public Dictionary<string, List<(double Left, double Right, double Y)>> Waypoints { get; } = new();

        /// <summary>Croisements de liens entre couches voisines dans le dessin obtenu.</summary>
        public int Crossings { get; set; }
    }

    private sealed class LNode
    {
        public int Index;
        public ErNode? Real;
        public double Width, Height;
        public int Layer, Pos, LayerSize;
        public double Top;
        public readonly List<LSeg> In = [];
        public readonly List<LSeg> Out = [];
        public bool Dummy => Real == null;
    }

    private sealed class LSeg(LNode from, LNode to, double fromOffset, double toOffset, double weight)
    {
        public readonly LNode From = from, To = to;
        public readonly double FromOffset = fromOffset, ToOffset = toOffset, Weight = weight;
    }

    private sealed class Placed
    {
        public List<(ErNode Node, double X, double Y)> Nodes { get; } = [];
        public List<(string EdgeId, List<(double Left, double Right, double Y)> Points)> Waypoints { get; } = [];
        public double Width, Height;
        public int Crossings;
        public string Key = "";
    }

    public static Result Run(IReadOnlyList<ErNode> nodes, IReadOnlyList<ErEdge> edges, double columnGap, double rowGap)
    {
        var result = new Result();
        var real = edges.Where(e => e.ReferencingTableId != e.ReferencedTableId).ToList();
        var linkedIds = real.SelectMany(e => new[] { e.ReferencingTableId, e.ReferencedTableId }).ToHashSet();
        var linked = nodes.Where(n => linkedIds.Contains(n.Id))
            .OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

        var placed = new List<Placed>();
        foreach (var (members, memberEdges) in Components(linked, real))
            placed.Add(LayoutComponent(members, memberEdges, columnGap, rowGap));

        var bottom = ErDiagram.Margin;
        if (placed.Count > 0)
        {
            result.Crossings = placed.Sum(p => p.Crossings);
            bottom = Pack(placed, result) + rowGap;
        }

        PlaceLoose(nodes.Where(n => !linkedIds.Contains(n.Id)).ToList(), bottom, columnGap, rowGap);
        return result;
    }

    // --- Composantes connexes ---

    private static List<(List<ErNode> Nodes, List<ErEdge> Edges)> Components(List<ErNode> linked, List<ErEdge> edges)
    {
        var parent = linked.Select((_, i) => i).ToArray();
        var index = linked.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);

        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        foreach (var e in edges)
        {
            var a = Find(index[e.ReferencingTableId]);
            var b = Find(index[e.ReferencedTableId]);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        var groups = new List<(List<ErNode>, List<ErEdge>)>();
        var slot = new Dictionary<int, int>();
        for (var i = 0; i < linked.Count; i++)
        {
            var root = Find(i);
            if (!slot.TryGetValue(root, out var s))
            {
                s = groups.Count;
                slot[root] = s;
                groups.Add(([], []));
            }

            groups[s].Item1.Add(linked[i]);
        }

        foreach (var e in edges) groups[slot[Find(index[e.ReferencingTableId])]].Item2.Add(e);
        return groups;
    }

    // --- Une composante ---

    private static Placed LayoutComponent(List<ErNode> members, List<ErEdge> edges, double columnGap, double rowGap)
    {
        var n = members.Count;
        var index = members.Select((m, i) => (m.Id, i)).ToDictionary(x => x.Id, x => x.i);

        // Liens logiques : le référencé à gauche, celui qui référence à droite.
        var pairs = new SortedDictionary<(int A, int B), List<ErEdge>>();
        foreach (var e in edges)
        {
            var key = (index[e.ReferencedTableId], index[e.ReferencingTableId]);
            if (!pairs.TryGetValue(key, out var list)) pairs[key] = list = [];
            list.Add(e);
        }

        var order = BreakCycles(n, pairs);
        var rank = new int[n];
        for (var i = 0; i < n; i++) rank[order[i]] = i;

        // Graphe sans cycle : un lien « à rebours » est inversé pour le calcul des couches (il reste tracé normalement).
        var dag = new SortedDictionary<(int U, int V), List<ErEdge>>();
        foreach (var (key, list) in pairs)
        {
            var oriented = rank[key.A] < rank[key.B] ? key : (key.B, key.A);
            if (!dag.TryGetValue(oriented, out var l)) dag[oriented] = l = [];
            l.AddRange(list);
        }

        var layer = Layers(n, order, dag);
        var layerCount = layer.Max() + 1;

        var lnodes = new List<LNode>();
        var layers = Enumerable.Range(0, layerCount).Select(_ => new List<LNode>()).ToList();
        for (var i = 0; i < n; i++)
            lnodes.Add(new LNode { Index = i, Real = members[i], Width = members[i].Width, Height = members[i].Height, Layer = layer[i] });

        var chains = new List<(List<ErEdge> Edges, List<LNode> Dummies)>();
        var dummies = new List<LNode>();
        foreach (var ((u, v), list) in dag)
        {
            var from = lnodes[u];
            var weight = list.Count;
            var prev = from;
            var prevOffset = list.Average(e => Port(members[u], e));
            var chain = new List<LNode>();
            for (var l = layer[u] + 1; l < layer[v]; l++)
            {
                var d = new LNode { Index = n + dummies.Count, Height = DummyHeight, Layer = l };
                dummies.Add(d);
                chain.Add(d);
                Link(prev, d, prevOffset, DummyHeight / 2, weight);
                prev = d;
                prevOffset = DummyHeight / 2;
            }

            Link(prev, lnodes[v], prevOffset, list.Average(e => Port(members[v], e)), weight);
            if (chain.Count > 0) chains.Add((list, chain));
        }

        var all = lnodes.Concat(dummies).ToList();

        // Point de départ 1 : ordre de suppression des cycles (parcours topologique) ; 2 : ordre alphabétique.
        var starts = new List<List<List<LNode>>> { Initial(layers, lnodes.OrderBy(x => rank[x.Index]), dummies) };
        if (all.Count <= LargeGraph) starts.Add(Initial(layers, lnodes, dummies));
        var maxIter = all.Count <= LargeGraph ? 40 : 14;
        List<List<LNode>>? best = null;
        var bestCrossings = int.MaxValue;
        foreach (var start in starts)
        {
            var crossings = Optimize(start, all.Count <= LargeGraph, maxIter);
            if (crossings >= bestCrossings) continue;
            bestCrossings = crossings;
            best = start.Select(c => c.ToList()).ToList();
            if (crossings == 0) break;
        }

        layers = best!;
        Renumber(layers);

        AssignHeights(layers, rowGap);
        var placed = new Placed { Crossings = bestCrossings, Key = members[0].Name };
        var (lefts, rights) = AssignColumns(layers, placed, columnGap);

        foreach (var (list, chain) in chains)
        {
            var points = chain.OrderBy(d => d.Layer).Select(d => (lefts[d.Layer], rights[d.Layer], d.Top + d.Height / 2)).ToList();
            foreach (var e in list) placed.Waypoints.Add((e.RelationId, points));
        }

        placed.Height = Math.Max(placed.Nodes.Max(x => x.Y + x.Node.Height), placed.Waypoints.Count == 0 ? 0 : placed.Waypoints.Max(w => w.Points.Max(p => p.Y)));
        return placed;

        static void Link(LNode from, LNode to, double fromOffset, double toOffset, double weight)
        {
            var seg = new LSeg(from, to, fromOffset, toOffset, weight);
            from.Out.Add(seg);
            to.In.Add(seg);
        }
    }

    /// <summary>Hauteur, dans la table, de la ligne de la colonne qui porte le lien (milieu de l'en-tête si elle n'est pas affichée).</summary>
    private static double Port(ErNode node, ErEdge edge)
    {
        var columns = node.Id == edge.ReferencingTableId
            ? edge.Pairs().Select(p => p.Referencing)
            : edge.Pairs().Select(p => p.Referenced);
        var offsets = new List<double>();
        foreach (var column in columns)
        {
            var row = -1;
            for (var i = 0; i < node.Rows.Count; i++)
                if (node.Rows[i].ColumnId == column)
                {
                    row = i;
                    break;
                }

            offsets.Add(row < 0 ? node.HeaderHeight / 2 : node.HeaderHeight + (row + 0.5) * ErDiagram.RowHeight);
        }

        return offsets.Count == 0 ? node.HeaderHeight / 2 : offsets.Average();
    }

    // --- Suppression des cycles ---

    /// <summary>
    ///     Ordre de sommets où le moins possible de liens vont « à rebours » (heuristique d'Eades, Lin et Smyth) : les
    ///     puits partent à la fin, les sources au début, sinon le sommet dont les liens sortants dominent le plus.
    /// </summary>
    private static int[] BreakCycles(int n, SortedDictionary<(int A, int B), List<ErEdge>> pairs)
    {
        var outs = Enumerable.Range(0, n).Select(_ => new List<(int To, int Count)>()).ToArray();
        var ins = Enumerable.Range(0, n).Select(_ => new List<(int From, int Count)>()).ToArray();
        var outW = new int[n];
        var inW = new int[n];
        foreach (var ((a, b), list) in pairs)
        {
            outs[a].Add((b, list.Count));
            ins[b].Add((a, list.Count));
            outW[a] += list.Count;
            inW[b] += list.Count;
        }

        var alive = Enumerable.Repeat(true, n).ToArray();
        var left = n;
        var head = new List<int>();
        var tail = new List<int>();

        void Remove(int v)
        {
            alive[v] = false;
            left--;
            foreach (var (to, count) in outs[v])
                if (alive[to])
                    inW[to] -= count;
            foreach (var (from, count) in ins[v])
                if (alive[from])
                    outW[from] -= count;
        }

        while (left > 0)
        {
            bool progress;
            do
            {
                progress = false;
                for (var v = 0; v < n; v++)
                    if (alive[v] && outW[v] == 0)
                    {
                        tail.Add(v);
                        Remove(v);
                        progress = true;
                    }

                for (var v = 0; v < n; v++)
                    if (alive[v] && inW[v] == 0)
                    {
                        head.Add(v);
                        Remove(v);
                        progress = true;
                    }
            } while (progress);

            if (left == 0) break;
            var pick = -1;
            for (var v = 0; v < n; v++)
                if (alive[v] && (pick < 0 || outW[v] - inW[v] > outW[pick] - inW[pick]))
                    pick = v;
            head.Add(pick);
            Remove(pick);
        }

        tail.Reverse();
        return head.Concat(tail).ToArray();
    }

    // --- Couches ---

    /// <summary>
    ///     Couche de chaque sommet : chemin le plus long depuis les sources, puis chaque sommet est ramené vers ses voisins
    ///     du côté qui domine (plus de liens entrants : au plus près de ses sources ; plus de liens sortants : au plus
    ///     près de ses cibles). La longueur totale des liens ne fait que diminuer, le calcul s'arrête donc.
    /// </summary>
    private static int[] Layers(int n, int[] order, SortedDictionary<(int U, int V), List<ErEdge>> dag)
    {
        var succ = Enumerable.Range(0, n).Select(_ => new List<(int V, int W)>()).ToArray();
        var pred = Enumerable.Range(0, n).Select(_ => new List<(int V, int W)>()).ToArray();
        foreach (var ((u, v), list) in dag)
        {
            succ[u].Add((v, list.Count));
            pred[v].Add((u, list.Count));
        }

        var layer = new int[n];
        foreach (var v in order)
            foreach (var (u, _) in pred[v])
                layer[v] = Math.Max(layer[v], layer[u] + 1);

        for (var guard = 0; guard < 60; guard++)
        {
            var changed = false;
            foreach (var v in guard % 2 == 0 ? order : Enumerable.Reverse(order))
            {
                var inW = pred[v].Sum(p => p.W);
                var outW = succ[v].Sum(s => s.W);
                if (inW > outW && pred[v].Count > 0)
                {
                    var wanted = pred[v].Max(p => layer[p.V]) + 1;
                    if (wanted < layer[v])
                    {
                        layer[v] = wanted;
                        changed = true;
                    }
                }
                else if (outW > inW && succ[v].Count > 0)
                {
                    var wanted = succ[v].Min(s => layer[s.V]) - 1;
                    if (wanted > layer[v])
                    {
                        layer[v] = wanted;
                        changed = true;
                    }
                }
            }

            if (!changed) break;
        }

        // Pas de couche vide : les numéros utilisés sont renumérotés de 0 à n-1.
        var used = layer.Distinct().OrderBy(x => x).Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i);
        for (var i = 0; i < n; i++) layer[i] = used[layer[i]];
        return layer;
    }

    // --- Ordre dans les couches ---

    private static List<List<LNode>> Initial(List<List<LNode>> shape, IEnumerable<LNode> reals, List<LNode> dummies)
    {
        var layers = shape.Select(_ => new List<LNode>()).ToList();
        foreach (var node in reals) layers[node.Layer].Add(node);
        foreach (var node in dummies) layers[node.Layer].Add(node);
        return layers;
    }

    private static void Renumber(List<List<LNode>> layers)
    {
        foreach (var layer in layers)
            for (var i = 0; i < layer.Count; i++)
            {
                layer[i].Pos = i;
                layer[i].LayerSize = layer.Count;
            }
    }

    private static int Optimize(List<List<LNode>> layers, bool transpose, int maxIter)
    {
        Renumber(layers);
        var best = CountCrossings(layers);
        var snapshot = layers.Select(l => l.ToList()).ToList();
        var stale = 0;
        for (var iter = 0; iter < maxIter && best > 0; iter++)
        {
            if (iter % 2 == 0)
                for (var k = 1; k < layers.Count; k++)
                    Reorder(layers[k], true);
            else
                for (var k = layers.Count - 2; k >= 0; k--)
                    Reorder(layers[k], false);

            if (transpose) Transpose(layers);
            var crossings = CountCrossings(layers);
            if (crossings < best)
            {
                best = crossings;
                snapshot = layers.Select(l => l.ToList()).ToList();
                stale = 0;
            }
            else if (++stale >= 6)
            {
                break;
            }
        }

        for (var k = 0; k < layers.Count; k++)
        {
            layers[k].Clear();
            layers[k].AddRange(snapshot[k]);
        }

        Renumber(layers);
        return best;
    }

    /// <summary>Barycentre : chaque sommet va à la moyenne des places (0 à 1) de ses voisins de la couche fixe.</summary>
    private static void Reorder(List<LNode> layer, bool useIncoming)
    {
        var keys = new Dictionary<LNode, double>();
        foreach (var node in layer)
        {
            var neighbours = useIncoming ? node.In.Select(s => s.From) : node.Out.Select(s => s.To);
            var sum = 0.0;
            var count = 0;
            foreach (var nb in neighbours)
            {
                sum += nb.Pos / (double)Math.Max(1, nb.LayerSize - 1);
                count++;
            }

            keys[node] = count == 0 ? node.Pos / (double)Math.Max(1, node.LayerSize - 1) : sum / count;
        }

        var sorted = layer.OrderBy(x => keys[x]).ThenBy(x => x.Pos).ToList();
        layer.Clear();
        layer.AddRange(sorted);
        for (var i = 0; i < layer.Count; i++) layer[i].Pos = i;
    }

    /// <summary>Échange de deux voisines quand cela retire des croisements avec les deux couches adjacentes.</summary>
    private static void Transpose(List<List<LNode>> layers)
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var improved = false;
            foreach (var layer in layers)
                for (var i = 0; i + 1 < layer.Count; i++)
                {
                    var (u, v) = (layer[i], layer[i + 1]);
                    var (keep, swap) = PairCrossings(u, v);
                    if (swap >= keep) continue;
                    (layer[i], layer[i + 1]) = (v, u);
                    u.Pos = i + 1;
                    v.Pos = i;
                    improved = true;
                }

            if (!improved) break;
        }
    }

    private static (int Keep, int Swap) PairCrossings(LNode u, LNode v)
    {
        int keep = 0, swap = 0;
        foreach (var x in u.In.Select(s => s.From).Concat(u.Out.Select(s => s.To)))
            foreach (var y in v.In.Select(s => s.From).Concat(v.Out.Select(s => s.To)))
            {
                if (x.Layer != y.Layer) continue;
                if (x.Pos > y.Pos) keep++;
                else if (x.Pos < y.Pos) swap++;
            }

        return (keep, swap);
    }

    /// <summary>Croisements entre couches voisines, par inversions comptées avec un arbre indexé (O(n log n) par couche).</summary>
    private static int CountCrossings(List<List<LNode>> layers)
    {
        var total = 0;
        for (var k = 0; k + 1 < layers.Count; k++)
        {
            var size = layers[k + 1].Count;
            var tree = new int[size + 1];
            var seen = 0;
            foreach (var node in layers[k])
                foreach (var target in node.Out.Select(s => s.To.Pos).OrderBy(p => p))
                {
                    var before = 0;
                    for (var i = target + 1; i > 0; i -= i & -i) before += tree[i];
                    total += seen - before;
                    for (var i = target + 1; i <= size; i += i & -i) tree[i]++;
                    seen++;
                }
        }

        return total;
    }

    // --- Hauteurs ---

    private static double Gap(LNode a, LNode b, double rowGap) =>
        a.Dummy && b.Dummy ? DummyGap : a.Dummy || b.Dummy ? MixedGap : rowGap;

    /// <summary>
    ///     Hauteur de chaque table : elle se place face à ses voisines, ligne contre ligne (la colonne qui référence à la
    ///     hauteur de la colonne référencée), par moyennes pondérées successives dans les deux sens ; les tables d'une
    ///     couche restent dans leur ordre sans se chevaucher (<see cref="Place" />).
    /// </summary>
    private static void AssignHeights(List<List<LNode>> layers, double rowGap)
    {
        foreach (var layer in layers)
        {
            var y = 0.0;
            for (var i = 0; i < layer.Count; i++)
            {
                if (i > 0) y += Gap(layer[i - 1], layer[i], rowGap);
                layer[i].Top = y;
                y += layer[i].Height;
            }
        }

        for (var round = 0; round < 18; round++)
        {
            for (var k = 1; k < layers.Count; k++) PlaceLayer(layers[k], 1, rowGap);
            for (var k = layers.Count - 2; k >= 0; k--) PlaceLayer(layers[k], -1, rowGap);
        }

        for (var round = 0; round < 4; round++)
        {
            for (var k = 0; k < layers.Count; k++) PlaceLayer(layers[k], 0, rowGap);
            for (var k = layers.Count - 1; k >= 0; k--) PlaceLayer(layers[k], 0, rowGap);
        }
    }

    /// <param name="side">1 : suit les couches de gauche ; -1 : celles de droite ; 0 : les deux.</param>
    private static void PlaceLayer(List<LNode> layer, int side, double rowGap)
    {
        var target = new double[layer.Count];
        var weight = new double[layer.Count];
        for (var i = 0; i < layer.Count; i++)
        {
            var node = layer[i];
            double sum = 0, total = 0;
            if (side >= 0)
                foreach (var s in node.In)
                {
                    var w = s.Weight * (s.From.Dummy && node.Dummy ? 4 : 1);
                    sum += w * (s.From.Top + s.FromOffset - s.ToOffset);
                    total += w;
                }

            if (side <= 0)
                foreach (var s in node.Out)
                {
                    var w = s.Weight * (s.To.Dummy && node.Dummy ? 4 : 1);
                    sum += w * (s.To.Top + s.ToOffset - s.FromOffset);
                    total += w;
                }

            target[i] = total == 0 ? node.Top : sum / total;
            weight[i] = total == 0 ? 0.01 : total;
        }

        Place(layer, target, weight, rowGap);
    }

    /// <summary>
    ///     Hauteurs les plus proches des cibles (au sens des moindres carrés pondérés) qui gardent l'ordre et l'écart minimal
    ///     entre tables : régression isotone par fusion de blocs voisins (« pool adjacent violators »).
    /// </summary>
    private static void Place(List<LNode> layer, double[] target, double[] weight, double rowGap)
    {
        var n = layer.Count;
        var offset = new double[n];
        for (var i = 1; i < n; i++) offset[i] = offset[i - 1] + layer[i - 1].Height + Gap(layer[i - 1], layer[i], rowGap);

        var sumW = new double[n];
        var sumWZ = new double[n];
        var count = new int[n];
        var top = -1;
        for (var i = 0; i < n; i++)
        {
            top++;
            sumW[top] = weight[i];
            sumWZ[top] = weight[i] * (target[i] - offset[i]);
            count[top] = 1;
            while (top > 0 && sumWZ[top - 1] / sumW[top - 1] > sumWZ[top] / sumW[top])
            {
                sumW[top - 1] += sumW[top];
                sumWZ[top - 1] += sumWZ[top];
                count[top - 1] += count[top];
                top--;
            }
        }

        var index = 0;
        for (var b = 0; b <= top; b++)
        {
            var mean = sumWZ[b] / sumW[b];
            for (var c = 0; c < count[b]; c++, index++) layer[index].Top = mean + offset[index];
        }
    }

    // --- Colonnes ---

    /// <summary>Place les tables en colonnes, centrées dans leur colonne ; rend les bords gauche et droit de chaque colonne.</summary>
    private static (double[] Lefts, double[] Rights) AssignColumns(List<List<LNode>> layers, Placed placed, double columnGap)
    {
        var minTop = layers.SelectMany(l => l).Min(x => x.Top);
        foreach (var node in layers.SelectMany(l => l)) node.Top -= minTop;

        var x = 0.0;
        var lefts = new double[layers.Count];
        var rights = new double[layers.Count];
        for (var k = 0; k < layers.Count; k++)
        {
            var reals = layers[k].Where(n => !n.Dummy).ToList();
            var columnWidth = reals.Max(n => n.Width);
            foreach (var node in reals)
                placed.Nodes.Add((node.Real!, x + (columnWidth - node.Width) / 2, node.Top));
            lefts[k] = x;
            rights[k] = x + columnWidth;
            x += columnWidth + columnGap;
        }

        placed.Width = x - columnGap;
        return (lefts, rights);
    }

    // --- Rangement des composantes ---

    /// <summary>Range les composantes en étagères (les plus grandes d'abord) ; rend la hauteur occupée, marge haute comprise.</summary>
    private static double Pack(List<Placed> placed, Result result)
    {
        var ordered = placed.OrderByDescending(p => p.Width * p.Height).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var area = ordered.Sum(p => (p.Width + ComponentGap) * (p.Height + ComponentGap));
        var limit = Math.Max(ordered.Max(p => p.Width), Math.Sqrt(area) * 1.4);
        double x = 0, y = 0, shelf = 0;
        foreach (var p in ordered)
        {
            if (x > 0 && x + p.Width > limit)
            {
                y += shelf + ComponentGap;
                x = 0;
                shelf = 0;
            }

            var (dx, dy) = (ErDiagram.Margin + x, ErDiagram.Margin + y);
            foreach (var (node, nx, ny) in p.Nodes)
            {
                node.X = dx + nx;
                node.Y = dy + ny;
            }

            foreach (var (edgeId, points) in p.Waypoints)
                result.Waypoints[edgeId] = points.Select(pt => (pt.Left + dx, pt.Right + dx, pt.Y + dy)).ToList();
            x += p.Width + ComponentGap;
            shelf = Math.Max(shelf, p.Height);
        }

        return ErDiagram.Margin + y + shelf;
    }

    /// <summary>Les tables sans relation, en grille sous le graphe.</summary>
    private static void PlaceLoose(List<ErNode> loose, double bottom, double columnGap, double rowGap)
    {
        if (loose.Count == 0) return;
        var perRow = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(loose.Count)));
        var cx = ErDiagram.Margin;
        var cy = bottom;
        var rowHeight = 0.0;
        for (var i = 0; i < loose.Count; i++)
        {
            if (i > 0 && i % perRow == 0)
            {
                cx = ErDiagram.Margin;
                cy += rowHeight + rowGap;
                rowHeight = 0;
            }

            loose[i].X = cx;
            loose[i].Y = cy;
            cx += loose[i].Width + columnGap / 2;
            rowHeight = Math.Max(rowHeight, loose[i].Height);
        }
    }
}
