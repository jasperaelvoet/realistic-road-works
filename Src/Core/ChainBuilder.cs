using System.Collections.Generic;
using Unity.Entities;

namespace RealisticRoadWorks.V3
{
    // Input edge for ChainBuilder: the edge, its two nodes and its curve length.
    public struct ChainEdgeIn
    {
        public Entity Edge, StartNode, EndNode;
        public float Length;
    }

    // One edge placed on a chain: chain coordinates of its curve t=0 (U0) and t=1 (U1).
    public struct ChainEdgeOut
    {
        public Entity Edge;
        public float U0, U1;
    }

    public sealed class Chain
    {
        public readonly List<ChainEdgeOut> Edges = new List<ChainEdgeOut>();
        public float Length;
    }

    // Splits a set of edges (one tool apply / one bulldoze drag / one dev request batch) into chains:
    // a chain is a maximal path; it stops at nodes where the set branches (3+ set edges) and at dead ends.
    // Each chain becomes one project with one crew and one continuous work front. Pure, main thread.
    public static class ChainBuilder
    {
        public static List<Chain> Build(IList<ChainEdgeIn> edges)
        {
            var chains = new List<Chain>();
            int n = edges.Count;
            if (n == 0) return chains;
            var byNode = new Dictionary<Entity, List<int>>();
            void Link(Entity node, int i)
            {
                if (node == Entity.Null) return;
                if (!byNode.TryGetValue(node, out var l)) { l = new List<int>(2); byNode.Add(node, l); }
                l.Add(i);
            }
            for (int i = 0; i < n; i++) { Link(edges[i].StartNode, i); Link(edges[i].EndNode, i); }
            int Degree(Entity node) => node != Entity.Null && byNode.TryGetValue(node, out var l) ? l.Count : 0;
            var used = new bool[n];
            int remaining = n;
            while (remaining > 0)
            {
                // Prefer an unused edge with an end at a dead end / branch of the set (so chains start at ends).
                int first = -1;
                Entity startNode = Entity.Null;
                for (int i = 0; i < n && first < 0; i++)
                {
                    if (used[i]) continue;
                    if (Degree(edges[i].StartNode) != 2) { first = i; startNode = edges[i].StartNode; }
                    else if (Degree(edges[i].EndNode) != 2) { first = i; startNode = edges[i].EndNode; }
                }
                if (first < 0)
                {
                    for (int i = 0; i < n; i++) if (!used[i]) { first = i; startNode = edges[i].StartNode; break; }
                }
                var chain = new Chain();
                float u = 0f;
                int cur = first;
                Entity at = startNode;
                while (cur >= 0)
                {
                    used[cur] = true;
                    remaining--;
                    var e = edges[cur];
                    bool forward = e.StartNode == at || at == Entity.Null;
                    float len = e.Length > 0.01f ? e.Length : 0.01f;
                    chain.Edges.Add(new ChainEdgeOut { Edge = e.Edge, U0 = forward ? u : u + len, U1 = forward ? u + len : u });
                    u += len;
                    Entity next = forward ? e.EndNode : e.StartNode;
                    cur = -1;
                    if (Degree(next) == 2)
                    {
                        foreach (int j in byNode[next])
                            if (!used[j]) { cur = j; break; }
                    }
                    at = next;
                }
                chain.Length = u;
                chains.Add(chain);
            }
            return chains;
        }
    }
}
