using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetSubLane = Game.Net.SubLane;

namespace RealisticRoadWorks.V3.Director
{
    // Nodes, preview adoption and wear. Edge Hidden lives in the edge pass
    // (WorksDirectorSystem.EdgePass) so it is decided in the same loop that writes the runtime.
    //
    // Street furniture is NOT held hidden after the reveal: Game.Objects.SubObjectHiddenSystem (Mod5, HiddenSubObjectJob)
    // removes Hidden from every Object whose Owner is not Hidden, every frame, so a Mod1 Hidden on the sub-objects of a
    // visible edge would be undone before rendering (game code: SubObjectHiddenSystem). The fallback (verified in game) applies:
    // the street lights appear with the road at the reveal.
    public partial class WorksDirectorSystem
    {
        private readonly HashSet<Entity> m_NodeCandidates = new HashSet<Entity>();
        private readonly HashSet<Entity> m_NodeWanted = new HashSet<Entity>();
        private readonly List<Entity> m_NodeTmp = new List<Entity>(32);
        private readonly List<Entity> m_LaneTmp = new List<Entity>(32);

        // ------------------------------------------------------------------ nodes

        // A node is hidden iff every edge connected to it (end or middle connection) is a registry edge with Hidden
        // applied this frame, and the node has no Owner. Asserted every frame; removed only on the transition, and only
        // for nodes whose Hidden we applied (DirectorShared.HiddenNodes).
        private void NodePass()
        {
            var em = EntityManager;
            m_NodeCandidates.Clear();
            m_NodeWanted.Clear();
            for (int i = 0; i < m_Records.Count; i++)
            {
                var rec = m_Records[i];
                if (!rec.HiddenApplied) continue;   // a node is only hidden next to at least one hidden works edge
                if (rec.StartNode != Entity.Null) m_NodeCandidates.Add(rec.StartNode);
                if (rec.EndNode != Entity.Null) m_NodeCandidates.Add(rec.EndNode);
            }
            foreach (var node in m_NodeCandidates)
            {
                if (!NodeWantsHidden(node)) continue;
                m_NodeWanted.Add(node);
                EcsUtil.SetHidden(em, node, true);
                DirectorShared.HiddenNodes.Add(node);
            }
            if (DirectorShared.HiddenNodes.Count == m_NodeWanted.Count) return;
            m_NodeTmp.Clear();
            foreach (var node in DirectorShared.HiddenNodes)
                if (!m_NodeWanted.Contains(node)) m_NodeTmp.Add(node);
            for (int i = 0; i < m_NodeTmp.Count; i++)
            {
                Entity node = m_NodeTmp[i];
                if (EcsUtil.Alive(em, node)) EcsUtil.SetHidden(em, node, false);
                DirectorShared.HiddenNodes.Remove(node);
            }
        }

        private bool NodeWantsHidden(Entity node)
        {
            var em = EntityManager;
            if (!EcsUtil.Alive(em, node) || em.HasComponent<Owner>(node) || em.HasComponent<Temp>(node)) return false;
            if (!em.HasBuffer<ConnectedEdge>(node)) return false;
            var buf = em.GetBuffer<ConnectedEdge>(node, true);
            int n = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                Entity ce = buf[i].m_Edge;
                if (ce == Entity.Null || !em.Exists(ce) || em.HasComponent<Deleted>(ce)) continue;
                if (!SiteRegistry.TryGetEdge(ce, out var r) || !r.HiddenApplied) return false;
                n++;
            }
            return n > 0;
        }

        // ------------------------------------------------------------------ preview adoption

