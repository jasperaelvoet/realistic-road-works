using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.SceneFlow;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticRoadWorks.V3.Tooling
{
    // Helpers shared by WorksTagSystem and BulldozeInterceptSystem: temp classification, chain-coordinate
    // mapping of split / combined / replaced pieces onto the original arcs (CurveMatch / EdgeArc.ProjectExtended),
    // and project-id safety. Main thread only.
    internal static class ToolUtil
    {
        public static bool InGame()
        {
            var gm = GameManager.instance;
            return gm != null && gm.gameMode == GameMode.Game;
        }

        public static bool Has(TempFlags flags, TempFlags f) => (flags & f) != 0;

        public static float CurveLength(EntityManager em, Entity e) =>
            em.HasComponent<Curve>(e) ? math.max(0.01f, em.GetComponentData<Curve>(e).m_Length) : 0.01f;

        // A node (or the original a Temp node stands for) on the map border: tagging such edges would break
        // the outside connection's traffic, so they are skipped.
        public static bool IsOutsideConnectionNode(EntityManager em, Entity node)
        {
            if (node == Entity.Null || !em.Exists(node)) return false;
            if (em.HasComponent<OutsideConnection>(node)) return true;
            if (em.HasComponent<Temp>(node))
            {
                Entity orig = em.GetComponentData<Temp>(node).m_Original;
                if (orig != Entity.Null && em.Exists(orig) && em.HasComponent<OutsideConnection>(orig)) return true;
            }
            return false;
        }

        public static bool TouchesOutsideConnection(EntityManager em, Entity edge)
        {
            if (!em.HasComponent<Edge>(edge)) return false;
            var e = em.GetComponentData<Edge>(edge);
            return IsOutsideConnectionNode(em, e.m_Start) || IsOutsideConnectionNode(em, e.m_End);
        }

        // Buildings served by the road (Replace temps: dependants => SlowZone => mode D; demolitions: informational flag).
        public static bool HasDependants(EntityManager em, Entity edge)
        {
            if (edge == Entity.Null || !em.Exists(edge) || !em.HasBuffer<ConnectedBuilding>(edge)) return false;
            return em.GetBuffer<ConnectedBuilding>(edge, true).Length > 0;
        }

        public static SiteFactoryEdge FactoryEdge(EntityManager em, Entity edge, int paidCost, bool dependants)
        {
            var fe = new SiteFactoryEdge { Edge = edge, Length = CurveLength(em, edge), PaidCost = math.max(0, paidCost), Dependants = dependants };
            if (em.HasComponent<Edge>(edge))
            {
                var e = em.GetComponentData<Edge>(edge);
                fe.StartNode = e.m_Start;
                fe.EndNode = e.m_End;
            }
            fe.DigEligible = EcsUtil.DigEligible(em, edge, out _);
            return fe;
        }

        // Same, for a temp whose site belongs on another entity (target: the kept original of a Modify / Upgrade temp;
        // Entity.Null = the temp itself). The chain is built from the temps, the site is written on the target.
        public static SiteFactoryEdge FactoryEdge(EntityManager em, Entity edge, int paidCost, bool dependants, Entity target)
        {
            var fe = FactoryEdge(em, edge, paidCost, dependants);
            fe.Target = target;
            return fe;
        }

        // Unbuilt share of what a works edge has paid, credited to the city (mode H parts that end with a combine).
        public static int RefundUnbuilt(EntityManager em, Entity city, in RoadWorksSite site)
        {
            int amount = WorkTime.CancelRefund(math.max(0, site.m_PaidCost), site.Progress, false);
            EcsUtil.Credit(em, city, amount);
            return amount;
        }

        // Chain coordinate of a world point on a source edge's arc (beyond the ends: linear extension, see WorksTagSystem rule 1).
        public static float ChainUOf(EdgeArc arc, in RoadWorksSite src, float3 p) =>
            PhasePlan.ChainU(arc.ProjectExtended(p), src.m_ChainU0, src.m_ChainU1, arc.Length);

        // Maps a piece (split remnant / replaced copy) that lies on one source edge into the source's chain range.
        // The result keeps the piece's own orientation (u0 = its curve start). Clamped to the source range, so two
        // remnants of one split meet exactly at the projected split point.
        public static void MapOnto(EdgeArc arc, in RoadWorksSite src, Bezier4x3 piece, out float u0, out float u1)
        {
            float lo = src.ChainLo, hi = src.ChainHi;
            u0 = math.clamp(ChainUOf(arc, src, piece.a), lo, hi);
            u1 = math.clamp(ChainUOf(arc, src, piece.d), lo, hi);
            if (math.abs(u1 - u0) < 0.01f)
            {
                // degenerate projection (should not happen): keep the source orientation over a minimal range
                bool fwd = src.m_ChainU1 >= src.m_ChainU0;
                u1 = fwd ? math.min(hi, u0 + 0.01f) : math.max(lo, u0 - 0.01f);
            }
        }

        // NextProjectId must exceed every saved id before SiteFactory allocates. The Director raises it in its
        // first-frame rebuild; a tool apply (or a migration) before that rebuild scans the saved sites itself.
        // Delegates to Core: raises NextProjectId above every saved id while the registry is not rebuilt yet.
        public static void EnsureProjectIds(EntityManager em, EntityQuery siteQuery) => SiteRegistry.EnsureIdsFromSaved(siteQuery);

        // Writes a RoadWorksSite on an entity (add, or overwrite when one is already there).
        public static void PutSite(EntityManager em, Entity e, in RoadWorksSite site)
        {
            if (em.HasComponent<RoadWorksSite>(e)) em.SetComponentData(e, site);
            else em.AddComponentData(e, site);
        }

        // Natural-ground estimate of a merged edge: the max over the sources that have a valid sample, else unsampled.
        public static void MergeNatural(ref RoadWorksSite target, List<RoadWorksSite> sources)
        {
            float above = float.NaN, below = float.NaN;
            for (int i = 0; i < sources.Count; i++)
            {
                if (!sources[i].NaturalValid) continue;
                above = float.IsNaN(above) ? sources[i].m_NatAbove : math.max(above, sources[i].m_NatAbove);
                below = float.IsNaN(below) ? sources[i].m_NatBelow : math.max(below, sources[i].m_NatBelow);
            }
            target.m_NatAbove = above;
            target.m_NatBelow = below;
            target.Set(SiteFlags.NaturalSampled, !float.IsNaN(above) && !float.IsNaN(below));
        }

        public static string Summary(int a, string name) => a > 0 ? " " + name + "=" + a : "";
    }
}
