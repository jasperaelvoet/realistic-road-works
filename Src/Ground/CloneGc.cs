using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Unity.Collections;
using Unity.Entities;

namespace RealisticRoadWorks.V3.Ground
{
    // Clone lifecycle outside the per-edge state machine (ported from the terrain prototype):
    // - Collect: destroys queued clones once they are >= 2 updates old AND nothing references them. Burst jobs index
    //   NetCompositionData through Composition / Orphan without HasComponent checks (TerrainSystem), so a
    //   referenced composition must never be destroyed. While anything due is still referenced, back off 15 updates
    //   (the reference scan copies every Composition in the city).
    // - LoadFixup: clone entities survive loads (ClearSystem keeps NetCompositionData entities); after a load nothing
    //   should reference them (Composition is IEmptySerializable: CompositionSelectSystem re-selects vanilla).
    // - Sweep: finds clones no state owns (leaks, orphan edges after a lost state) and hands referencing edges back.
    // Only main thread, only at ModificationEnd before TerrainSystem (structural changes allowed there).
    public sealed class CloneGc
    {
        private readonly EntityQuery m_CompositionRefs, m_OrphanRefs, m_Clones;
        private readonly HashSet<Entity> m_Due = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Referenced = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Owned = new HashSet<Entity>();
        private readonly List<Entity> m_Tmp = new List<Entity>();
        private uint m_NextCollect;
        public const int kBackoffUpdates = 15;
        public const int kMinAgeUpdates = 2;

        public CloneGc(EntityQuery compositionRefs, EntityQuery orphanRefs, EntityQuery clones)
        {
            m_CompositionRefs = compositionRefs;
            m_OrphanRefs = orphanRefs;
            m_Clones = clones;
        }

        public int CloneCount => m_Clones.CalculateEntityCount();

        public void Reset() { m_NextCollect = 0; }

        // Fills m_Referenced with the members of `candidates` that any Composition or Orphan references.
        private void ScanReferenced(HashSet<Entity> candidates)
        {
            m_Referenced.Clear();
            if (candidates.Count == 0) return;
            var comps = m_CompositionRefs.ToComponentDataArray<Composition>(Allocator.Temp);
            try
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (candidates.Contains(c.m_Edge)) m_Referenced.Add(c.m_Edge);
                    if (candidates.Contains(c.m_StartNode)) m_Referenced.Add(c.m_StartNode);
                    if (candidates.Contains(c.m_EndNode)) m_Referenced.Add(c.m_EndNode);
                }
            }
            finally { comps.Dispose(); }
            var orphans = m_OrphanRefs.ToComponentDataArray<Orphan>(Allocator.Temp);
            try
            {
                for (int i = 0; i < orphans.Length; i++)
                    if (candidates.Contains(orphans[i].m_Composition)) m_Referenced.Add(orphans[i].m_Composition);
            }
            finally { orphans.Dispose(); }
        }

        private void BuildOwned()
        {
            m_Owned.Clear();
            foreach (var s in GroundTable.Edges.Values)
                for (int k = 0; k < 3; k++)
                    if (s.Clone[k] != Entity.Null) m_Owned.Add(s.Clone[k]);
        }

        public void Collect(EntityManager em, uint now)
        {
            if (GroundTable.PendingDestroy.Count == 0) return;
            if (unchecked((int)(now - m_NextCollect)) < 0) return;
            m_Due.Clear();
            foreach (var kv in GroundTable.PendingDestroy)
                if (unchecked(now - kv.Value) >= kMinAgeUpdates) m_Due.Add(kv.Key);
            if (m_Due.Count == 0) return;
            BuildOwned();
            m_Tmp.Clear();
            // revive clones a state owns again (requeued by mistake / adopted back), drop dead ids
            foreach (var e in m_Due)
            {
                if (m_Owned.Contains(e) || !em.Exists(e) || !em.HasComponent<RRWCompositionClone>(e)) m_Tmp.Add(e);
            }
            for (int i = 0; i < m_Tmp.Count; i++) { m_Due.Remove(m_Tmp[i]); GroundTable.PendingDestroy.Remove(m_Tmp[i]); }
            if (m_Due.Count == 0) return;
            ScanReferenced(m_Due);
            int destroyed = 0, kept = 0;
            foreach (var e in m_Due)
            {
                if (m_Referenced.Contains(e)) { kept++; continue; }
                em.DestroyEntity(e);
                destroyed++;
                GroundTable.PendingDestroy.Remove(e);
            }
            GroundTable.ClonesDestroyed += destroyed;
            if (kept > 0) m_NextCollect = now + kBackoffUpdates;
            if (destroyed > 0 || kept > 0)
                RRWLog.Verbose("ground gc destroyed=" + destroyed + " stillReferenced=" + kept + " pending=" + GroundTable.PendingDestroy.Count);
        }

        // First ModEnd after a load: destroy every clone nothing references; queue the referenced ones (states that adopt
        // them revive them; otherwise they die as soon as the reference goes).
        public void LoadFixup(EntityManager em, uint now)
        {
            GroundTable.PendingDestroy.Clear();
            m_Due.Clear();
            var arr = m_Clones.ToEntityArray(Allocator.Temp);
            try { for (int i = 0; i < arr.Length; i++) m_Due.Add(arr[i]); }
            finally { arr.Dispose(); }
            if (m_Due.Count == 0) { RRWLog.Info("ground loadfixup: no clones in memory"); return; }
            ScanReferenced(m_Due);
            int destroyed = 0, referenced = 0;
            foreach (var e in m_Due)
            {
                if (m_Referenced.Contains(e)) { referenced++; GroundTable.QueueDestroy(e, now); continue; }
                if (em.Exists(e)) { em.DestroyEntity(e); destroyed++; }
            }
            GroundTable.ClonesDestroyed += destroyed;
            RRWLog.Info("ground loadfixup: clones found=" + m_Due.Count + " destroyed=" + destroyed + " stillReferenced=" + referenced);
        }

        // Clones that no state owns and that are not queued: queue them, and report live edges that still reference
        // one of them through their own Composition (the clone's m_Edge), so the system can run the hold -> restore.
        public void Sweep(EntityManager em, uint now, List<Entity> edgesReferencingClones)
        {
            edgesReferencingClones.Clear();
            BuildOwned();
            var arr = m_Clones.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Entity c = arr[i];
                    if (m_Owned.Contains(c) || GroundTable.PendingDestroy.ContainsKey(c)) continue;
                    var tag = em.GetComponentData<RRWCompositionClone>(c);
                    Entity edge = tag.m_Edge;
                    if (!GroundTable.Edges.ContainsKey(edge) && EcsUtil.Alive(em, edge) && em.HasComponent<Composition>(edge))
                    {
                        var comp = em.GetComponentData<Composition>(edge);
                        if (comp.m_Edge == c || comp.m_StartNode == c || comp.m_EndNode == c)
                        {
                            if (!edgesReferencingClones.Contains(edge)) edgesReferencingClones.Add(edge);
                            continue;
                        }
                    }
                    GroundTable.QueueDestroy(c, now);
                }
            }
            finally { arr.Dispose(); }
        }

        // rrw.check helper: clones referenced by a live edge that is not a works edge for longer than allowed.
        public int CountUnowned(EntityManager em)
        {
            BuildOwned();
            int n = 0;
            var arr = m_Clones.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                    if (!m_Owned.Contains(arr[i]) && !GroundTable.PendingDestroy.ContainsKey(arr[i])) n++;
            }
            finally { arr.Dispose(); }
            return n;
        }
    }
}
