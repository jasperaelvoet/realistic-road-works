using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Director
{
    // Deferred upgrades (DeferredNet): the works of a project whose roads keep their old state build in the frame of the target
    // curve (RefreshGeometry), cut their sub-strips from the stored target layout, let traffic plan on the old lanes, and ask for
    // the target state when the re-marking window starts (or at the completion, which waits for it).
    public partial class WorksDirectorSystem
    {
        private EntityQuery m_DeferredQuery;
        private readonly HashSet<uint> m_DeferredProjects = new HashSet<uint>();
        private readonly List<LaneProbe> m_DeferProbes = new List<LaneProbe>(16);
        private uint m_DeferOrphanCheck;
        private readonly HashSet<uint> m_DeferMissing = new HashSet<uint>();

        private EntityQuery DeferredQuery()
        {
            if (m_DeferredQuery == default)
                m_DeferredQuery = GetEntityQuery(ComponentType.ReadOnly<RRWDeferredNet>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            return m_DeferredQuery;
        }

        // Projects with deferred entities this frame; drops the targets of projects that no longer exist (bulldozed works: the
        // old road stays as it is).
        private void RefreshDeferred()
        {
            m_DeferredProjects.Clear();
            var q = DeferredQuery();
            if (q.IsEmptyIgnoreFilter) return;
            var arr = q.ToComponentDataArray<RRWDeferredNet>(Allocator.Temp);
            for (int i = 0; i < arr.Length; i++) m_DeferredProjects.Add(arr[i].m_ProjectId);
            arr.Dispose();
            if (!SiteRegistry.Loaded || m_Now < m_DeferOrphanCheck) return;
            m_DeferOrphanCheck = m_Now + 32;
            // a project must be missing on two checks in a row (a new one registers a frame after its apply)
            foreach (var id in m_DeferredProjects)
            {
                if (SiteRegistry.Projects.ContainsKey(id)) { m_DeferMissing.Remove(id); continue; }
                if (!m_DeferMissing.Add(id) && DeferredNet.DropRequests.Add(id))
                {
                    m_DeferMissing.Remove(id);
                    RRWLog.Once("defer-drop-" + id, "director: project #" + id + " is gone with its roads still deferred: they keep the old road");
                }
            }
        }

        private bool ProjectDeferred(uint id) => m_DeferredProjects.Contains(id);

        // Works frame of an edge: the target curve while deferred.
        private Bezier4x3 WorksCurve(Entity e, out float length, out bool deferred)
        {
            var em = EntityManager;
            deferred = DeferredNet.TryEdge(em, e, out var d);
            if (deferred) { length = MathUtils.Length(d.m_Curve); return d.m_Curve; }
            var c = em.GetComponentData<Curve>(e);
            length = c.m_Length;
            return c.m_Bezier;
        }

        // Composition width the works use: the target layout's outline while deferred.
        private float WorksWidth(Entity e)
        {
            var em = EntityManager;
            if (DeferredNet.TryEdge(em, e, out var d) && d.m_Strips.Length > 0 && d.m_OuterR > d.m_OuterL) return d.m_OuterR - d.m_OuterL;
            return EcsUtil.CompositionWidth(em, e);
        }

        // Per edge of an upgrade project (UpgradeUpdate): the deferred flag, the target layout and the old lanes for traffic.
        private void SyncDeferredEdge(EdgeRecord rec, DirEdgeState st, UpgradeEdgeState eu)
        {
            var em = EntityManager;
            bool deferred = DeferredNet.TryEdge(em, rec.Edge, out var d);
            if (eu.Deferred != deferred)
            {
                eu.Deferred = deferred;
                st.UwLayoutTried = int.MinValue;   // re-read the layout (stored target or live road)
                st.UwDeferLanesKey = 0;
            }
            if (!deferred || rec.Arc == null) return;
            uint key = (uint)rec.GeometryRevision * 2654435761u ^ st.LaneSig ^ 0x9E3779B9u;
            if (key == st.UwDeferLanesKey) return;
            st.UwDeferLanesKey = key;
            EcsUtil.ProbeLanes(em, rec.Edge, rec.Arc, m_DeferProbes, null);
            eu.SetTrafficLanes(m_DeferProbes);
            RRWLog.Verbose("director: deferred edge " + RRWLog.E(rec.Edge) + ": traffic plans on " + eu.TrafficSection.Count + " old lane strip(s)");
        }

        // The stored target layout of a deferred edge (false when none).
        private bool DeferredLayout(EdgeRecord rec, CompositionLayout into)
        {
            return DeferredNet.TryEdge(EntityManager, rec.Edge, out var d) && d.GetLayout(into);
        }

        // Project level, every update: the runtime's Deferred flag and the switch when the re-marking window starts.
        private void SyncDeferredProject(ProjectRecord proj, DirProjectState ps, int layoutWindow, int remarkWindow)
        {
            var rt = proj.Upgrade;
            bool deferred = ProjectDeferred(proj.Id);
            if (rt != null && rt.Deferred != deferred) { rt.Deferred = deferred; proj.Revision++; }
            if (!deferred) return;
            if (remarkWindow >= 0 && layoutWindow >= remarkWindow && DeferredNet.SwitchRequests.Add(proj.Id))
                RRWLog.Once("defer-switch-remark-" + proj.Id, "director: upgrade project #" + proj.Id + " reached its re-marking window: the roads switch to the upgraded road");
        }

        // Completion of a construction project: the deferred target goes in first; true while it is still pending.
        private bool AwaitDeferredSwitch(ProjectRecord proj)
        {
            if (!ProjectDeferred(proj.Id)) return false;
            if (DeferredNet.SwitchRequests.Add(proj.Id))
                RRWLog.Once("defer-switch-done-" + proj.Id, "director: upgrade project #" + proj.Id + " finished: the roads switch to the upgraded road before the site goes");
            return true;
        }
    }
}