        // PreviewHide (Mod2B) marks Temp copies of hidden works roads with TempFlags.Hidden (never the Hidden
        // component). ApplyNetSystem turns a Create temp into a permanent entity by removing Temp only; Tools adds the Hidden
        // component to a split remnant of a hidden works edge right before ApplyNetSystem, so it is hidden in the
        // apply frame. Adoption is keyed on SET MEMBERSHIP (not on the Hidden component): edges with a site were adopted
        // in RegisterSite (HiddenApplied; the edge pass asserts Hidden this frame); permanent nodes we hid become node-pass
        // candidates (it hides or releases them in this same frame); anything else that carries Hidden is un-hidden.
        private void AdoptPreviewHidden()
        {
            var set = DirectorShared.PreviewHidden;
            if (set.Count == 0) return;
            var em = EntityManager;
            m_NodeTmp.Clear();
            foreach (var e in set) m_NodeTmp.Add(e);
            for (int i = 0; i < m_NodeTmp.Count; i++)
            {
                Entity e = m_NodeTmp[i];
                if (!em.Exists(e) || em.HasComponent<Deleted>(e)) { set.Remove(e); continue; }
                if (em.HasComponent<Temp>(e)) continue;   // still a preview
                set.Remove(e);
                if (em.HasComponent<Edge>(e))
                {
                    if (SiteRegistry.TryGetEdge(e, out var rec))
                    {
                        rec.HiddenApplied = true;   // the edge pass keeps Hidden (asserts it this frame) or removes it once
                        DirectorShared.PreviewAdopted++;
                    }
                    else if (em.HasComponent<Hidden>(e))
                    {
                        EcsUtil.SetHidden(em, e, false);
                        DirectorShared.PreviewReleased++;
                        RRWLog.Once("director-preview-release", "director: released Hidden on a permanent edge created from a hidden preview (no site was tagged on it)");
                    }
                }
                else if (em.HasComponent<Game.Net.Node>(e))
                {
                    DirectorShared.HiddenNodes.Add(e);   // node pass keeps / makes it hidden or removes Hidden this frame
                    DirectorShared.PreviewAdopted++;
                }
                else if (em.HasComponent<Hidden>(e))
                {
                    EcsUtil.SetHidden(em, e, false);
                    DirectorShared.PreviewReleased++;
                }
            }
            m_NodeTmp.Clear();
        }

        // ------------------------------------------------------------------ wear

        // Every kWearCheckInterval updates per edge (staggered by entity index): compare PhasePlan.Wear with the current
        // LaneCondition (car lanes) and NetCondition and rewrite any difference (vanilla maintenance may repair it).
        private void StepWear()
        {
            if (!RRWDebug.On(DebugLayers.Wear)) return;
            var em = EntityManager;
            uint mask = (uint)RRWConst.kWearCheckInterval - 1u;
            for (int i = 0; i < m_Records.Count; i++)
            {
                var rec = m_Records[i];
                Entity e = rec.Edge;
                if ((((uint)e.Index + m_Now) & mask) != 0) continue;
                if (!em.Exists(e) || em.HasComponent<Deleted>(e) || !em.HasComponent<RoadWorksSite>(e) || !em.HasComponent<RoadWorksRuntime>(e)) continue;
                var site = em.GetComponentData<RoadWorksSite>(e);
                var rt = em.GetComponentData<RoadWorksRuntime>(e);
                float target = PhasePlan.Wear(PlanInput.From(site, rt));
                if (target < 0f) continue;
                SetWear(e, target);
            }
        }

        // Writes `wear` where it differs. Returns the number of components rewritten.
        internal int SetWear(Entity edge, float wear)
        {
            var em = EntityManager;
            int n = 0;
            if (em.HasBuffer<NetSubLane>(edge))
            {
                EcsUtil.SubLanes(em, edge, m_LaneTmp);
                for (int i = 0; i < m_LaneTmp.Count; i++)
                {
                    Entity l = m_LaneTmp[i];
                    if (!em.Exists(l) || !em.HasComponent<LaneCondition>(l) || !em.HasComponent<NetCarLane>(l)) continue;
                    var lc = em.GetComponentData<LaneCondition>(l);
                    if (math.abs(lc.m_Wear - wear) < 0.01f) continue;
                    lc.m_Wear = wear;
                    em.SetComponentData(l, lc);
                    n++;
                }
                m_LaneTmp.Clear();
            }
            if (em.HasComponent<NetCondition>(edge))
            {
                var nc = em.GetComponentData<NetCondition>(edge);
                if (math.any(math.abs(nc.m_Wear - wear) >= 0.01f))
                {
                    nc.m_Wear = new float2(wear);
                    em.SetComponentData(edge, nc);
                    n++;
                }
            }
            return n;
        }
    }
}
