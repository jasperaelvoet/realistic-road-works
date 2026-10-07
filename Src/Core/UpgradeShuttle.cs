using System.Collections.Generic;
using Game.Net;
using Unity.Entities;

namespace RealisticRoadWorks.V3
{
    // Where one-lane alternating operation can run: a single chain of edges up to RRWConst.kUwShuttleMaxLength whose two end
    // nodes have no traffic lights of their own (the game's lights would overrule the portable ones).
    public static class UpgradeShuttle
    {
        private static readonly Dictionary<Entity, int> s_Count = new Dictionary<Entity, int>(8);

        // Live state per project (Traffic writes, Props shows it on the portable signal heads): the end node whose entering
        // traffic has green now (Entity.Null = all red). A project without an entry has no one-lane operation.
        public static readonly Dictionary<uint, Entity> GreenAt = new Dictionary<uint, Entity>();
        private static readonly Dictionary<Entity, Entity> s_EdgeAt = new Dictionary<Entity, Entity>(8);

        public static bool SignalsPossible(EntityManager em, IList<Entity> edges)
        {
            if (!RRWGates.UpgradeDrop) return false;
            if (!ChainEnds(em, edges, out var a, out _, out var b, out _)) return false;
            float length = 0f;
            for (int i = 0; i < edges.Count; i++)
                if (em.HasComponent<Curve>(edges[i])) length += em.GetComponentData<Curve>(edges[i]).m_Length;
            return length <= RRWConst.kUwShuttleMaxLength && !em.HasComponent<TrafficLights>(a) && !em.HasComponent<TrafficLights>(b);
        }

        // The two nodes that only one edge of the chain touches, and that edge. False unless the edges form one open chain.
        public static bool ChainEnds(EntityManager em, IList<Entity> edges, out Entity a, out Entity edgeA, out Entity b, out Entity edgeB)
        {
            a = b = edgeA = edgeB = Entity.Null;
            if (edges == null || edges.Count == 0) return false;
            s_Count.Clear();
            s_EdgeAt.Clear();
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                if (!em.Exists(e) || !em.HasComponent<Edge>(e)) return false;
                var ed = em.GetComponentData<Edge>(e);
                Count(ed.m_Start, e);
                Count(ed.m_End, e);
            }
            foreach (var kv in s_Count)
            {
                if (kv.Value == 2) continue;
                if (kv.Value != 1) return false;
                if (a == Entity.Null) { a = kv.Key; edgeA = s_EdgeAt[kv.Key]; }
                else if (b == Entity.Null) { b = kv.Key; edgeB = s_EdgeAt[kv.Key]; }
                else return false;
            }
            return a != Entity.Null && b != Entity.Null;
        }

        private static void Count(Entity node, Entity edge)
        {
            s_Count[node] = s_Count.TryGetValue(node, out int n) ? n + 1 : 1;
            s_EdgeAt[node] = edge;
        }
    }
}
